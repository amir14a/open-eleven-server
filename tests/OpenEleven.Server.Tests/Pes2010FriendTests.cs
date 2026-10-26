// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2010FriendTests
{
    [Fact]
    public async Task Friend_watches_are_acknowledged_and_remembered()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "friend");
        var me = Player(harness, 1);

        var list = Assert.Single(await harness.DispatchAsync(me, "CMD_WATCH_FRIENDLIST", rqid: 21));
        Assert.Equal(21, list.Rqid);
        Assert.Equal("NOERR", list.GetString("result"));
        Assert.Equal(21, me.FriendListWatchRqid);

        var requests = Assert.Single(await harness.DispatchAsync(me, "CMD_WATCH_FRIENDREQ", rqid: 22));
        Assert.Equal(22, requests.Rqid);
        Assert.Equal(22, me.FriendRequestWatchRqid);
    }

    [Fact]
    public async Task Accepting_a_request_pushes_the_friend_to_both_players()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "friend");
        var (me, them) = (Player(harness, 1), Player(harness, other));
        await harness.DispatchAsync(me, "CMD_WATCH_FRIENDLIST", rqid: 21);
        await harness.DispatchAsync(them, "CMD_WATCH_FRIENDLIST", rqid: 31);
        await harness.DispatchAsync(them, "CMD_WATCH_FRIENDREQ", rqid: 32);
        Drain(me);
        Drain(them);

        var requested = await harness.DispatchAsync(me, "CMD_SEND_FRIENDREQ", withFields: r => r.Set("to_pid", other));
        // The sender has its own watch registered here, so the handler queues a refresh to
        // it too; the ACK is still the only reply to the request itself.
        Assert.Single(requested, m => m.MsgName == "CMD_SEND_FRIENDREQ" && m.GetString("result") == "NOERR");
        var own = Assert.Single(requested, m => m.MsgName == "CMD_WATCH_FRIENDLIST");
        Assert.Equal("SentReq", Assert.Single(own.GetList("flist")).GetString("state"));
        var forThem = Drain(them);
        var request = Assert.Single(forThem, m => m.MsgName == "CMD_WATCH_FRIENDLIST");
        Assert.Equal(0, request.Rqid);
        Assert.Equal("RecievedReq", Assert.Single(request.GetList("flist")).GetString("state"));
        Assert.Contains(forThem, m => m.MsgName == "CMD_WATCH_FRIENDREQ" && m.Rqid == 0);

        var replies = await harness.DispatchAsync(them, "CMD_REPLY_FRIENDREQ", rqid: 33,
            withFields: r => r.Set("from_pid", 1).Set("answer", "YES"));
        Assert.Equal(["CMD_REPLY_FRIENDREQ", "CMD_WATCH_FRIENDLIST", "CMD_WATCH_FRIENDREQ"],
            replies.Select(m => m.MsgName));
        Assert.Equal(33, replies[0].Rqid);
        AssertFriend(replies[1], other, 1);

        var toSender = Assert.Single(Drain(me), m => m.MsgName == "CMD_WATCH_FRIENDLIST");
        AssertFriend(toSender, 1, other);
        var full = Assert.Single(toSender.GetList("flist"));
        Assert.Equal("NETWORK", full.GetString("mode"));
        Assert.True(full.Has("lobby_id") && full.Has("block_id") && full.Has("req_date") && full.Has("req_read"));
    }

    [Fact]
    public async Task Ending_a_friendship_pushes_the_empty_list_to_both_players()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "friend");
        var (me, them) = (Player(harness, 1), Player(harness, other));
        await harness.DispatchAsync(me, "CMD_WATCH_FRIENDLIST", rqid: 21);
        await harness.DispatchAsync(them, "CMD_WATCH_FRIENDLIST", rqid: 31);
        await harness.DispatchAsync(me, "CMD_SEND_FRIENDREQ", withFields: r => r.Set("to_pid", other));
        await harness.DispatchAsync(them, "CMD_REPLY_FRIENDREQ", withFields: r => r.Set("from_pid", 1).Set("answer", "YES"));
        Drain(me);
        Drain(them);

        var deleted = await harness.DispatchAsync(me, "CMD_DEL_FRIEND", withFields: r => r.Set("from_pid", other));
        var ownPush = Assert.Single(deleted, m => m.MsgName == "CMD_WATCH_FRIENDLIST");
        Assert.Equal(0, ownPush.GetInt32("friend_num"));
        Assert.Empty(ownPush.GetList("flist"));
        var theirPush = Assert.Single(Drain(them), m => m.MsgName == "CMD_WATCH_FRIENDLIST");
        Assert.Equal(0, theirPush.GetInt32("friend_num"));
        Assert.Empty(theirPush.GetList("flist"));
    }

    [Fact]
    public async Task Pes2011_keeps_its_parked_watch_behaviour()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");
        var me = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        me.Pid = 1;

        Assert.Empty(await harness.DispatchAsync(me, "CMD_WATCH_FRIENDLIST", rqid: 21));
        Assert.Empty(await harness.DispatchAsync(me, "CMD_WATCH_FRIENDREQ", rqid: 22));
    }

    private static void AssertFriend(KvMessage message, int me, int other)
    {
        Assert.Equal(0, message.Rqid);
        Assert.Equal(1, message.GetInt32("friend_num"));
        var friend = Assert.Single(message.GetList("flist"));
        Assert.Equal(other, friend.GetInt32("pid"));
        Assert.Equal("Friend", friend.GetString("state"));
    }

    private static Task<ServerHarness> Harness() => ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2010Pc");

    private static Session Player(ServerHarness harness, int pid)
    {
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.PlayerSelected);
        session.Pid = pid;
        session.PlayerName = $"p{pid}";
        return session;
    }

    private static async Task<int> AddPlayerAsync(ServerHarness harness, string name)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var account = new Account { GameId = name, PasswordHash = "x" };
        var player = new Player { Name = name, NormalizedName = name.ToUpperInvariant(), Division = "D5" };
        account.Players.Add(player);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return player.Pid;
    }

    private static IReadOnlyList<KvMessage> Drain(Session session)
    {
        var messages = new List<KvMessage>();
        while (session.Queue.Reader.TryRead(out var packet))
            if (packet.Message is { } message) messages.Add(message);
        return messages;
    }
}
