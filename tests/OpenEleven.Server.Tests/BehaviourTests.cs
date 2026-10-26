// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class ServerListTests
{
    [Fact]
    public async Task Advertises_one_port_per_service_with_the_gate_pinned()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_GET_SVRLIST");
        var list = replies[0].GetList("svrlist");

        Assert.Equal(6, replies[0].GetInt32("server_num"));
        Assert.Equal(6, list.Count);

        Assert.Equal(28010, list.Single(e => e.GetString("svrtype") == "GATE").GetInt32("svrport"));

        var ports = list.Select(e => e.GetInt32("svrport")).ToArray();
        Assert.Equal(ports.Length, ports.Distinct().Count());

        // svrgid keys the client's internal service table, so it must stay stable.
        Assert.Equal([1, 2, 3, 4, 5, 6], list.Select(e => e.GetInt32("svrgid")).ToArray());
    }

    [Fact]
    public async Task A_host_name_is_sent_as_its_ip_in_the_server_list_and_kept_in_urls()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
        {
            s["Server:AdvertiseIp"] = "localhost";
            s["Server:Https:Enabled"] = "true";
        });

        var servers = await harness.DispatchAsync(harness.NewSession(), "CMD_GET_SVRLIST");
        var urls = await harness.DispatchAsync(harness.NewSession(), "CMD_GET_URLLIST");

        Assert.All(servers[0].GetList("svrlist"), e => Assert.Equal("127.0.0.1", e.GetString("svraddr")));
        Assert.Contains("https://localhost/pcspec.bin", urls[0].GetList("ulist").Select(e => e.GetString("url")));
    }

    [Fact]
    public async Task Every_entry_advertises_the_configured_address()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_GET_SVRLIST");

        Assert.All(
            replies[0].GetList("svrlist"),
            entry => Assert.Equal("127.0.0.1", entry.GetString("svraddr")));
    }

    [Fact]
    public async Task Service_role_declarations_are_enforced_when_switched_on()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
            s["Server:Protocol:EnforceServiceRoles"] = "true");
        var session = harness.NewSession(ServiceRole.Lobby);

        var replies = await harness.DispatchAsync(session, "CMD_GET_SVRLIST");

        Assert.Equal("ERR", replies[0].GetString("result"));
        Assert.Equal("SVRTYPE", replies[0].GetString("reason"));
    }

    [Fact]
    public async Task Service_role_declarations_are_advisory_by_default()
    {
        // The reference server answered every command on one port, so a command that
        // turns up somewhere unexpected is logged, not refused, until traffic proves
        // where it belongs.
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby);

        var replies = await harness.DispatchAsync(session, "CMD_GET_SVRLIST");

        Assert.Equal("NOERR", replies[0].GetString("result"));
    }
}

public class InformationListTests
{
    [Fact]
    public async Task Information_list_is_served_from_the_database()
    {
        // Regression: ordering this query by a DateTimeOffset column is not translatable
        // on SQLite, which turned the whole command into an ERR/SERVER reply.
        await using var harness = await ServerHarness.CreateAsync();
        using (var scope = harness.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            db.Information.Add(new InformationItem { Subject = "Maintenance", Body = "Tonight" });
            await db.SaveChangesAsync();
        }

        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_GET_INFORMATIONLIST");

        // The fixed project notice always comes first, marked important.
        Assert.Equal("NOERR", replies[0].GetString("result"));
        Assert.Equal(2, replies[0].GetInt32("info_num"));
        var list = replies[0].GetList("ilist");
        Assert.Equal(("About OpenEleven", "1"), (list[0].GetString("mes_subject"), list[0].GetString("important")));
        Assert.Equal("Maintenance", list[1].GetString("mes_subject"));
    }
}

public class PlayerProfileTests
{
    [Fact]
    public async Task Profile_edits_are_persisted_and_read_back()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);
        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");

        var replies = await harness.DispatchAsync(session, "CMD_SET_PLAYERPROFILE", withFields: r =>
            r.Set("profile", new KvMessage()
                .Set("date", 0)
                .Set("birthmonth", 3)
                .Set("birthday", 21)
                .Set("country", 7)
                .Set("area", 2)
                .Set("favorite_team", 11)
                .Set("favorite_player", 1234)
                .Set("intro", "hello there")));

        Assert.Equal("NOERR", replies[0].GetString("result"));

        var info = await harness.DispatchAsync(session, "CMD_GET_PLAYERINFO");
        Assert.Equal(3, info[0].GetInt32("birthmonth"));
        Assert.Equal(21, info[0].GetInt32("birthday"));
        Assert.Equal(7, info[0].GetInt32("country"));
        Assert.Equal(11, info[0].GetInt32("favoriteTeam"));
        Assert.Equal(1234, info[0].GetInt32("favoritePlayer"));
        Assert.Equal("hello there", info[0].GetString("intro"));
    }

    [Fact]
    public async Task Profile_edit_without_a_selected_player_is_refused()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);

        var replies = await harness.DispatchAsync(session, "CMD_SET_PLAYERPROFILE", withFields: r =>
            r.Set("profile", new KvMessage().Set("country", 7)));

        Assert.Equal("ERR_DATABASE", replies[0].GetString("result"));
    }
}

