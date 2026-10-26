// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2010QuickMatchTests
{
    [Fact]
    public async Task Both_players_get_the_room_tail_after_accepting_the_quick_match()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2010Pc");
        var (a, b) = (Player(harness, 1), Player(harness, 2));

        await Search(harness, a);
        Assert.Contains(await Search(harness, b), m => m.MsgName == "MSG_QUICK_MATCH_MEMBER_NOTICE");
        Drain(a);
        await harness.DispatchAsync(a, "MSG_QUICK_MATCH_MEMBER_REPLY", withFields: r => r.Set("result", "NOERR"));
        await harness.DispatchAsync(b, "MSG_QUICK_MATCH_MEMBER_REPLY", withFields: r => r.Set("result", "NOERR"));
        await harness.DispatchAsync(a, "MSG_QUICK_ACCEPT_MATCH_OFFER", withFields: r => r.Set("result", "NOERR"));

        // b's accept completes the group; its reply carries the accept result followed by the
        // room-entry publications the lobby room tail normally sends.
        var replies = await harness.DispatchAsync(b, "MSG_QUICK_ACCEPT_MATCH_OFFER", withFields: r => r.Set("result", "NOERR"));
        Assert.Equal("MSG_QUICK_ACCEPT_MATCH_OFFER_RES",
            replies.First(m => m.MsgName == "MSG_QUICK_ACCEPT_MATCH_OFFER_RES").MsgName);
        foreach (var name in new[] { "CMD_WATCH_ROOMSTATE", "MSG_RMEMBERLISTSTART", "MSG_RMEMBERLIST",
                     "MSG_RMEMBEREND", "CMD_WATCH_ROOMPLAYERLIST", "CMD_WATCH_ENTRY_GAME",
                     "CMD_WATCH_DECIDE_GAMEPLAYER", "CMD_WATCH_DECIDE_GAMEPLAYERENV" })
            Assert.Contains(replies, m => m.MsgName == name);

        // The other player, whose accept came first, got the same tail when the group completed.
        var forA = Drain(a);
        Assert.Contains(forA, m => m.MsgName == "MSG_QUICK_ACCEPT_MATCH_OFFER_RES");
        Assert.Contains(forA, m => m.MsgName == "MSG_RMEMBERLIST");
        Assert.Contains(forA, m => m.MsgName == "CMD_WATCH_ENTRY_GAME");

        var room = harness.Hub.RequireRoom(a.RoomId!.Value);
        Assert.Equal(("SETENV", 2), (room.Status, room.Count));
        Assert.True(room.MatchStarting);
        Assert.Equal((0, 1), (a.GameSide, b.GameSide));
        Assert.Equal((0, 1), (a.GameEntryNo, b.GameEntryNo));
        Assert.True(a.GameSideLeader == true && b.GameSideLeader == true);

        var roster = Assert.Single(forA, m => m.MsgName == "MSG_RMEMBERLIST");
        Assert.Equal(2, roster.GetInt32("count"));
        var entries = Assert.Single(forA, m => m.MsgName == "CMD_WATCH_ENTRY_GAME");
        Assert.Equal(2, entries.GetList("entrygame").Count(e => e.GetInt32("gameNum") >= 0));
    }

    private static Session Player(ServerHarness harness, int pid)
    {
        var s = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        s.Pid = pid;
        s.PlayerName = $"p{pid}";
        return s;
    }

    private static Task<IReadOnlyList<KvMessage>> Search(ServerHarness harness, Session s)
        => harness.DispatchAsync(s, "MSG_QUICK_REQ_MATCHING_START", rqid: 9, withFields: r => r
            .Set("result", "NOERR").Set("type", "OC_Rank").Set("player_num", 2)
            .Set("game_time", "GT10").Set("frrate", "FRAME_60"));

    private static List<KvMessage> Drain(Session s)
    {
        var pushed = new List<KvMessage>();
        while (s.Queue.Reader.TryRead(out var item))
            if (item.Message is { } m)
                pushed.Add(m);
        return pushed;
    }
}
