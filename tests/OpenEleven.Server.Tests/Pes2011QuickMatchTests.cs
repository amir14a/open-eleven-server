// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2011QuickMatchTests
{
    private static Session Player(ServerHarness harness, int pid)
    {
        var s = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        s.Pid = pid;
        s.PlayerName = $"p{pid}";
        return s;
    }

    private static List<KvMessage> Drain(Session s)
    {
        var pushed = new List<KvMessage>();
        while (s.Queue.Reader.TryRead(out var item))
            if (item.Message is { } m)
                pushed.Add(m);
        return pushed;
    }

    private static Task<IReadOnlyList<KvMessage>> Search(ServerHarness harness, Session s)
        => harness.DispatchAsync(s, "MSG_QUICK_REQ_MATCHING_START", rqid: 9, withFields: r => r
            .Set("result", "NOERR").Set("type", "OC_Rank").Set("player_num", 2)
            .Set("game_time", "GT10").Set("frrate", "FRAME_60"));

    [Fact]
    public async Task Two_searching_players_are_matched_and_put_in_a_ranked_room()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");
        var (a, b) = (Player(harness, 1), Player(harness, 2));

        // The harness hands back what was pushed to the caller along with its replies.
        Assert.Equal(["MSG_QUICK_REQ_MATCHING_START", "MSG_QUICK_MATCHING_START_RES"],
            (await Search(harness, a)).Select(m => m.MsgName));

        var numbers = Assert.Single(await harness.DispatchAsync(b, "CMD_GET_QUICKMATCHNUMBERS"));
        Assert.Equal(1, numbers.GetInt32("rank_1vs1_num"));

        Assert.Contains(await Search(harness, b), m => m.MsgName == "MSG_QUICK_MATCH_MEMBER_NOTICE");
        var notice = Assert.Single(Drain(a), m => m.MsgName == "MSG_QUICK_MATCH_MEMBER_NOTICE");
        Assert.Equal(9, notice.Rqid);
        Assert.Equal(2, notice.GetInt32("member_num"));
        var members = notice.GetList("member");
        Assert.Equal((1, "YES"), (members[0].GetInt32("pid"), members[0].GetString("is_owner")));
        Assert.Equal((2, "NO"), (members[1].GetInt32("pid"), members[1].GetString("is_owner")));

        Assert.DoesNotContain(
            await harness.DispatchAsync(a, "MSG_QUICK_MATCH_MEMBER_REPLY", withFields: r => r.Set("result", "NOERR")),
            m => m.MsgName == "MSG_QUICK_MATCHING_RES");
        await harness.DispatchAsync(b, "MSG_QUICK_MATCH_MEMBER_REPLY", withFields: r => r.Set("result", "NOERR"));
        Assert.Contains(Drain(a), m => m.MsgName == "MSG_QUICK_MATCHING_RES" && m.GetString("result") == "NOERR");

        await harness.DispatchAsync(a, "MSG_QUICK_ACCEPT_MATCH_OFFER", withFields: r => r.Set("result", "NOERR"));
        Assert.Null(a.RoomId);
        var lastAccept = await harness.DispatchAsync(b, "MSG_QUICK_ACCEPT_MATCH_OFFER", withFields: r => r.Set("result", "NOERR"));

        var offer = Assert.Single(lastAccept, m => m.MsgName == "MSG_QUICK_ACCEPT_MATCH_OFFER_RES");
        Assert.Contains(Drain(a), m => m.MsgName == "MSG_QUICK_ACCEPT_MATCH_OFFER_RES");
        Assert.Equal([1, 2], offer.GetList("member").Select(m => m.GetInt32("pid")));
        Assert.NotNull(a.RoomId);
        Assert.Equal(a.RoomId, b.RoomId);
        var room = harness.Hub.RequireRoom(a.RoomId!.Value);
        Assert.Equal(("OC_RANKING", 1, 0, 1), (room.MatchType, room.OwnerPid, a.GameSide, b.GameSide));
    }

    [Fact]
    public async Task Quick_chat_reaches_the_match_room_and_team_chat_only_the_own_side()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");
        var (a, b) = (Player(harness, 1), Player(harness, 2));
        harness.Hub.CreateQuickMatchRoom([a, b], "OC_RANKING", new KvMessage());

        Task Say(string channel) => harness.DispatchAsync(a, "CMD_SEND_TEXTCHAT", withFields: r => r
            .Set("channel", channel).Set("statement", "hi").Set("list_flg", "CHAT_BAN"));

        await Say("GAME_QUICK");
        Assert.Contains(Drain(b), m => m.MsgName == "CMD_WATCH_TEXTCHAT" && m.GetString("channel") == "GAME_QUICK");
        await Say("TEAM_QUICK");
        Assert.Empty(Drain(b));
    }

    [Fact]
    public async Task Cancelling_a_formed_match_tells_the_other_player()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");
        var (a, b) = (Player(harness, 1), Player(harness, 2));
        await Search(harness, a);
        await Search(harness, b);
        Drain(a);

        var reply = Assert.Single(await harness.DispatchAsync(b, "CMD_CANCEL_STARTQUICKMATCH"));

        Assert.Equal("NOERR", reply.GetString("result"));
        var cancelled = Assert.Single(Drain(a));
        Assert.Equal(("MSG_QUICK_MATCHING_RES", "ERR"), (cancelled.MsgName, cancelled.GetString("result")));
        Assert.Null(harness.Hub.QuickMatch.GroupOf(a));
    }
}