public class SessionStateTests
{
    [Fact]
    public async Task Commands_arriving_too_early_are_rejected_rather_than_handled()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby);   // still Connected

        var replies = await harness.DispatchAsync(session, "CMD_JOIN_BLOCK");

        Assert.Equal("ERR", replies[0].GetString("result"));
        Assert.Equal("SEQUENCE", replies[0].GetString("reason"));
    }

    [Fact]
    public async Task Challenge_then_auth_advances_the_session()
    {
        await using var harness = await ServerHarness.CreateAsync();
        const string credential = "86d84f975c5afebdea53f5ec3c6abbde";

        await using (var scope = Microsoft.Extensions.DependencyInjection
                     .ServiceProviderServiceExtensions.CreateAsyncScope(harness.Services))
        {
            var accounts = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Data.Repositories.IAccountRepository>(scope.ServiceProvider);
            await accounts.RegisterAsync("tester", credential, "SERIAL-TESTER");
        }

        Assert.True((await harness.AuthenticateHttpAsync("tester", credential)).Success);

        var session = harness.NewSession(ServiceRole.Account);

        var challenge = await harness.DispatchAsync(session, "MSG_REQCCODE");
        Assert.Equal(SessionState.Challenged, session.State);
        Assert.Equal("MSG_CHALLENGE", challenge[1].MsgName);
        Assert.Equal(32, challenge[1].GetString("ccode")!.Length);

        var auth = await harness.AuthenticateSocketAsync(
            session, "tester", credential, "SERIAL-TESTER");

        Assert.Equal(SessionState.Authenticated, session.State);
        Assert.Equal("MSG_AUTHRESULT", auth[1].MsgName);
        Assert.Equal("tester", session.GameId);
    }

    [Fact]
    public async Task Selecting_a_player_binds_the_session_to_a_pid()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");

        Assert.Equal(SessionState.PlayerSelected, session.State);
        Assert.Equal("Local Player", session.PlayerName);
        Assert.True(session.Pid > 0);
    }
}

public class RoomTests
{
    [Fact]
    public async Task Peers_receive_the_advertised_external_endpoint_for_direct_lookup()
    {
        // A publicly advertised server is not on the clients' LAN, so the external
        // endpoint is the one to hand out.
        await using var harness = await ServerHarness.CreateAsync(
            settings => settings["Server:AdvertiseIp"] = "198.51.100.1");

        // Different remote addresses represent two different hosts behind the internet;
        // the LAN fallback must not kick in there.
        var host = harness.NewSession(
            ServiceRole.Lobby, SessionState.InBlock, IPAddress.Parse("203.0.113.10"));
        host.Pid = 1;
        host.ExternalIp = "198.51.100.10";
        host.ExternalPort = 61001;
        host.InternalIp = "192.168.1.10";
        host.InternalPort = 25110;

        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");

        var guest = harness.NewSession(
            ServiceRole.Lobby, SessionState.InBlock, IPAddress.Parse("203.0.113.20"));
        guest.Pid = 2;
        guest.ExternalIp = "198.51.100.20";
        guest.ExternalPort = 61002;
        guest.InternalIp = "192.168.1.20";
        guest.InternalPort = 27414;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        var replies = await harness.DispatchAsync(guest, "CMD_GET_IPANDPORT", withFields: request =>
            request.Set("pid", host.Pid));
        var endpoint = Assert.Single(replies);

        Assert.Equal(host.ExternalIp, endpoint.GetString("ex_ip"));
        Assert.Equal(host.ExternalPort, endpoint.GetInt32("ex_port"));
        Assert.Equal("192.168.1.10", endpoint.GetString("in_ip"));
        Assert.Equal(25110, endpoint.GetInt32("in_port"));
        Assert.Equal(host.Pid, endpoint.GetInt32("pid"));
    }

    [Fact]
    public async Task Endpoint_watch_notifies_host_when_same_nat_guest_joins()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        host.Pid = 1;
        host.ExternalIp = "198.51.100.10";
        host.ExternalPort = 61001;
        host.InternalIp = "192.168.1.10";
        host.InternalPort = 25110;

        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");
        var initialWatch = await harness.DispatchAsync(host, "CMD_WATCH_IPANDPORT", rqid: 77);
        Assert.Empty(initialWatch);

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        guest.ExternalIp = host.ExternalIp;
        guest.ExternalPort = 61002;
        guest.InternalIp = "192.168.1.20";
        guest.InternalPort = 27414;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        var pushed = new List<KvMessage>();
        while (host.Queue.Reader.TryRead(out var item))
            if (item.Message is { } message)
                pushed.Add(message);

