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

public class Pes2011SocialTests
{
    /// <summary>The harness seeds pid 1; a second profile to befriend, block or play.</summary>
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

    private static Session Player(ServerHarness harness, int pid, SessionState state = SessionState.PlayerSelected)
    {
        var s = harness.NewSession(ServiceRole.Menu, state);
        s.Pid = pid;
        s.PlayerName = $"p{pid}";
        using var scope = harness.Services.CreateScope();
        s.AccountId = scope.ServiceProvider.GetRequiredService<GameDbContext>().Players.Single(p => p.Pid == pid).AccountId;
        return s;
    }

    private static Task<ServerHarness> Harness() => ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");

    private static string State(KvMessage list, int pid)
        => list.GetList("flist").Single(e => e.GetInt32("pid") == pid).GetString("state")!;

    [Fact]
    public async Task Friend_request_shows_on_both_sides_and_becomes_a_friendship()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "friend");
        var (me, them) = (Player(harness, 1), Player(harness, other));

        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(me, "CMD_SEND_FRIENDREQ", withFields: r => r.Set("to_pid", other))).GetString("result"));
        Assert.Equal("ERR_FRIEND_PERMISSION_WAITING_STATE",
            Assert.Single(await harness.DispatchAsync(me, "CMD_SEND_FRIENDREQ", withFields: r => r.Set("to_pid", other))).GetString("result"));

        var theirList = Assert.Single(await harness.DispatchAsync(them, "CMD_GET_FRIENDLIST", withFields: r => r.Set("target_pid", other)));
        Assert.Equal("RecievedReq", State(theirList, 1));
        Assert.Equal("YES", theirList.GetList("flist")[0].GetString("is_login"));

        await harness.DispatchAsync(them, "CMD_REPLY_FRIENDREQ", withFields: r => r.Set("from_pid", 1).Set("answer", "YES"));
        var myList = Assert.Single(await harness.DispatchAsync(me, "CMD_GET_FRIENDLIST", withFields: r => r.Set("target_pid", 1)));
        Assert.Equal("Friend", State(myList, other));

        // The request also left a notice for the target.
        var notices = Assert.Single(await harness.DispatchAsync(them, "CMD_GET_PRIVATEINFO", withFields: r => r.Set("pid", other)));
        Assert.Equal(1, notices.GetInt32("count"));

        // Shown once: the next login has nothing new.
        var again = Assert.Single(await harness.DispatchAsync(them, "CMD_GET_PRIVATEINFO", withFields: r => r.Set("pid", other)));
        Assert.Equal(0, again.GetInt32("count"));
    }

    [Fact]
    public async Task Another_accounts_notices_cannot_be_read_or_cleared()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "victim");
        var (me, them) = (Player(harness, 1), Player(harness, other));
        await harness.DispatchAsync(me, "CMD_SEND_FRIENDREQ", withFields: r => r.Set("to_pid", other));

        // The notices connection has no player selected yet; only the account decides.
        var snoop = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(snoop, "CMD_GET_PRIVATEINFO", withFields: r => r.Set("pid", other))).GetInt32("count"));
        Assert.Equal(1, Assert.Single(await harness.DispatchAsync(them, "CMD_GET_PRIVATEINFO", withFields: r => r.Set("pid", other))).GetInt32("count"));
    }

    [Fact]
    public async Task Blacklisted_player_cannot_send_requests_and_is_listed()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "pest");
        var (me, them) = (Player(harness, 1), Player(harness, other));

        await harness.DispatchAsync(me, "CMD_ADD_BLACKLIST", withFields: r => r.Set("target_pid", other));

        var list = Assert.Single(await harness.DispatchAsync(me, "CMD_GET_BLACKLIST", withFields: r => r.Set("pid", 1)));
        Assert.Equal((1, "pest"), (list.GetInt32("list_num"), list.GetList("blist")[0].GetString("name")));
        Assert.Equal("ERR_INBLACKLIST",
            Assert.Single(await harness.DispatchAsync(them, "CMD_SEND_FRIENDREQ", withFields: r => r.Set("to_pid", 1))).GetString("result"));
    }

    [Fact]
    public async Task Joining_a_room_with_different_game_data_is_refused()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "joiner");
        var owner = Player(harness, 1, SessionState.InBlock);
        owner.ParaHash = "040211f976018baf26c9f0b883acc2bb";
        var created = Assert.Single(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "r").Set("max_players", 2)), m => m.MsgName == "CMD_CREATEJOIN_ROOM");

        var joiner = Player(harness, other, SessionState.InBlock);
        joiner.ParaHash = "ffffffffffffffffffffffffffffffff";
        var reply = (await harness.DispatchAsync(joiner, "CMD_JOIN_ROOM", withFields: r => r.Set("room_id", created.GetInt32("room_id"))))[0];

        Assert.Equal("ERR_DIFFDCVERSION", reply.GetString("result"));
    }

    [Fact]
    public async Task Room_list_echoes_the_options_the_room_was_created_with()
    {
        await using var harness = await Harness();
        var owner = Player(harness, 1, SessionState.InBlock);
        var created = Assert.Single(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "r").Set("max_players", 2)
            .Set("team_flag", new KvArray(["YES", "NO", "YES", ""]))
            .Set("is_legends_vs_player", "YES")), m => m.MsgName == "CMD_CREATEJOIN_ROOM");

        var entry = Pes2011Lists.RoomEntry(harness.Hub.FindRoom(created.GetInt32("room_id"))!);

        Assert.Equal(["YES", "NO", "YES"], Assert.IsType<KvArray>(entry.GetValue("team_flag")).Values);
        Assert.Equal("YES", entry.GetString("is_legends_vs_player"));
        Assert.Equal("ENTRY", entry.GetString("game_phase"));
    }

    [Fact]
    public async Task Only_the_match_host_during_a_match_can_report_goals_and_phases()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "guest");
        var owner = Player(harness, 1, SessionState.InBlock);
        var created = Assert.Single(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "r").Set("max_players", 2)), m => m.MsgName == "CMD_CREATEJOIN_ROOM");
        var room = harness.Hub.FindRoom(created.GetInt32("room_id"))!;
        var guest = Player(harness, other, SessionState.InBlock);
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: r => r.Set("room_id", room.Id));

        async Task Goal(Session s) => await harness.DispatchAsync(s, "CMD_ADD_SCORE", withFields: r => r.Set("goal_side", "HOME"));

        await Goal(owner);                                   // before the match started
        await Goal(guest);                                   // not the host
        await harness.DispatchAsync(guest, "CMD_CHANGE_GAMEPHASE", withFields: r => r.Set("phase", "END"));
        Assert.Equal((0, "ENTRY"), (room.HomeGoals, room.Phase));

        await harness.DispatchAsync(owner, "CMD_SET_GAMEMEMBERENV");
        await harness.DispatchAsync(owner, "CMD_CHANGE_GAMEPHASE", withFields: r => r.Set("phase", "1ST"));
        await Goal(guest);
        await Goal(owner);

        Assert.Equal((1, "1ST", "GAME"), (room.HomeGoals, room.Phase, room.Status));
    }

    [Fact]
    public async Task An_injury_end_settles_only_a_match_being_played()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "guest");
        var owner = Player(harness, 1, SessionState.InBlock);
        var created = Assert.Single(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "r").Set("max_players", 2)), m => m.MsgName == "CMD_CREATEJOIN_ROOM");
        var room = harness.Hub.FindRoom(created.GetInt32("room_id"))!;
        var guest = Player(harness, other, SessionState.InBlock);
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: r => r.Set("room_id", room.Id));
        var before = room.Status;

        await harness.DispatchAsync(owner, "CMD_SET_INJURYGAMEEND", withFields: r => r.Set("side", "AWAY"));   // nothing started
        Assert.Equal((before, false), (room.Status, room.ResultRecorded));

        await harness.DispatchAsync(owner, "CMD_SET_GAMEMEMBERENV");
        await harness.DispatchAsync(owner, "CMD_CHANGE_GAMEPHASE", withFields: r => r.Set("phase", "1ST"));
        await harness.DispatchAsync(guest, "CMD_SET_INJURYGAMEEND", withFields: r => r.Set("side", "HOME"));   // not the host
        Assert.Equal(("GAME", false), (room.Status, room.ResultRecorded));

        await harness.DispatchAsync(owner, "CMD_SET_INJURYGAMEEND", withFields: r => r.Set("side", "AWAY"));
        Assert.Equal("RESULT", room.Status);
    }

    [Fact]
    public async Task Ranking_lists_played_players_by_the_requested_type()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "second");
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            foreach (var p in db.Players.Include(p => p.Stats))
            {
                p.Stats.MatchCount = 5;
                p.Rating = p.Pid == other ? 1300 : 1100;
                p.Stats.WinCount = p.Pid == other ? 1 : 4;
            }
            await db.SaveChangesAsync();
        }

        var me = Player(harness, 1);
        string Lines(int type) => Assert.Single(harness.DispatchAsync(me, "CMD_GET_RANKING", withFields: r => r
            .Set("uri", "http://x/ranking").Set("data", $"type={type}&division=6&from=0&records=100&pid=1&flag=0"))
            .GetAwaiter().GetResult()).GetString("ranking_data")!;

        Assert.StartsWith("1,1,", Lines(2).Split('\n')[11]);       // wins: pid 1 leads
        Assert.StartsWith($"1,{other},", Lines(6).Split('\n')[11]); // rating: the other leads
    }

    [Fact]
    public async Task Short_mail_reaches_the_inbox_is_read_and_deleted_per_side()
    {
        await using var harness = await Harness();
        var other = await AddPlayerAsync(harness, "pen");
        var (me, them) = (Player(harness, 1), Player(harness, other));

        await harness.DispatchAsync(me, "CMD_SEND_SHORTMAIL", withFields: r => r
            .Set("to_pid", other).Set("to_npid", 0).Set("mes_subject", "hello").Set("mes_body", "my good friend"));

        var inbox = Assert.Single(await harness.DispatchAsync(them, "CMD_GET_SHORTMAILLIST"));
        var mail = Assert.Single(inbox.GetList("smlist"));
        Assert.Equal(("hello", "NO", 1), (mail.GetString("mes_subject"), mail.GetString("read_flag"), mail.GetInt32("from_pid")));

        var read = Assert.Single(await harness.DispatchAsync(them, "CMD_GET_SHORTMAIL", withFields: r => r.Set("smail_id", mail.GetInt32("smail_id"))));
        Assert.Equal("my good friend", read.GetString("mes_body"));

        await harness.DispatchAsync(them, "CMD_DEL_SHORTMAIL", withFields: r => r.Set("smail_id", mail.GetInt32("smail_id")));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(them, "CMD_GET_SHORTMAILLIST")).GetInt32("smail_num"));
        var sent = Assert.Single(await harness.DispatchAsync(me, "CMD_GET_SENDSHORTMAILLIST"));
        Assert.Equal(("pen", 1), (sent.GetList("smlist")[0].GetString("to_name"), sent.GetInt32("smail_num")));
    }
}
