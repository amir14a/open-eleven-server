// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data.Entities;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class PlayerDirectoryCommandTests
{
    [Fact]
    public async Task Player_numbers_count_distinct_selected_players()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var firstSocket = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        firstSocket.Pid = 10;
        var samePlayer = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        samePlayer.Pid = 10;
        var secondPlayer = harness.NewSession(ServiceRole.Lobby, SessionState.InRoom);
        secondPlayer.Pid = 11;

        var requester = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);
        var reply = Assert.Single(await harness.DispatchAsync(requester, "CMD_GET_PLAYERNUMBERS"));

        Assert.Equal(2, reply.GetInt32("network_player_num"));
        Assert.Equal(0, reply.GetInt32("compe_player_num"));
        Assert.Equal(0, reply.GetInt32("commu_player_num"));
        Assert.Equal(0, reply.GetInt32("legends_player_num"));
    }

    [Theory]
    [InlineData("FORWARD", "local")]
    [InlineData("PART", "cal pla")]
    [InlineData("PERFECT", "LOCAL PLAYER")]
    public async Task Player_search_modes_are_case_insensitive(string option, string query)
    {
        await using var harness = await ServerHarness.CreateAsync();
        var requester = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);

        var reply = Assert.Single(await harness.DispatchAsync(
            requester,
            "CMD_SEARCH_PLAYER",
            withFields: request => request.Set("option", option).Set("pname", query)));
        var result = Assert.Single(reply.GetList("list"));

        Assert.Equal(1, reply.GetInt32("count"));
        Assert.Equal("Local Player", result.GetString("name"));
        Assert.True(result.GetInt32("pid") > 0);
        Assert.Null(result.GetValue("svrgid"));
    }

    [Fact]
    public async Task Online_search_result_includes_service_and_block_presence()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var online = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        await harness.DispatchAsync(online, "CMD_SET_CURRENTPLAYER");
        online.State = SessionState.InBlock;
        online.BlockId = 1;

        var requester = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);
        var reply = Assert.Single(await harness.DispatchAsync(
            requester,
            "CMD_SEARCH_PLAYER",
            withFields: request => request.Set("option", "PERFECT").Set("pname", "Local Player")));
        var result = Assert.Single(reply.GetList("list"));

        Assert.Equal("LOBBY", result.GetString("svrtype"));
        Assert.Equal("Lobby", result.GetString("lobby_name"));
        Assert.Equal(1, result.GetInt32("block"));
        Assert.Equal("Beginner", result.GetString("block_name"));
        Assert.Equal(false, result.GetValue("inmatch"));
        Assert.Contains("inmatch=\"NO\"", harness.Render(reply));
    }

    [Fact]
    public async Task String_check_enforces_utf8_limit_controls_and_whole_blocked_words()
    {
        await using var harness = await ServerHarness.CreateAsync(settings =>
            settings["Server:Protocol:BlockedTerms:0"] = "blocked");
        var session = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);

        Assert.Equal("NOERR", (await Check("blockade"))[0].GetString("result"));
        Assert.Equal("ERR_INVALIDLETTER", (await Check("a BLOCKED word"))[0].GetString("result"));
        Assert.Equal("ERR_INVALIDLETTER", (await Check("bad\u0001text"))[0].GetString("result"));
        Assert.Equal("ERR_INVALIDLETTER", (await Check(new string('x', 115)))[0].GetString("result"));

        Task<IReadOnlyList<KvMessage>> Check(string value)
            => harness.DispatchAsync(session, "CMD_CHECK_STRING", withFields: r => r.Set("str", value));
    }

    [Fact]
    public async Task Empty_private_info_and_no_division_update_use_complete_contracts()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);

        var privateInfo = Assert.Single(await harness.DispatchAsync(session, "CMD_GET_PRIVATEINFO"));
        var division = Assert.Single(await harness.DispatchAsync(session, "CMD_GET_DIVISIONUPDATE"));

        Assert.Equal(0, privateInfo.GetInt32("count"));
        Assert.Equal(false, division.GetValue("updated"));
        Assert.Contains("updated=\"NO\"", harness.Render(division));
    }
}