        // Same NAT, but PES 2010 still gets the endpoint as reported: the client probes
        // in_ip itself when ex_ip matches its own (FUN_00f8bd00).
        var endpoint = Assert.Single(pushed, message => message.MsgName == "CMD_WATCH_IPANDPORT");
        Assert.Equal(77, endpoint.Rqid);
        Assert.Equal(guest.ExternalIp, endpoint.GetString("ex_ip"));
        Assert.Equal(guest.ExternalPort, endpoint.GetInt32("ex_port"));
        Assert.Equal(guest.Pid, endpoint.GetInt32("pid"));
    }

    [Fact]
    public async Task Room_updates_reach_every_in_block_client()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        host.Pid = 1;
        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");

        var observer = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        observer.Pid = 3;

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        Assert.True(observer.Queue.Reader.TryRead(out var packet));
        Assert.Equal("MSG_ROOMLISTUPDATE", packet.Message?.MsgName);
        Assert.Equal(1, packet.Message?.GetInt32("count"));
        Assert.Equal(roomId, packet.Message?.GetList("roomList")[0].GetInt32("room_id"));
    }

    [Fact]
    public async Task Room_deletion_reaches_every_in_block_client()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        host.Pid = 1;
        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");

        var observer = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        observer.Pid = 3;

        var result = harness.Hub.LeaveRoom(host, roomId);
        Assert.Equal(RoomLeaveStatus.Left, result.Status);
        Assert.True(observer.Queue.Reader.TryRead(out var packet));
        Assert.Equal("MSG_ROOMLISTDEL", packet.Message?.MsgName);
        Assert.Equal(roomId, packet.Message?.GetInt32("rid"));
    }

    [Fact]
    public async Task Room_state_watch_can_arm_before_join_and_ack_precedes_update()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var created = await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        Assert.Empty(await harness.DispatchAsync(guest, "CMD_WATCH_ROOMSTATE", rqid: 88));

        var replies = await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        Assert.Equal(
            ["CMD_JOIN_ROOM", "CMD_WATCH_ROOMSTATE",
             "MSG_RMEMBERLISTSTART", "MSG_RMEMBERLIST", "MSG_RMEMBEREND", "CMD_WATCH_ROOMPLAYERLIST"],
            replies.Select(reply => reply.MsgName).ToArray());
        Assert.Equal(2, replies[^1].GetInt32("room_player_num"));
        Assert.Equal(88, replies[1].Rqid);
        Assert.Equal("WAITING", replies[1].GetString("state"));
        Assert.Equal(owner.Pid, replies[1].GetInt32("owner_pid"));
    }

    [Fact]
    public async Task Room_state_is_sent_as_unsolicited_update_for_local_client_watch()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var created = await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        var replies = await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        var state = Assert.Single(replies, message => message.MsgName == "CMD_WATCH_ROOMSTATE");
        Assert.Equal(Hub.UnsolicitedRqid, state.Rqid);
        Assert.Equal("WAITING", state.GetString("state"));
        Assert.Equal(owner.Pid, state.GetInt32("owner_pid"));
    }

    [Fact]
    public async Task Room_watches_can_register_during_lobby_connection_initialization()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Connected);

        Assert.Empty(await harness.DispatchAsync(session, "CMD_WATCH_ROOMSTATE", rqid: 41));
        Assert.Empty(await harness.DispatchAsync(session, "CMD_WATCH_IPANDPORT", rqid: 42));
    }

    [Fact]
    public async Task Room_player_watch_is_acknowledged_after_join()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var created = await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");

        var replies = await harness.DispatchAsync(owner, "CMD_WATCH_ROOMPLAYERLIST", withFields: request =>
            request.Set("room_id", roomId));

        var reply = Assert.Single(replies);
        Assert.Equal("CMD_WATCH_ROOMPLAYERLIST", reply.MsgName);
        Assert.Equal(roomId, reply.GetInt32("room_id"));
        // The joining client looks the host's pid up in this list (FUN_007b8ad0).
        Assert.Equal(1, reply.GetInt32("room_player_num"));
        Assert.Equal(owner.Pid, Assert.Single(reply.GetList("room_player_list")).GetInt32("pid"));
    }

    [Fact]
    public async Task Numeric_entry_game_marks_both_room_players_ready()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        await harness.DispatchAsync(owner, "CMD_WATCH_ENTRY_GAME", rqid: 21);
        await harness.DispatchAsync(guest, "CMD_WATCH_ENTRY_GAME", rqid: 22);
        await harness.DispatchAsync(owner, "CMD_ENTRY_GAME", withFields: request =>
            request.Set("entry", 1).Set("side", "HOME"));
        await harness.DispatchAsync(guest, "CMD_ENTRY_GAME", withFields: request =>
            request.Set("entry", 1).Set("side", "AWAY"));

        var room = Assert.Single(harness.Hub.Rooms);
        var members = room.Snapshot();
        Assert.Equal([0, 1], members.Select(member => member.GameEntryNo).OrderBy(value => value));
        Assert.Equal([0, 1], members.Select(member => member.GameSide).OrderBy(value => value));
    }

    [Fact]
    public async Task Game_environment_reaches_the_other_occupant_through_its_watch()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));
        Assert.Empty(await harness.DispatchAsync(guest, "CMD_WATCH_DECIDE_GAMEENV", rqid: 55));

        await harness.DispatchAsync(owner, "CMD_SET_GAMEENV", withFields: request =>
            request.Set("game_env", new KvMessage()
                .Set("cpuLevel", "HARD")
                .Set("gametime", "15MINUTES")));

        var pushed = new List<KvMessage>();
        while (guest.Queue.Reader.TryRead(out var item))
            if (item.Message is { } message)
                pushed.Add(message);

        var decided = Assert.Single(
            pushed, message => message.MsgName == "CMD_WATCH_DECIDE_GAMEENV");
        Assert.Equal(55, decided.Rqid);
        var environment = Assert.IsType<KvMessage>(decided.GetValue("game_env"));
        Assert.Equal("HARD", environment.GetString("cpuLevel"));
        Assert.Equal("15MINUTES", environment.GetString("gametime"));
    }

    [Fact]
    public async Task Entering_the_game_publishes_the_roster_and_side_assignments()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        await harness.DispatchAsync(owner, "CMD_WATCH_DECIDE_GAMEPLAYER", rqid: 61);
        await harness.DispatchAsync(owner, "CMD_WATCH_DECIDE_GAMEPLAYERENV", rqid: 62);
        await harness.DispatchAsync(owner, "CMD_ENTRY_GAME", withFields: request =>
            request.Set("entry", 1).Set("side", "HOME"));
        await harness.DispatchAsync(guest, "CMD_ENTRY_GAME", withFields: request =>
            request.Set("entry", 1).Set("side", "AWAY"));

        var pushed = new List<KvMessage>();
        while (owner.Queue.Reader.TryRead(out var item))
            if (item.Message is { } message)
                pushed.Add(message);

        var roster = pushed.Last(message => message.MsgName == "CMD_WATCH_DECIDE_GAMEPLAYER");
        Assert.Equal(61, roster.Rqid);
        Assert.Equal(2, roster.GetInt32("player_count"));
        // FUN_0072ED20 reads the scalar key "pid" eight times, whatever the room size.
        var pids = roster.Fields
            .Where(field => field.Key == "pid")
            .Select(field => Convert.ToUInt32(field.Value))
            .ToArray();
        Assert.Equal([1u, 2u, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue], pids);
        Assert.Equal(8, roster.Fields.Count(field => field.Key == "video"));

        var sides = pushed.Last(message => message.MsgName == "CMD_WATCH_DECIDE_GAMEPLAYERENV");
        Assert.Equal(62, sides.Rqid);
        var entries = sides.GetList("sideinfo");
        Assert.Equal(2, entries.Count);
        var home = Assert.Single(entries, entry => entry.GetInt32("pid") == owner.Pid);
        Assert.Equal("HOME", home.GetString("side"));
        Assert.Equal("YES", home.GetString("sideLeader"));
        var away = Assert.Single(entries, entry => entry.GetInt32("pid") == guest.Pid);
        Assert.Equal("AWAY", away.GetString("side"));
        Assert.Equal("YES", away.GetString("sideLeader"));
    }

    [Fact]
    public async Task Leaving_notifies_the_remaining_occupant_through_the_discon_watches()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        await harness.DispatchAsync(owner, "CMD_WATCH_DISCON_PLAYERENV", rqid: 71);
        await harness.DispatchAsync(owner, "CMD_WATCH_DISCON_PLAYERMATCH", rqid: 72);
        await harness.DispatchAsync(owner, "CMD_ENTRY_GAME", withFields: request =>
            request.Set("entry", 1).Set("side", "HOME"));
        while (owner.Queue.Reader.TryRead(out _)) { }

        await harness.DispatchAsync(guest, "CMD_LEAVE_ROOM");

        var pushed = new List<KvMessage>();
        while (owner.Queue.Reader.TryRead(out var item))
            if (item.Message is { } message)
                pushed.Add(message);

        var left = Assert.Single(
            pushed, message => message.MsgName == "CMD_WATCH_DISCON_PLAYERENV");
        Assert.Equal(71, left.Rqid);
        Assert.Equal(guest.Pid, left.GetInt32("pid"));

        // The capital I in sideInfo is the client's own spelling for this command.
        var sides = Assert.Single(
            pushed, message => message.MsgName == "CMD_WATCH_DISCON_PLAYERMATCH");
        Assert.Equal(72, sides.Rqid);
        var remaining = Assert.Single(sides.GetList("sideInfo"));
        Assert.Equal(owner.Pid, remaining.GetInt32("pid"));
    }

    [Fact]
    public async Task Only_the_owner_can_rename_the_room()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        var refused = (await harness.DispatchAsync(guest, "CMD_CHANGE_ROOMNAME",
                withFields: request => request.Set("name", "Guest Rename")))
            .Single(message => message.MsgName == "CMD_CHANGE_ROOMNAME");
        Assert.Equal("ERR_NOTROOMOWNER", refused.GetString("result"));

        var renamed = (await harness.DispatchAsync(owner, "CMD_CHANGE_ROOMNAME",
                withFields: request => request.Set("name", "Owner Rename")))
            .Single(message => message.MsgName == "CMD_CHANGE_ROOMNAME");
        Assert.Equal("NOERR", renamed.GetString("result"));
        Assert.Equal("Owner Rename", harness.Hub.RequireRoom(roomId).Name);
    }

    [Fact]
    public async Task Only_the_owner_can_kick_a_room_member()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        var refused = (await harness.DispatchAsync(guest, "CMD_KICK_ROOMMEMBER",
                withFields: request => request.Set("target_pid", owner.Pid)))
            .Single(message => message.MsgName == "CMD_KICK_ROOMMEMBER");
        Assert.Equal("ERR_NOTROOMOWNER", refused.GetString("result"));

        var kicked = (await harness.DispatchAsync(owner, "CMD_KICK_ROOMMEMBER",
                withFields: request => request.Set("target_pid", guest.Pid)))
            .Single(message => message.MsgName == "CMD_KICK_ROOMMEMBER");
        Assert.Equal("NOERR", kicked.GetString("result"));
        Assert.Null(guest.RoomId);
        Assert.Equal(SessionState.InBlock, guest.State);
        Assert.Equal(owner.Pid, Assert.Single(harness.Hub.RequireRoom(roomId).Snapshot()).Pid);
    }

    [Fact]
    public async Task Guest_toggle_shows_up_in_the_room_entry()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        await harness.DispatchAsync(owner, "CMD_SET_GUEST", withFields: request =>
            request.Set("has_guestplayer", "YES"));

        Assert.True(owner.HasGuestPlayer);
        var entry = RoomPresenter.ListEntry(harness.Hub.RequireRoom(roomId));
        var gamer = Assert.Single(entry.GetList("gamer"));
        // Written as a bool, which the writer renders as the NO/YES the client's enum
        // type 0x36 accepts.
        Assert.Equal(true, gamer.GetValue("has_guestplayer"));
        Assert.Contains("has_guestplayer=\"YES\"", harness.Render(entry));
    }

    // SETENV is the two-player pre-match state: PublishRoomJoined only advances a room
    // once a second occupant arrives. A room the owner is still alone in stays WAITING
    // however it is configured.
    [Fact]
    public async Task Setting_game_environment_alone_does_not_advance_room_state()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var roomId = (await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        var room = Assert.Single(harness.Hub.Rooms);
        Assert.Equal("WAITING", room.Status);

        await harness.DispatchAsync(owner, "CMD_SET_GAMEENV", withFields: request =>
            request.Set("game_env", new KvMessage()
                .Set("cpuLevel", "NORMAL")
                .Set("gametime", "10MINUTES")
                .Set("injury", "NO")
                .Set("condition", "RANDOM")
                .Set("ball_type", 7)
                .Set("exGame", "YES")
                .Set("pkOnOff", "YES")
                .Set("substitution", 3)
                .Set("limitTime", "MIDDLE")));

        Assert.Equal(roomId, room.Id);
        Assert.Equal("WAITING", room.Status);
    }

    [Fact]
    public async Task Room_member_list_is_built_from_live_sessions()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        host.Pid = 1;
        host.PlayerName = "Host";
        host.ExternalIp = "203.0.113.7";
        host.ExternalPort = 5730;

        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");
        Assert.True(roomId > 0);
        Assert.Equal(SessionState.InRoom, host.State);

        var guest = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        guest.Pid = 2;
        guest.PlayerName = "Guest";
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: r =>
            r.Set("room_id", roomId));

        // Joining already refreshed the host's cached roster; the last MSG_RMEMBERLIST
        // is the direct reply to this request.
        var replies = await harness.DispatchAsync(host, "MSG_REQRMEMBERLIST");
        var members = replies.Last(r => r.MsgName == "MSG_RMEMBERLIST").GetList("list");

        Assert.Equal(2, members.Count);
        Assert.Equal("203.0.113.7", members[0].GetString("ex_ip"));
        Assert.Equal([0, 1], members.Select(m => m.GetInt32("room_entry_no")).ToArray());
        Assert.Equal([1, 2], members.Select(m => m.GetInt32("pid")).ToArray());

        // room_pid is each member's unique room-local number, one-based.
        Assert.Equal([1, 2], members.Select(m => m.GetInt32("room_pid")).ToArray());
    }

    [Fact]
    public async Task Leaving_the_last_session_disposes_the_room()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        host.Pid = 1;

        await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        Assert.Single(harness.Hub.Rooms);

        harness.Hub.Unregister(host);
        Assert.Empty(harness.Hub.Rooms);
    }

    [Fact]
    public async Task A_handler_can_push_to_another_session()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        host.Pid = 1;
        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        guest.Pid = 2;
        harness.Hub.JoinRoom(guest, roomId, password: "");

        var sent = harness.Hub.BroadcastToRoom(
            roomId, KvMessage.Ok("MSG_TESTNOTICE", 1), except: host.Id);

        Assert.Equal(1, sent);
        Assert.True(guest.Queue.Reader.TryRead(out var queued));
        Assert.Equal("MSG_TESTNOTICE", queued.Message!.MsgName);
    }
}

