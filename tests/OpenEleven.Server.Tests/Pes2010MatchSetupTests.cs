// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2010MatchSetupTests
{
    [Fact]
    public async Task Host_decisions_reach_both_clients_after_the_ack_with_their_own_watch_rqids()
    {
        await using var harness = await Harness();
        var (room, host, guest) = await TwoPlayers(harness);
        await harness.DispatchAsync(host, "CMD_WATCH_DECIDE_GAMEPLAYERENV", rqid: 61);
        await harness.DispatchAsync(guest, "CMD_WATCH_DECIDE_GAMEPLAYERENV", rqid: 71);
        await harness.DispatchAsync(host, "CMD_WATCH_DECIDE_GAMEPLAYER", rqid: 62);
        await harness.DispatchAsync(guest, "CMD_WATCH_DECIDE_GAMEPLAYER", rqid: 72);
        Drain(host);
        Drain(guest);

        // Reverse the entry-screen choices: peers must receive the final host decision.
        var replies = await harness.DispatchAsync(host, "CMD_SET_GAMEMEMBERENV", rqid: 16,
            withFields: r => r.SetList("list_num", "envlist",
                [Assignment(1, "AWAY"), Assignment(2, "HOME")]));
        Assert.Equal(["CMD_SET_GAMEMEMBERENV", "CMD_WATCH_DECIDE_GAMEPLAYERENV", "MSG_ROOMLISTUPDATE"],
            replies.Select(m => m.MsgName));
        Assert.Equal(16, replies[0].Rqid);
        Assert.Equal("NOERR", replies[0].GetString("result"));
        AssertSides(replies[1], 61);
        AssertSides(Assert.Single(Drain(guest), m => m.MsgName == "CMD_WATCH_DECIDE_GAMEPLAYERENV"), 71);
        Assert.True(room.MatchStarting);

        replies = await harness.DispatchAsync(host, "CMD_SET_GAMEMEMBER", rqid: 17);
        Assert.Equal(["CMD_SET_GAMEMEMBER", "CMD_WATCH_DECIDE_GAMEPLAYER"], replies.Select(m => m.MsgName));
        Assert.Equal(17, replies[0].Rqid);
        Assert.Equal(62, replies[1].Rqid);
        Assert.Equal(72, Assert.Single(Drain(guest)).Rqid);
        Assert.Equal(2, replies[1].GetInt32("player_count"));

        // Leaving the entry screen must not erase the accepted match lineup.
        await harness.DispatchAsync(guest, "CMD_ENTRY_GAME", withFields: r => r.Set("entry", "FALSE"));
        Assert.Equal(2, room.Snapshot().Count(m => m.GameEntryNo >= 0));
        Assert.True(host.GameSideLeader);
        Assert.True(guest.GameSideLeader);

        static void AssertSides(KvMessage message, int rqid)
        {
            Assert.Equal(rqid, message.Rqid);
            Assert.Equal(2, message.GetInt32("count"));
            var sides = message.GetList("sideinfo");
            Assert.Equal("AWAY", sides.Single(e => e.GetInt32("pid") == 1).GetString("side"));
            Assert.Equal("HOME", sides.Single(e => e.GetInt32("pid") == 2).GetString("side"));
            Assert.All(sides, e => Assert.Equal("YES", e.GetString("sideLeader")));
        }
    }

    [Fact]
    public async Task Only_selected_players_are_advertised_and_host_captain_choices_are_preserved()
    {
        await using var harness = await Harness();
        var (room, host, guest) = await TwoPlayers(harness, maxPlayers: 4);
        var spectator = await Join(harness, room.Id, 3);
        var teammate = await Join(harness, room.Id, 4);
        await Enter(harness, teammate, "HOME");
        foreach (var member in room.Snapshot()) Drain(member);

        await harness.DispatchAsync(host, "CMD_SET_GAMEMEMBERENV", withFields: r => r.SetList("list_num", "envlist",
            [Assignment(1, "HOME", "NO"), Assignment(2, "AWAY"), Assignment(4, "HOME")]));
        var sides = Assert.Single(Drain(guest), m => m.MsgName == "CMD_WATCH_DECIDE_GAMEPLAYERENV").GetList("sideinfo");
        Assert.Equal(3, sides.Count);
        Assert.Equal("NO", sides.Single(e => e.GetInt32("pid") == host.Pid).GetString("sideLeader"));
        Assert.Equal("YES", sides.Single(e => e.GetInt32("pid") == teammate.Pid).GetString("sideLeader"));
        Assert.DoesNotContain(sides, e => e.GetInt32("pid") == spectator.Pid);
        foreach (var member in room.Snapshot()) Drain(member);

        var replies = await harness.DispatchAsync(host, "CMD_SET_GAMEMEMBER");
        var roster = replies[1];
        Assert.Equal(0, roster.Rqid); // local watches need not be registered on the wire
        Assert.Equal(3, roster.GetInt32("player_count"));
        Assert.Equal(new uint[] { 1, 2, uint.MaxValue, 4, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue },
            roster.Fields.Where(f => f.Key == "pid").Select(f => Convert.ToUInt32(f.Value)));
        var wire = harness.Render(roster);
        Assert.Contains("pid=4294967295", wire);
        Assert.DoesNotContain("pid=-1", wire);
        Assert.Equal(8, roster.Fields.Count(f => f.Key == "video"));
        Assert.All(Drain(guest), m => Assert.Equal(0, m.Rqid));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("outsider")]
    [InlineData("spectator")]
    [InlineData("side")]
    [InlineData("leader")]
    [InlineData("no-captain")]
    [InlineData("one-side")]
    [InlineData("count")]
    public async Task Invalid_final_assignments_do_not_partially_mutate_or_publish(string invalid)
    {
        await using var harness = await Harness();
        var (room, host, guest) = await TwoPlayers(harness, maxPlayers: 4);
        await Join(harness, room.Id, 3);
        foreach (var member in room.Snapshot()) Drain(member);
        KvMessage[] entries = [Assignment(1, "AWAY"), Assignment(2, "HOME")];
        entries = invalid switch
        {
            "empty" => [],
            "missing" => [entries[0]],
            "duplicate" => [entries[0], Assignment(1, "HOME")],
            "outsider" => [entries[0], Assignment(99, "HOME")],
            "spectator" => [entries[0], Assignment(3, "HOME")],
            "side" => [entries[0], Assignment(2, "INVALID")],
            "leader" => [entries[0], Assignment(2, "HOME", "TRUE")],
            "no-captain" => [entries[0], Assignment(2, "HOME", "NO")],
            "one-side" => [entries[0], Assignment(2, "AWAY")],
            _ => entries,
        };
        var replies = await harness.DispatchAsync(host, "CMD_SET_GAMEMEMBERENV", withFields: r =>
            r.Set("envlist", entries).Set("list_num", invalid == "count" ? 7 : entries.Length));
        Assert.Equal("ERR_INVALIDROOMINFO", Assert.Single(replies).GetString("result"));
        Assert.False(room.MatchStarting);
        Assert.Equal(0, host.GameSide);
        Assert.Equal(1, guest.GameSide);
        Assert.All(room.Snapshot(), m => Assert.Null(m.GameSideLeader));
        Assert.All(room.Snapshot(), m => Assert.Empty(Drain(m)));
    }

    [Theory]
    [InlineData("CMD_SET_GAMEMEMBERENV")]
    [InlineData("CMD_SET_GAMEMEMBER")]
    public async Task A_non_host_cannot_decide_or_publish_the_match(string command)
    {
        await using var harness = await Harness();
        var (room, host, guest) = await TwoPlayers(harness);
        var replies = await harness.DispatchAsync(guest, command, withFields: r => r.SetList("list_num", "envlist",
            [Assignment(1, "AWAY"), Assignment(2, "HOME")]));
        Assert.Equal("ERR_NOTROOMOWNER", Assert.Single(replies).GetString("result"));
        Assert.False(room.MatchStarting);
        Assert.Empty(Drain(host));
    }

    [Fact]
    public async Task Final_roster_requires_an_accepted_side_decision()
    {
        await using var harness = await Harness();
        var (_, host, guest) = await TwoPlayers(harness);
        var replies = await harness.DispatchAsync(host, "CMD_SET_GAMEMEMBER");
        Assert.Equal("ERR_INVALIDROOMINFO", Assert.Single(replies).GetString("result"));
        Assert.Empty(Drain(guest));
    }

    [Fact]
    public async Task Phase_updates_advertise_current_teams_even_when_room_status_does_not_change()
    {
        await using var harness = await Harness();
        var (room, host, _) = await TwoPlayers(harness);
        await harness.DispatchAsync(host, "CMD_SET_GAMEMEMBERENV", withFields: r => r.SetList("list_num", "envlist",
            [Assignment(1, "HOME"), Assignment(2, "AWAY")]));
        await harness.DispatchAsync(host, "CMD_SET_GAMETEAM", withFields: r =>
            r.Set("home_team_id", 101).Set("away_team_id", 202));
        var browser = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        browser.RoomListSubscribed = true;

        foreach (var phase in new[] { "1ST", "BREAK1", "2ND" })
        {
            var replies = await harness.DispatchAsync(host, "CMD_CHANGE_GAMEPHASE", withFields: r => r.Set("phase", phase));
            Assert.Equal("CMD_CHANGE_GAMEPHASE", replies[0].MsgName);
            var entry = Assert.Single(Drain(browser)).GetList("roomList").Single();
            Assert.Equal("GAME", entry.GetString("status"));
            Assert.Equal(phase, entry.GetString("game_phase"));
            Assert.Equal(101, entry.GetInt32("team[0]"));
            Assert.Equal(202, entry.GetInt32("team[1]"));
        }

        await harness.DispatchAsync(host, "CMD_UPDATE_ROOMSTATE", withFields: r => r.Set("status", "SETENV"));
        Assert.Equal("ENTRY", room.Phase);
        Assert.False(room.MatchStarting);
        Assert.All(room.Snapshot(), m => Assert.Null(m.GameSideLeader));
    }

    private static Task<ServerHarness> Harness() => ServerHarness.CreateAsync(settings =>
        settings["Server:GameProfile"] = "Pes2010Pc");

    private static async Task<(Room Room, Session Host, Session Guest)> TwoPlayers(ServerHarness harness, int maxPlayers = 2)
    {
        var host = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        host.Pid = 1;
        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM", withFields: r => r.Set("max_players", maxPlayers));
        var creation = created.First(m => m.MsgName == "CMD_CREATEJOIN_ROOM");
        Assert.Equal("NOERR", creation.GetString("result"));
        var roomId = creation.GetInt32("room_id");
        var guest = await Join(harness, roomId, 2);
        await Enter(harness, host, "HOME");
        await Enter(harness, guest, "AWAY");
        Drain(host);
        Drain(guest);
        return (Assert.IsType<Room>(harness.Hub.FindRoom(roomId)), host, guest);
    }

    private static async Task<Session> Join(ServerHarness harness, int roomId, int pid)
    {
        var member = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        member.Pid = pid;
        await harness.DispatchAsync(member, "CMD_JOIN_ROOM", withFields: r => r.Set("room_id", roomId));
        return member;
    }

    private static Task<IReadOnlyList<KvMessage>> Enter(ServerHarness harness, Session member, string side)
        => harness.DispatchAsync(member, "CMD_ENTRY_GAME", withFields: r => r.Set("entry", 1).Set("side", side));

    private static KvMessage Assignment(int pid, string side, string leader = "YES")
        => new KvMessage().Set("pid", pid).Set("side", side).Set("sideLeader", leader);

    private static IReadOnlyList<KvMessage> Drain(Session session)
    {
        var messages = new List<KvMessage>();
        while (session.Queue.Reader.TryRead(out var packet))
            if (packet.Message is { } message) messages.Add(message);
        return messages;
    }
}
