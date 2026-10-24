// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Friends and blacklist. A friendship is two rows (one per player), so each side
/// sees its own state: a request is SentReq for the sender and RecievedReq for the target
/// until it is answered, then Friend on both.
/// </summary>
public sealed class FriendHandlers(GameDbContext db, LobbyCatalog lobbies)
{
    private const ServiceRole Roles = ServiceRole.Menu | ServiceRole.Account;

    /// <summary>Friends plus pending requests one player can have, and blacklist size.</summary>
    public const int MaxFriends = 50, MaxBlacklist = 50;

    /// <summary>
    /// friend_num, then flist[i]: name, pid, state (enum 0x18), rating, division (enum 0x16),
    /// manner, is_login / is_ingame (YES|NO), server (svrtype), lobby_name, block_name,
    /// room_name and room_id (FUN_0051b3e0, FUN_005424e0..FUN_00543850).
    /// </summary>
    [Command("CMD_GET_FRIENDLIST", Roles = Roles)]
    public async ValueTask<KvMessage[]> GetFriendList(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("target_pid", ctx.Session.Pid);
        var rows = await db.Friendships.Where(f => f.Pid == pid).OrderBy(f => f.CreatedAt).ToListAsync(ctx.CancellationToken);
        var others = await Players(rows.Select(f => f.OtherPid), ctx.CancellationToken);

        var entries = new List<KvMessage>();
        foreach (var row in rows)
        {
            if (!others.TryGetValue(row.OtherPid, out var p))
                continue;

            var online = ctx.Hub.SessionsForPid(p.Pid).Where(s => s.State >= SessionState.PlayerSelected).ToList();
            var where = online.FirstOrDefault(s => s.RoomId is not null) ?? online.FirstOrDefault(s => s.BlockId is not null);
            var room = where?.RoomId is { } roomId ? ctx.Hub.FindRoom(roomId) : null;
            entries.Add(new KvMessage()
                .Set("name", p.Name)
                .Set("pid", p.Pid)
                .Set("state", row.State.ToString())
                .Set("rating", p.Rating)
                .Set("division", RankSystem.DivisionName(p.Division))
                .Set("manner", p.Manner)
                .Set("is_login", online.Count > 0 ? "YES" : "NO")
                .Set("is_ingame", room?.Status == "GAME" ? "YES" : "NO")
                .Set("server", "MENU")
                .Set("lobby_name", where is null ? "" : "OpenEleven")
                .Set("block_name", where?.BlockId is { } block ? lobbies.ById(block)?.Name ?? "" : "")
                .Set("room_name", room?.Name ?? "")
                .Set("room_id", room?.Id ?? 0));
        }

        return [ctx.Ok().SetList("friend_num", "flist", entries)];
    }