public class BlockTests
{
    [Fact]
    public async Task Joining_a_block_records_both_endpoints()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK", withFields: r => r
            .Set("ex_ip", "198.51.100.9")
            .Set("ex_port", 6000)
            .Set("in_ip", "192.168.1.44")
            .Set("in_port", 6001));

        Assert.Equal("198.51.100.9", session.ExternalIp);
        Assert.Equal(6000, session.ExternalPort);
        Assert.Equal("192.168.1.44", session.InternalIp);
        Assert.Equal(6001, session.InternalPort);
        Assert.Equal(SessionState.InBlock, session.State);
    }

    [Fact]
    public async Task Block_player_list_reports_the_players_present()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");
        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK");

        var replies = await harness.DispatchAsync(session, "MSG_REQBLOCKPLAYERLIST");

        Assert.Equal(
            ["MSG_REQBLOCKPLAYERLIST", "MSG_BLOCKPLAYERLISTSTART", "MSG_BLOCKPLAYERLISTDATA",
             "MSG_BLOCKPLAYERLISTEND"],
            replies.Select(r => r.MsgName ?? "").ToArray());

        var data = replies[2];
        Assert.Equal(1, data.GetInt32("block_player_num"));
        Assert.Equal("Local Player", data.GetList("block_player_list")[0].GetString("name"));
    }

    [Fact]
    public async Task Block_player_list_reports_the_room_occupied_by_a_player()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");
        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK");
        var room = Enumerable.First(await harness.DispatchAsync(session, "CMD_CREATEJOIN_ROOM"));

        var replies = await harness.DispatchAsync(session, "MSG_REQBLOCKPLAYERLIST");
        var entry = Assert.Single(replies[2].GetList("block_player_list"));

        Assert.Equal(room.GetInt32("room_id"), entry.GetInt32("room_id"));
    }
}

