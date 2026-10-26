// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Block (lobby room) selection and the player roster inside a block.
/// </summary>
public sealed class LobbyHandlers(
    LobbyCatalog lobbies,
    IPlayerRepository players,
    ILogger<LobbyHandlers> log)
{
    private const ServiceRole LobbyRoles = ServiceRole.Lobby | ServiceRole.FdLobby | ServiceRole.Menu;

    [Command("CMD_GET_BLOCKLIST", Roles = LobbyRoles | ServiceRole.Account)]
    public ValueTask<KvMessage[]> GetBlockList(CommandContext ctx)
    {
        var entries = lobbies.Lobbies.Select(l => new KvMessage()
            .Set("name", l.Name)
            .Set("player_num", ctx.Hub.SessionsInBlock(l.Id).Count)
            .Set("max_player_num", l.MaxPlayers)).ToArray();

        return Reply.Of(ctx.Ok().SetList("count", "bklist", entries));
    }

    [Command("CMD_JOIN_BLOCK", Roles = LobbyRoles, RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> JoinBlock(CommandContext ctx)
    {
        var session = ctx.Session;
        var previousBlockId = session.BlockId;

        // The client reports both its NAT-visible and LAN endpoints here. Peers need
        // both to set up the direct connection for a match.
        session.ExternalIp = ctx.Request.GetString("ex_ip") ?? session.ExternalIp;
        session.ExternalPort = ctx.Request.GetInt32("ex_port", session.ExternalPort);
        session.InternalIp = ctx.Request.GetString("in_ip") ?? session.InternalIp;
        session.InternalPort = ctx.Request.GetInt32("in_port", session.InternalPort);

        // The client picks a block by its position in the list it was just given, so
        // "index" is the authoritative selector; the name is only a fallback.
        var lobby =
            (ctx.Request.Has("index") ? lobbies.ByIndex(ctx.Request.GetInt32("index", -1)) : null)
            ?? (ctx.Request.GetString("name") is { } name ? lobbies.ByName(name) : null)
            ?? lobbies.Lobbies.FirstOrDefault();

        if (lobby is null)
        {
            log.LogWarning("CMD_JOIN_BLOCK but no lobbies are configured");
            return [ctx.Err("NOBLOCK")];
        }

        // Leaving the old block has to reach the peers there, not the ones being joined.
        if (previousBlockId is not null && previousBlockId != lobby.Id)
            ctx.Hub.PublishBlockPlayerRemoved(session);

        session.BlockId = lobby.Id;
        if (session.State < SessionState.InBlock)
            session.State = SessionState.InBlock;

        log.LogInformation(
            "pid {Pid} joined block {Block} (index {Index}, id {Id}); " +
            "endpoints external={ExIp}:{ExPort} internal={InIp}:{InPort}",
            session.Pid, lobby.Name, lobby.Index, lobby.Id,
            session.ExternalIp, session.ExternalPort, session.InternalIp, session.InternalPort);

        // Everyone already here cached their roster before this player existed to them,
        // and nothing else would ever tell them about it.
        var player = session.Pid > 0
            ? await players.GetAsync(session.Pid, ctx.CancellationToken)
            : null;
        if (player is not null)
        {
            var entry = PlayerPresenter.BlockListEntry(player, session.RoomId ?? 0, ctx.Profile);
            ctx.Hub.PublishBlockPlayerAdded(session, entry);
        }

        return [ctx.Ok()];
    }

    [Command("MSG_REQBLOCKPLAYERLIST", Roles = LobbyRoles, RequiredState = SessionState.InBlock)]
    public async ValueTask<KvMessage[]> RequestBlockPlayerList(CommandContext ctx)
    {
        var blockId = ctx.Session.BlockId ?? 0;
        var presences = ctx.Hub.SessionsInBlock(blockId)
            .Where(s => s.Role == ctx.Session.Role && s.Pid > 0)
            .GroupBy(s => s.Pid)
            .Select(group => group
                .OrderByDescending(s => s.RoomId.HasValue)
                .First())
            .ToArray();

        var entries = new List<KvMessage>(presences.Length);
        foreach (var presence in presences)
        {
            var player = await players.GetAsync(presence.Pid, ctx.CancellationToken);
            if (player is not null)
                entries.Add(PlayerPresenter.BlockListEntry(player, presence.RoomId ?? 0, ctx.Profile));
        }

        // Request ack followed by START / DATA / END. All four are one logical response,
        // so they are one handler rather than a separate follow-up code path.
        return
        [
            ctx.Ok(),
            ctx.Ok("MSG_BLOCKPLAYERLISTSTART"),
            ctx.Ok("MSG_BLOCKPLAYERLISTDATA")
                .SetList("block_player_num", "block_player_list", entries),
            ctx.Ok("MSG_BLOCKPLAYERLISTEND"),
        ];
    }
}
