// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data.Entities;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>Player discovery and the lightweight online-status commands used by menus.</summary>
public sealed class PlayerDirectoryHandlers(
    IPlayerRepository players,
    LobbyCatalog lobbies,
    ServerCatalog servers,
    ProtocolTextPolicy textPolicy,
    IOptionsMonitor<ServerOptions> options,
    ILogger<PlayerDirectoryHandlers> log)
{
    private const ServiceRole DirectoryRoles =
        ServiceRole.Account | ServiceRole.Menu | ServiceRole.Lobby | ServiceRole.FdLobby;

    [Command("CMD_GET_PLAYERNUMBERS", Roles = DirectoryRoles,
        RequiredState = SessionState.Authenticated)]
    public ValueTask<KvMessage[]> GetPlayerNumbers(CommandContext ctx)
    {
        // This exact four-field response is consumed by PES2010 FUN_0072A100.
        return Reply.Of(ctx.Ok()
            .Set("network_player_num", ctx.Hub.ConnectedPlayerCount())
            .Set("compe_player_num", 0)
            .Set("commu_player_num", 0)
            .Set("legends_player_num", 0));
    }

    [Command("CMD_SEARCH_PLAYER", Roles = DirectoryRoles,
        RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> SearchPlayer(CommandContext ctx)
    {
        // PES2010 FUN_0072DC10 recognizes FORWARD, PART and PERFECT and then reads
        // count plus individually optional list[i] presence fields.
        if (!TryParseSearchMode(ctx.Request.GetString("option"), out var mode))
        {
            log.LogWarning("CMD_SEARCH_PLAYER with unsupported option {Option}",
                ctx.Request.GetString("option"));
            return [ctx.Ok().SetList("count", "list", Array.Empty<KvMessage>())];
        }

        var query = ctx.Request.GetString("pname")?.Trim() ?? "";
        var found = await players.SearchAsync(
            mode,
            query,
            options.CurrentValue.Protocol.PlayerSearchLimit,
            ctx.CancellationToken);

        var entries = found.Select(player => PresentSearchResult(ctx.Hub, player, ctx.Profile)).ToArray();
        return [ctx.Ok().SetList("count", "list", entries)];
    }

    [Command("CMD_CHECK_STRING", Roles = ServiceRole.All,
        RequiredState = SessionState.Authenticated)]
    public ValueTask<KvMessage[]> CheckString(CommandContext ctx)
    {
        // FUN_007AC550 and FUN_007C7E80 accept a bare NOERR or ERR_INVALIDLETTER.
        return textPolicy.TryValidate(ctx.Request.GetString("str"), out _)
            ? Reply.Of(ctx.Ok())
            : Reply.Of(ctx.Fail("ERR_INVALIDLETTER"));
    }

    private KvMessage PresentSearchResult(Hub hub, Player player, GameProfile profile)
    {
        var entry = new KvMessage()
            .Set("pid", player.Pid)
            .Set("name", player.Name)
            .Set("division", RankSystem.Label(player.Division, profile))
            .Set("rating", player.Rating)
            .Set("manner", player.Manner);

        var presence = hub.SessionsForPid(player.Pid)
            .Where(s => s.State >= SessionState.PlayerSelected)
            .OrderByDescending(s => s.State)
            .ThenByDescending(s => PresenceRolePriority(s.Role))
            .ThenByDescending(s => s.ConnectedAt)
            .FirstOrDefault();
        if (presence is null)
            return entry;

        var endpoint = servers.Endpoints.FirstOrDefault(e => e.Role == presence.Role);
        if (endpoint is not null)
        {
            entry.Set("svrgid", endpoint.Gid)
                .Set("lobby_name", endpoint.Name)
                .Set("svrtype", presence.Role.ToSvrType());
        }

        if (presence.BlockId is { } blockId)
        {
            entry.Set("block", blockId);
            if (lobbies.ById(blockId) is { } lobby)
                entry.Set("block_name", lobby.Name);
        }

        if (presence.RoomId is { } roomId && hub.FindRoom(roomId) is { } room)
            entry.Set("room_id", room.Id).Set("room_name", room.Name);

        entry.Set("inmatch", presence.State >= SessionState.InMatch);
        return entry;
    }

    private static bool TryParseSearchMode(string? option, out PlayerSearchMode mode)
    {
        mode = option?.ToUpperInvariant() switch
        {
            "FORWARD" => PlayerSearchMode.Forward,
            "PART" => PlayerSearchMode.Part,
            "PERFECT" => PlayerSearchMode.Perfect,
            _ => (PlayerSearchMode)(-1),
        };
        return Enum.IsDefined(mode);
    }

    private static int PresenceRolePriority(ServiceRole role) => role switch
    {
        ServiceRole.Lobby => 3,
        ServiceRole.FdLobby => 2,
        ServiceRole.Menu => 1,
        _ => 0,
    };
}