public class MultiPortIdentityTests
{
    [Fact]
    public async Task A_second_connection_inherits_the_identity_of_the_first()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var account = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        await harness.DispatchAsync(account, "CMD_SET_CURRENTPLAYER");

        // The client opens a fresh socket for the lobby port and does not repeat login.
        var lobby = new Session
        {
            Id = Guid.NewGuid(),
            Role = ServiceRole.Lobby,
            Remote = account.Remote,
        };

        Assert.True(harness.Hub.TryAdoptIdentity(lobby));
        Assert.Equal(account.Pid, lobby.Pid);
        Assert.Equal("Local Player", lobby.PlayerName);
        Assert.Equal(SessionState.PlayerSelected, lobby.State);
    }

    [Fact]
    public async Task Identity_is_not_shared_across_different_addresses()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var account = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        await harness.DispatchAsync(account, "CMD_SET_CURRENTPLAYER");

        var elsewhere = new Session
        {
            Id = Guid.NewGuid(),
            Role = ServiceRole.Lobby,
            Remote = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.5"), 50001),
        };

        Assert.False(harness.Hub.TryAdoptIdentity(elsewhere));
        Assert.Equal(0, elsewhere.Pid);
    }

    [Fact]
    public async Task Room_membership_is_never_inherited()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        host.Pid = 1;
        await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        Assert.Equal(SessionState.InRoom, host.State);

        var second = new Session
        {
            Id = Guid.NewGuid(),
            Role = ServiceRole.VdpChat,
            Remote = host.Remote,
        };

        Assert.True(harness.Hub.TryAdoptIdentity(second));
        Assert.Equal(SessionState.PlayerSelected, second.State);
        Assert.Null(second.RoomId);
    }
}