    /// <summary>A friend request (FUN_005fe850): to_pid and an optional message (mes_body).</summary>
    [Command("CMD_SEND_FRIENDREQ", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> SendFriendRequest(CommandContext ctx)
    {
        var me = ctx.Session.Pid;
        var to = ctx.Request.GetInt32("to_pid", 0);
        if (to == me || await db.Players.FindAsync([to], ctx.CancellationToken) is not { } target)
            return [ctx.Fail("ERR_DATABASE")];

        var existing = await db.Friendships.FirstOrDefaultAsync(f => f.Pid == me && f.OtherPid == to, ctx.CancellationToken);
        if (existing is { State: FriendState.Friend })
            return [ctx.Fail("ERR_INFRIENDLIST")];
        if (existing is not null)
            return [ctx.Fail("ERR_FRIEND_PERMISSION_WAITING_STATE")];
        if (await db.Blacklist.AnyAsync(b => (b.Pid == me && b.TargetPid == to) || (b.Pid == to && b.TargetPid == me), ctx.CancellationToken))
            return [ctx.Fail("ERR_INBLACKLIST")];
        if (await db.Friendships.CountAsync(f => f.Pid == me, ctx.CancellationToken) >= MaxFriends
            || await db.Friendships.CountAsync(f => f.Pid == to, ctx.CancellationToken) >= MaxFriends)
            return [ctx.Fail("ERR_FRIENDCOUNTFULL")];

        db.Friendships.Add(new Friendship { Pid = me, OtherPid = to, State = FriendState.SentReq });
        db.Friendships.Add(new Friendship { Pid = to, OtherPid = me, State = FriendState.RecievedReq });
        var message = (ctx.Request.GetString("mes_body") ?? "").Trim();
        db.Notices.Add(new PlayerNotice
        {
            Pid = to,
            Title = ServerText.Get("friendreq.title", target.Lang, ctx.Session.PlayerName),
            Body = message.Length > 0 ? message : ServerText.Get("friendreq.body", target.Lang, ctx.Session.PlayerName),
        });
        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>Answer to a received request (FUN_005f67f0): from_pid, answer YES|NO.</summary>
    [Command("CMD_REPLY_FRIENDREQ", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> ReplyFriendRequest(CommandContext ctx)
    {
        var me = ctx.Session.Pid;
        var from = ctx.Request.GetInt32("from_pid", 0);
        var pair = await Pair(me, from, ctx.CancellationToken);
        if (pair.Mine is not { State: FriendState.RecievedReq })
            return [ctx.Fail("ERR_DATABASE")];

        if (ctx.Request.GetString("answer") == "YES")
        {
            pair.Mine.State = FriendState.Friend;
            if (pair.Theirs is not null)
                pair.Theirs.State = FriendState.Friend;
            var lang = (await db.Players.FindAsync([from], ctx.CancellationToken))?.Lang;
            db.Notices.Add(new PlayerNotice
            {
                Pid = from,
                Title = ServerText.Get("friendok.title", lang),
                Body = ServerText.Get("friendok.body", lang, ctx.Session.PlayerName),
            });
        }
        else
        {
            Remove(pair);
        }

        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>Withdraw a request I sent (FUN_005f6580): to_pid.</summary>
    [Command("CMD_DEL_FRIENDREQ", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> DeleteFriendRequest(CommandContext ctx) => RemovePair(ctx, ctx.Request.GetInt32("to_pid", 0));

    /// <summary>End a friendship (FUN_005f6a70): from_pid names the friend.</summary>
    [Command("CMD_DEL_FRIEND", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> DeleteFriend(CommandContext ctx) => RemovePair(ctx, ctx.Request.GetInt32("from_pid", 0));

    /// <summary>list_num, then blist[i].pid / name (FUN_0053b170). Asked right after login with pid.</summary>
    [Command("CMD_GET_BLACKLIST", Roles = Roles)]
    public async ValueTask<KvMessage[]> GetBlacklist(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        var rows = await db.Blacklist.Where(b => b.Pid == pid).OrderBy(b => b.CreatedAt).ToListAsync(ctx.CancellationToken);
        var targets = await Players(rows.Select(b => b.TargetPid), ctx.CancellationToken);
        var entries = rows.Where(b => targets.ContainsKey(b.TargetPid))
            .Select(b => new KvMessage().Set("pid", b.TargetPid).Set("name", targets[b.TargetPid].Name))
            .ToArray();
        return [ctx.Ok().SetList("list_num", "blist", entries)];
    }

    /// <summary>
    /// Block a player (FUN_005fe5e0): target_pid. A friend has to be removed first
    /// (ERR_INFRIENDLIST). Blocked players cannot join my rooms or send me requests.
    /// </summary>
    [Command("CMD_ADD_BLACKLIST", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> AddBlacklist(CommandContext ctx)
    {
        var me = ctx.Session.Pid;
        var target = ctx.Request.GetInt32("target_pid", 0);
        if (target == me)
            return [ctx.Fail("ERR_PLAYERISONESELF")];
        if (await db.Players.FindAsync([target], ctx.CancellationToken) is null)
            return [ctx.Fail("ERR_INVALIDARG")];
        if (await db.Blacklist.AnyAsync(b => b.Pid == me && b.TargetPid == target, ctx.CancellationToken))
            return [ctx.Fail("ERR_INBLACKLIST")];
        if (await db.Friendships.AnyAsync(f => f.Pid == me && f.OtherPid == target && f.State == FriendState.Friend, ctx.CancellationToken))
            return [ctx.Fail("ERR_INFRIENDLIST")];
        if (await db.Blacklist.CountAsync(b => b.Pid == me, ctx.CancellationToken) >= MaxBlacklist)
            return [ctx.Fail("ERR_FRIENDCOUNTFULL")];

        // A pending request either way goes away with the block.
        Remove(await Pair(me, target, ctx.CancellationToken));
        db.Blacklist.Add(new BlacklistEntry { Pid = me, TargetPid = target });
        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    [Command("CMD_DEL_BLACKLIST", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> DeleteBlacklist(CommandContext ctx)
    {
        var target = ctx.Request.GetInt32("target_pid", 0);
        await db.Blacklist.Where(b => b.Pid == ctx.Session.Pid && b.TargetPid == target).ExecuteDeleteAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// Long-lived friend list / request subscriptions (FUN_00541810). Parked without a reply:
    /// a bare reply would be taken as a delivery. The friend screen re-reads CMD_GET_FRIENDLIST.
    /// </summary>
    [Command("CMD_WATCH_FRIENDLIST", Roles = Roles)]
    [Command("CMD_WATCH_FRIENDREQ", Roles = Roles)]
    public ValueTask<KvMessage[]> Watch(CommandContext ctx) => Reply.None();

    private async ValueTask<KvMessage[]> RemovePair(CommandContext ctx, int other)
    {
        Remove(await Pair(ctx.Session.Pid, other, ctx.CancellationToken));
        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    private async Task<(Friendship? Mine, Friendship? Theirs)> Pair(int me, int other, CancellationToken ct)
        => (await db.Friendships.FirstOrDefaultAsync(f => f.Pid == me && f.OtherPid == other, ct),
            await db.Friendships.FirstOrDefaultAsync(f => f.Pid == other && f.OtherPid == me, ct));

    private void Remove((Friendship? Mine, Friendship? Theirs) pair)
    {
        if (pair.Mine is not null) db.Friendships.Remove(pair.Mine);
        if (pair.Theirs is not null) db.Friendships.Remove(pair.Theirs);
    }

    private async Task<Dictionary<int, Player>> Players(IEnumerable<int> pids, CancellationToken ct)
    {
        var ids = pids.Distinct().ToList();
        return await db.Players.Where(p => ids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, ct);
    }
}