public class RoomProtocolCommandTests
{
    [Fact]
    public async Task Room_list_is_scoped_and_uses_the_confirmed_four_message_sequence()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var owner = RoomSession(harness, 1);
        var sameBlock = RoomSession(harness, 2);
        var otherBlock = RoomSession(harness, 3, blockId: 2);

        var created = Enumerable.First(await harness.DispatchAsync(
            owner,
            "CMD_CREATEJOIN_ROOM",
            withFields: r => r
                .Set("name", "Visible room")
                .Set("match_type", "OC_FREE")
                .Set("team_category", "ALL")
                .Set("max_players", 4)
                .Set("password", "secret")
                .Set("enable_guest", "YES")));
        var roomId = created.GetInt32("room_id");

        var visible = (await harness.DispatchAsync(sameBlock, "MSG_REQROOMLIST"))
            .Where(message => message.MsgName is "MSG_REQROOMLIST" or "MSG_ROOMLISTSTART"
                or "MSG_ROOMLIST" or "MSG_ROOMLISTEND")
            .ToArray();
        Assert.Equal(
            ["MSG_REQROOMLIST", "MSG_ROOMLISTSTART", "MSG_ROOMLIST", "MSG_ROOMLISTEND"],
            visible.Select(r => r.MsgName!).ToArray());
        var entry = Assert.Single(visible[2].GetList("roomList"));
        Assert.Equal(roomId, entry.GetInt32("room_id"));
        Assert.Equal("Visible room", entry.GetString("name"));
        Assert.Equal("OC_FREE", entry.GetString("match_type"));
        Assert.Equal("WAITING", entry.GetString("status"));
        Assert.Equal("ENTRY", entry.GetString("game_phase"));
        Assert.Equal(4, entry.GetInt32("max_players"));
        Assert.Equal(true, entry.GetValue("is_passwd"));
        // The creator joins the room it makes, so the browser row shows one occupant.
        Assert.Equal(1, entry.GetInt32("gamer_num"));
        Assert.Equal(owner.Pid, Assert.Single(entry.GetList("gamer")).GetInt32("pid"));
        Assert.Equal(owner.Pid, Assert.Single(entry.GetList("room_player_list")).GetInt32("pid"));

        var wire = new KvWriter().Write(visible[2]);
        Assert.Contains("match_type=\"OC_FREE\"", wire);
        Assert.Contains("status=\"WAITING\"", wire);
        Assert.DoesNotContain("side=-1", wire);

        // FUN_00BB9200 looks up "roomList[N]", but that is a path expression — list
        // "roomList", element N — resolved by FUN_00B6E0F0/FUN_00B6DD20, which find the
        // element by walking {...} groups inside a bracketed list. Emitting a literal
        // roomList[0]={...} key instead leaves the browser empty.
        Assert.Contains("roomList=[{", wire);
        Assert.DoesNotContain("roomList[0]=", wire);

        // Dot-index keys inside the entry (score[0].score_1st) drop the client's Lobby
        // connection outright — see RoomPresenter.
        Assert.DoesNotContain("].score_", wire);