public class UnknownCommandTests
{
    [Fact]
    public async Task Unknown_commands_fall_back_to_a_bare_ack()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_TOTALLY_UNKNOWN");

        Assert.Equal("result=\"NOERR\",msg=\"CMD_TOTALLY_UNKNOWN\",rqid=4\0",
            harness.Render(replies[0]));
    }
}

public class SessionCommandTests
{
    [Fact]
    public async Task Heartbeat_is_acknowledged_and_stamps_activity()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);

        session.LastActivity = DateTimeOffset.UtcNow.AddMinutes(-10);
        var replies = await harness.DispatchAsync(session, "CMD_SEND_HEARTBEAT");

        Assert.Equal("result=\"NOERR\",msg=\"CMD_SEND_HEARTBEAT\",rqid=4\0",
            harness.Render(replies[0]));
        Assert.True(DateTimeOffset.UtcNow - session.LastActivity < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Language_is_stored_on_the_session_and_the_player()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");

        await harness.DispatchAsync(session, "CMD_SET_LANGUAGE", withFields: r => r.Set("lang", "FR"));

        Assert.Equal("FR", session.Language);

        var info = await harness.DispatchAsync(session, "CMD_GET_PLAYERINFO");
        Assert.Equal("FR", info[0].GetString("lang"));
    }
}

public class GameEnvironmentTests
{
    [Fact]
    public async Task Room_settings_are_kept_verbatim()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        session.Pid = 1;

        var created = await harness.DispatchAsync(session, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "Test Room")
            .Set("match_type", "OC_FREE")
            .Set("team_category", "ALL")
            .Set("max_players", 4)
            .Set("enable_guest", "YES"));

        var room = harness.Hub.RequireRoom(created[0].GetInt32("room_id"));
        Assert.Equal("OC_FREE", room.MatchType);
        Assert.Equal("ALL", room.TeamCategory);
        Assert.True(room.AllowGuest);

        await harness.DispatchAsync(session, "CMD_SET_GAMEENV", withFields: r =>
            r.Set("game_env", new KvMessage()
                .Set("cpuLevel", "HARD")
                .Set("gametime", "5MINUTES")
                .Set("ball_type", 7)
                .Set("substitution", 6)));

        Assert.NotNull(room.GameEnvironment);
        Assert.Equal("HARD", room.GameEnvironment!.GetString("cpuLevel"));
        Assert.Equal(7, room.GameEnvironment.GetInt32("ball_type"));
        Assert.Equal(6, room.GameEnvironment.GetInt32("substitution"));
    }

    [Fact]
    public async Task Game_settings_outside_a_room_are_refused()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.InRoom);

        var replies = await harness.DispatchAsync(session, "CMD_SET_GAMEENV", withFields: r =>
            r.Set("game_env", new KvMessage().Set("cpuLevel", "HARD")));

        Assert.Equal("NOROOM", replies[0].GetString("reason"));
    }

    // PES 2010 only sends CMD_ENTRY_GAME while the room is WAITING (FUN_007b7bc0), so a
    // join must not move the room to SETENV; the client does that itself.
    [Fact]
    public async Task Join_keeps_a_configured_room_waiting()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var owner = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        owner.Pid = 1;
        var guest = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        guest.Pid = 2;

        var created = await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "Test Room")
            .Set("match_type", "OC_FREE")
            .Set("team_category", "ALL")
            .Set("max_players", 4)
            .Set("enable_guest", "YES"));
        var roomId = created[0].GetInt32("room_id");

        await harness.DispatchAsync(owner, "CMD_SET_GAMEENV", withFields: r =>
            r.Set("game_env", new KvMessage().Set("gametime", "10MINUTES")));
        var room = harness.Hub.RequireRoom(roomId);
        Assert.Equal("WAITING", room.Status);

        var replies = await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: r =>
            r.Set("room_id", roomId));

        Assert.Equal("WAITING", room.Status);
        Assert.Contains(replies, message => message.MsgName == "CMD_JOIN_ROOM");
        Assert.Contains(replies, message =>
            message.MsgName == "CMD_WATCH_ROOMSTATE" && message.GetString("state") == "WAITING");

        // OwnerChanged/AppointOwner mean "the owner left" to PES 2010 (FUN_007bb130), so a
        // join is announced as a plain StateChanged.
        Assert.Equal("StateChanged", Assert.Single(replies, m => m.MsgName == "CMD_WATCH_ROOMSTATE").GetString("event"));

        // The creator's room screen reads its own room from the room-list cache, which only
        // this update fills; it follows the create reply.
        Assert.Equal("CMD_CREATEJOIN_ROOM", created[0].MsgName);
        var own = Assert.Single(created, m => m.MsgName == "MSG_ROOMLISTUPDATE");
        Assert.Equal(roomId, Assert.Single(own.GetList("roomList")).GetInt32("room_id"));
    }
}

public class LobbyConfigurationTests
{
    private static void ThreeLobbies(Dictionary<string, string?> settings)
    {
        settings["Server:Lobbies:0:Name"] = "Beginner";
        settings["Server:Lobbies:0:MaxPlayers"] = "100";
        settings["Server:Lobbies:1:Name"] = "Standard";
        settings["Server:Lobbies:1:MaxPlayers"] = "64";
        settings["Server:Lobbies:2:Name"] = "Expert";
        settings["Server:Lobbies:2:MaxPlayers"] = "32";
    }

    [Fact]
    public async Task Block_list_comes_from_configuration()
    {
        await using var harness = await ServerHarness.CreateAsync(ThreeLobbies);
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        var replies = await harness.DispatchAsync(session, "CMD_GET_BLOCKLIST");
        var blocks = replies[0].GetList("bklist");

        Assert.Equal(3, replies[0].GetInt32("count"));
        Assert.Equal(["Beginner", "Standard", "Expert"],
            blocks.Select(b => b.GetString("name") ?? "").ToArray());
        Assert.Equal(32, blocks[2].GetInt32("max_player_num"));
    }