        var hidden = await harness.DispatchAsync(otherBlock, "MSG_REQROOMLIST");
        Assert.Empty(hidden[2].GetList("roomList"));
    }

    [Fact]
    public async Task Join_requires_an_existing_compatible_room_exact_password_and_capacity()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var owner = RoomSession(harness, 1);
        owner.ExternalIp = "203.0.113.1";
        owner.ExternalPort = 5730;
        owner.InternalIp = "192.168.1.10";
        owner.InternalPort = 5731;
        var guest = RoomSession(harness, 2);
        guest.ExternalIp = owner.ExternalIp; // same LAN as the owner
        var third = RoomSession(harness, 3);

        var missing = Assert.Single(await harness.DispatchAsync(
            guest, "CMD_JOIN_ROOM", withFields: r => r.Set("room_id", 999)));
        Assert.Equal("ERR_ROOMNOTFOUND", missing.GetString("result"));
        Assert.Empty(harness.Hub.Rooms);

        var created = Enumerable.First(await harness.DispatchAsync(
            owner,
            "CMD_CREATEJOIN_ROOM",
            withFields: r => r.Set("name", "Private").Set("password", "CaseSensitive").Set("max_players", 2)));
        var roomId = created.GetInt32("room_id");

        var wrongPassword = (await harness.DispatchAsync(
            guest,
            "CMD_JOIN_ROOM",
            withFields: r => r.Set("room_id", roomId).Set("password", "casesensitive")))
            .Single(message => message.MsgName == "CMD_JOIN_ROOM");
        Assert.Equal("ERR_INVALIDROOMINFO", wrongPassword.GetString("result"));

        var joined = (await harness.DispatchAsync(
            guest,
            "CMD_JOIN_ROOM",
            withFields: r => r.Set("room_id", roomId).Set("password", "CaseSensitive")))
            .Single(message => message.MsgName == "CMD_JOIN_ROOM");
        Assert.Equal("NOERR", joined.GetString("result"));
        Assert.Equal(SessionState.InRoom, guest.State);
        Assert.Equal(roomId, guest.RoomId);
        var roomOwner = Assert.IsType<KvMessage>(joined.GetValue("room_owner"));
        Assert.Equal(owner.Pid, roomOwner.GetInt32("pid"));
        Assert.Equal(owner.PlayerName, roomOwner.GetString("name"));
        // PES 2010 gets the endpoint as reported, even behind the same NAT: the client
        // probes in_ip itself when ex_ip matches its own (FUN_00f8bd00).
        Assert.Equal(owner.ExternalIp, roomOwner.GetString("ex_ip"));

        var members = await harness.DispatchAsync(
            guest, "MSG_REQRMEMBERLIST", withFields: r => r.Set("rid", roomId));
        Assert.Equal("NOERR", members[0].GetString("result"));
        var memberList = members.Single(r => r.MsgName == "MSG_RMEMBERLIST");
        Assert.Equal(2, memberList.GetInt32("count"));

        // pid identifies each member globally; room_pid is its unique room-local number.
        Assert.Equal(
            [owner.Pid, guest.Pid],
            memberList.GetList("list").Select(member => member.GetInt32("pid")).ToArray());
        Assert.Equal(
            [1, 2],
            memberList.GetList("list").Select(member => member.GetInt32("room_pid")).ToArray());

        var endpoint = (await harness.DispatchAsync(
            guest, "CMD_GET_IPANDPORT", withFields: r => r.Set("pid", owner.Pid)))
            .Single(message => message.MsgName == "CMD_GET_IPANDPORT");
        Assert.Equal(owner.Pid, endpoint.GetInt32("pid"));
        Assert.Equal("203.0.113.1", endpoint.GetString("ex_ip"));
        Assert.Equal(5730, endpoint.GetInt32("ex_port"));
        Assert.Equal("192.168.1.10", endpoint.GetString("in_ip"));
        Assert.Equal(5731, endpoint.GetInt32("in_port"));

        var full = (await harness.DispatchAsync(
            third,
            "CMD_JOIN_ROOM",
            withFields: r => r.Set("room_id", roomId).Set("password", "CaseSensitive")))
            .Single(message => message.MsgName == "CMD_JOIN_ROOM");
        Assert.Equal("ERR_ROOMISFULL", full.GetString("result"));
    }

    [Fact]
    public async Task Join_and_leave_emit_notices_transfer_owner_and_delete_empty_room()
    {
        // MSG_ROOMINNOTICE only goes out with unconfirmed messages enabled.
        await using var harness = await ServerHarness.CreateAsync(EmitUnconfirmed);
        var observer = RoomSession(harness, 20);
        await harness.DispatchAsync(observer, "MSG_REQROOMLIST");

        var owner = RoomSession(harness, 10);
        owner.ExternalIp = "203.0.113.10";
        var guest = RoomSession(harness, 11);
        guest.ExternalIp = "203.0.113.11";

        var created = Enumerable.First(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"));
        var roomId = created.GetInt32("room_id");
        Drain(observer);

        await harness.DispatchAsync(
            guest, "CMD_JOIN_ROOM", withFields: r => r.Set("room_id", roomId));
        var joined = Assert.Single(Drain(owner), m => m.MsgName == "MSG_ROOMINNOTICE");
        Assert.Equal(11, joined.GetInt32("pid"));
        // The notice is about the joining player: its room-local id, not the host's.
        Assert.Equal(2, joined.GetInt32("room_pid"));
        Assert.Equal(1, joined.GetInt32("room_entry_no"));
        Assert.Equal(-1, joined.GetInt32("game_entry_no"));

        await harness.DispatchAsync(owner, "CMD_LEAVE_ROOM");
        var left = Assert.Single(Drain(guest), m => m.MsgName == "MSG_PxROOMOUTNOTICE");
        Assert.Equal(10, left.GetInt32("pid"));
        Assert.Equal(11, harness.Hub.RequireRoom(roomId).OwnerPid);
        Assert.Equal(SessionState.InBlock, owner.State);

        Drain(observer);
        await harness.DispatchAsync(guest, "CMD_LEAVE_ROOM");
        var deleted = Assert.Single(Drain(observer), m => m.MsgName == "MSG_ROOMLISTDEL");
        Assert.Equal(roomId, deleted.GetInt32("rid"));
        Assert.Empty(harness.Hub.Rooms);
    }

    /// <summary>
    /// PES 2010 sends MSG_ROOMINNOTICE on every join: it is how the owner's room player
    /// list (FUN_00BB4D70) gains the joiner, which the P2P handshake needs. The roster
    /// stream is refreshed as well for the room screen.
    /// </summary>
    [Fact]
    public async Task Join_sends_the_room_in_notice_and_refreshes_the_roster()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var owner = RoomSession(harness, 10);
        var guest = RoomSession(harness, 11);

        var created = Enumerable.First(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"));
        var roomId = created.GetInt32("room_id");
        Drain(owner);

        await harness.DispatchAsync(
            guest, "CMD_JOIN_ROOM", rqid: 11, withFields: r => r.Set("room_id", roomId));

        var pushed = Drain(owner);
        Assert.Equal(guest.Pid, Assert.Single(pushed, m => m.MsgName == "MSG_ROOMINNOTICE").GetInt32("pid"));
        Assert.Contains(pushed, m => m.MsgName == "MSG_RMEMBERLISTSTART");

        var members = Assert.Single(pushed, m => m.MsgName == "MSG_RMEMBERLIST");
        Assert.Equal(2, members.GetInt32("count"));
        Assert.Equal(
            [owner.Pid, guest.Pid],
            members.GetList("list").Select(member => member.GetInt32("pid")).ToArray());

        Assert.Contains(pushed, m => m.MsgName == "MSG_RMEMBEREND");
    }

    [Fact]
    public async Task Leaving_sends_the_room_out_notice_and_refreshes_the_roster()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var owner = RoomSession(harness, 10);
        var guest = RoomSession(harness, 11);

        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");
        await harness.DispatchAsync(
            guest, "CMD_JOIN_ROOM", withFields: r => r.Set("room_id", roomId));
        Drain(guest);

        await harness.DispatchAsync(owner, "CMD_LEAVE_ROOM");

        var pushed = Drain(guest);
        Assert.Equal(owner.Pid, Assert.Single(pushed, m => m.MsgName == "MSG_PxROOMOUTNOTICE").GetInt32("pid"));
        var members = Assert.Single(pushed, m => m.MsgName == "MSG_RMEMBERLIST");
        Assert.Equal(1, members.GetInt32("count"));
        Assert.Equal(guest.Pid, Assert.Single(members.GetList("list")).GetInt32("pid"));
    }

    /// <summary>
    /// A client caches the roster it got from MSG_REQBLOCKPLAYERLIST. Anyone arriving
    /// afterwards must be announced, or the cache never learns that pid exists — and
    /// MSG_ROOMINNOTICE later names it.
    /// </summary>
    [Fact]
    public async Task Joining_a_block_announces_the_player_to_everyone_already_there()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var sitting = RoomSession(harness, 40);
        await harness.DispatchAsync(sitting, "MSG_REQBLOCKPLAYERLIST");
        Drain(sitting);

        // The record is built from the stored profile, exactly as a roster element is, so
        // this has to be a pid the database actually holds.
        var arriving = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);
        arriving.Pid = await SeededPidAsync(harness);
        await harness.DispatchAsync(
            arriving, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 0));

        var added = Assert.Single(Drain(sitting), m => m.MsgName == "MSG_BLOCKPLAYERLISTADD");
        Assert.Equal(Hub.UnsolicitedRqid, added.Rqid);
        var record = Assert.IsType<KvMessage>(added.GetValue("block_player"));
        Assert.Equal(arriving.Pid, record.GetInt32("pid"));

        // The arriving player is told about itself by its own roster request instead.
        Assert.DoesNotContain(Drain(arriving), m => m.MsgName == "MSG_BLOCKPLAYERLISTADD");

        harness.Hub.Unregister(arriving);
        var removed = Assert.Single(Drain(sitting), m => m.MsgName == "MSG_BLOCKPLAYERLISTDEL");
        Assert.Equal(arriving.Pid, removed.GetInt32("pid"));
        Assert.Equal(Hub.UnsolicitedRqid, removed.Rqid);
    }

    /// <summary>
    /// The client asks for the host's endpoint from the room browser, one request before
    /// CMD_JOIN_ROOM, so this has to answer while the caller is still only InBlock.
    /// </summary>
    [Fact]
    public async Task Endpoint_lookup_answers_before_joining_but_only_within_the_callers_block()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var host = RoomSession(harness, 30);
        host.ExternalIp = "203.0.113.30";
        host.ExternalPort = 4100;
        host.InternalIp = "192.168.5.30";
        host.InternalPort = 4101;
        var browsing = RoomSession(harness, 31);
        browsing.ExternalIp = host.ExternalIp;
        var elsewhere = RoomSession(harness, 32, blockId: 2);

        await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");

        Assert.Equal(SessionState.InBlock, browsing.State);
        var endpoint = (await harness.DispatchAsync(
            browsing, "CMD_GET_IPANDPORT", withFields: r => r.Set("pid", host.Pid)))
            .Single(message => message.MsgName == "CMD_GET_IPANDPORT");
        Assert.Equal("NOERR", endpoint.GetString("result"));
        // Same public IP (same NAT): still the reported endpoint; the client picks in_ip.
        // Same public IP (same NAT): the internal endpoint is preferred.
        Assert.Equal("203.0.113.30", endpoint.GetString("ex_ip"));
        Assert.Equal(4100, endpoint.GetInt32("ex_port"));
        Assert.Equal("192.168.5.30", endpoint.GetString("in_ip"));
        Assert.Equal(4101, endpoint.GetInt32("in_port"));

        var otherBlock = Assert.Single(await harness.DispatchAsync(
            elsewhere, "CMD_GET_IPANDPORT", withFields: r => r.Set("pid", host.Pid)));
        Assert.Equal("ERR_NOPLAYER", otherBlock.GetString("result"));

        var unknown = Assert.Single(await harness.DispatchAsync(
            browsing, "CMD_GET_IPANDPORT", withFields: r => r.Set("pid", 999)));
        Assert.Equal("ERR_NOPLAYER", unknown.GetString("result"));
    }

    /// <summary>
    /// A response's rqid says which parked request on that connection it answers, so a
    /// push aimed at a bystander must never carry the acting player's rqid. Live, a
    /// joining player's rqid=11 reached the owner — whose rqid 11 was its own
    /// MSG_REQRMEMBERLIST — and the owner's Lobby connection dropped.
    /// </summary>
    [Fact]
    public async Task Notices_pushed_to_other_sessions_never_echo_the_acting_players_rqid()
    {
        await using var harness = await ServerHarness.CreateAsync(EmitUnconfirmed);
        var observer = RoomSession(harness, 20);
        await harness.DispatchAsync(observer, "MSG_REQROOMLIST");

        var owner = RoomSession(harness, 10);
        var guest = RoomSession(harness, 11);

        var created = Enumerable.First(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", rqid: 7));
        var roomId = created.GetInt32("room_id");
        Assert.All(Drain(observer), m => Assert.Equal(Hub.UnsolicitedRqid, m.Rqid));

        await harness.DispatchAsync(
            guest, "CMD_JOIN_ROOM", rqid: 11, withFields: r => r.Set("room_id", roomId));
        var onJoin = Drain(owner);
        Assert.Contains(onJoin, m => m.MsgName == "MSG_ROOMINNOTICE");
        Assert.All(onJoin, m => Assert.Equal(Hub.UnsolicitedRqid, m.Rqid));

        await harness.DispatchAsync(guest, "CMD_LEAVE_ROOM", rqid: 12);
        var onLeave = Drain(owner);
        Assert.Contains(onLeave, m => m.MsgName == "MSG_PxROOMOUTNOTICE");
        Assert.All(onLeave, m => Assert.Equal(Hub.UnsolicitedRqid, m.Rqid));

        await harness.DispatchAsync(owner, "CMD_LEAVE_ROOM", rqid: 9);
        Assert.All(Drain(observer), m => Assert.Equal(Hub.UnsolicitedRqid, m.Rqid));
    }

    [Fact]
    public async Task Game_environment_change_pushes_a_complete_room_update()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var observer = RoomSession(harness, 20);
        await harness.DispatchAsync(observer, "MSG_REQROOMLIST");

        var owner = RoomSession(harness, 10);
        await harness.DispatchAsync(
            owner,
            "CMD_CREATEJOIN_ROOM",
            withFields: r => r.Set("name", "Configured room"));
        Drain(observer);

        await harness.DispatchAsync(owner, "CMD_SET_GAMEENV", withFields: r => r.Set(
            "game_env",
            new KvMessage().Set("gametime", "10MINUTES").Set("ball_type", 7)));

        var update = Assert.Single(Drain(observer), m => m.MsgName == "MSG_ROOMLISTUPDATE");
        var room = Assert.Single(update.GetList("roomList"));
        var environment = Assert.IsType<KvMessage>(room.GetValue("gameenv"));
        Assert.Equal("Configured room", room.GetString("name"));
        Assert.Equal("10MINUTES", environment.GetString("gametime"));
        Assert.Equal(7, environment.GetInt32("ball_type"));
    }

    private static Session RoomSession(ServerHarness harness, int pid, int blockId = 1)
    {
        var session = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        session.Pid = pid;
        session.PlayerName = $"Player {pid}";
        session.BlockId = blockId;
        return session;
    }

    /// <summary>Turns on the messages whose client behaviour is not yet confirmed.</summary>
    private static void EmitUnconfirmed(IDictionary<string, string?> settings)
        => settings["Server:Protocol:EmitUnconfirmedMessages"] = "true";

    /// <summary>Pid of a profile the harness actually seeded, for paths that read one.</summary>
    private static async Task<int> SeededPidAsync(ServerHarness harness)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var players = scope.ServiceProvider.GetRequiredService<IPlayerRepository>();
        var player = await players.GetByNameAsync("Local Player");
        return Assert.IsType<Player>(player).Pid;
    }

    private static IReadOnlyList<KvMessage> Drain(Session session)
    {
        var messages = new List<KvMessage>();
        while (session.Queue.Reader.TryRead(out var packet))
            if (packet.Message is { } message)
                messages.Add(message);
        return messages;
    }
}