    [Fact]
    public async Task A_disabled_lobby_is_not_advertised()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
        {
            ThreeLobbies(s);
            s["Server:Lobbies:1:Enabled"] = "false";
        });
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        var replies = await harness.DispatchAsync(session, "CMD_GET_BLOCKLIST");

        Assert.Equal(["Beginner", "Expert"],
            replies[0].GetList("bklist").Select(b => b.GetString("name") ?? "").ToArray());
    }

    [Fact]
    public async Task Join_selects_the_block_by_its_index_in_the_advertised_list()
    {
        await using var harness = await ServerHarness.CreateAsync(ThreeLobbies);
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 2));

        Assert.Equal(SessionState.InBlock, session.State);
        Assert.Equal(3, session.BlockId);       // third entry, auto-assigned id
    }

    [Fact]
    public async Task Occupancy_is_reported_per_block()
    {
        await using var harness = await ServerHarness.CreateAsync(ThreeLobbies);

        var first = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        first.Pid = 1;
        await harness.DispatchAsync(first, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 0));

        var second = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        second.Pid = 2;
        await harness.DispatchAsync(second, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 2));

        var replies = await harness.DispatchAsync(first, "CMD_GET_BLOCKLIST");
        var blocks = replies[0].GetList("bklist");

        Assert.Equal(1, blocks[0].GetInt32("player_num"));
        Assert.Equal(0, blocks[1].GetInt32("player_num"));
        Assert.Equal(1, blocks[2].GetInt32("player_num"));
    }

    [Fact]
    public async Task Explicit_ids_survive_a_reorder()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
        {
            s["Server:Lobbies:0:Name"] = "Expert";
            s["Server:Lobbies:0:Id"] = "77";
            s["Server:Lobbies:1:Name"] = "Beginner";
            s["Server:Lobbies:1:Id"] = "42";
        });
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 1));

        Assert.Equal(42, session.BlockId);
    }

    [Fact]
    public async Task Join_falls_back_to_the_name_when_no_index_is_given()
    {
        await using var harness = await ServerHarness.CreateAsync(ThreeLobbies);
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK", withFields: r =>
            r.Set("name", "Standard"));

        Assert.Equal(2, session.BlockId);
    }

    [Fact]
    public async Task Join_is_refused_when_no_lobbies_are_configured()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
            s["Server:Lobbies:0:Enabled"] = "false");
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        var replies = await harness.DispatchAsync(session, "CMD_JOIN_BLOCK");

        Assert.Equal("NOBLOCK", replies[0].GetString("reason"));
        Assert.Equal(SessionState.Authenticated, session.State);
    }
}

public class RegistryTests
{
    [Fact]
    public async Task Every_registered_command_is_reachable_on_at_least_one_service()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var registry = (Dispatch.CommandRegistry)harness.Services.GetService(
            typeof(Dispatch.CommandRegistry))!;

        Assert.NotEmpty(registry.Entries);
        Assert.All(registry.Entries, entry => Assert.NotEqual(ServiceRole.None, entry.Roles));
    }

    [Theory]
    [InlineData("Pes2010Pc")]
    [InlineData("Pes2011Pc")]
    public async Task Only_the_gate_and_login_commands_are_open_before_login(string profile)
    {
        // RequiredState defaults to Authenticated; anything opting out must be on this list,
        // so a new command cannot become reachable from an unauthenticated connection by accident.
        string[] preLogin =
        [
            "CMD_GET_SVRLIST", "CMD_GET_SVRVERSION", "CMD_GET_SVRTIME", "CMD_GET_EULA", "CMD_GET_URLLIST",
            "CMD_GET_INFORMATIONLIST", "CMD_DISCONNECT", "CMD_SEND_HEARTBEAT", "MSG_REQCCODE", "MSG_REQAUTH",
            "NETCLIENT", "CMD_REG_SVRLIST", "CMD_GET_HTTP", "CMD_AUTH_GAMEID", "CMD_LOGIN",
            "CMD_WATCH_ROOMSTATE", "CMD_WATCH_IPANDPORT",
        ];
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = profile);
        var registry = (Dispatch.CommandRegistry)harness.Services.GetService(typeof(Dispatch.CommandRegistry))!;

        var open = registry.Entries.Where(e => e.RequiredState < SessionState.Authenticated).Select(e => e.Msg);

        Assert.Empty(open.Except(preLogin));
    }

    [Fact]
    public async Task An_unauthenticated_connection_cannot_report_match_events()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");
        var stranger = harness.NewSession(ServiceRole.Menu, SessionState.Connected);

        var reply = Assert.Single(await harness.DispatchAsync(stranger, "CMD_ADD_FOUL"));

        Assert.Equal(("ERR", "SEQUENCE"), (reply.GetString("result"), reply.GetString("reason")));
    }
}
