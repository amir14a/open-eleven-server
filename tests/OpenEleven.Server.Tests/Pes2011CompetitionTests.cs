// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2011CompetitionTests
{
    private static Task<ServerHarness> HarnessWithCup()
    {
        var now = DateTime.UtcNow;
        string At(double days) => now.AddDays(days).ToString("O", CultureInfo.InvariantCulture);
        return ServerHarness.CreateAsync(s =>
        {
            s["Server:GameProfile"] = "Pes2011Pc";
            s["Server:Competitions:0:Id"] = "7";
            s["Server:Competitions:0:Name"] = "Open Cup";
            s["Server:Competitions:0:EntryStart"] = At(-1);
            s["Server:Competitions:0:EntryEnd"] = At(2);
            s["Server:Competitions:0:MatchStart"] = At(3);
            s["Server:Competitions:0:MatchEnd"] = At(5);
            s["Server:Competitions:0:End"] = At(6);
            s["Server:Competitions:0:DlcVersion"] = "2.00";
        });
    }

    private static Session Player(ServerHarness harness)
    {
        var s = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        s.Pid = 1;
        return s;
    }

    [Fact]
    public async Task Running_cup_is_listed_with_every_key_the_client_requires()
    {
        await using var harness = await HarnessWithCup();
        var s = Player(harness);

        var reply = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_LIST", withFields: r => r
            .Set("compe_list_kind", "NOW_PLAY").Set("compe_kind", "KONAMI_CUP_ONLINE").Set("compe_mode", "NORMAL")));

        Assert.Equal(("NOERR", "NOW_PLAY", 1), (reply.GetString("result"), reply.GetString("compe_list_kind"), reply.GetInt32("compe_num")));
        var cup = Assert.Single(reply.GetList("compe_list"));
        foreach (var key in new[]
                 {
                     "compe_kind", "compe_id", "entry_start_time", "entry_end_time", "match_start_time",
                     "match_end_time", "compe_end_time", "compe_name", "status", "category", "entry_limit",
                     "entry_limit_rank", "dlcver", "player_capacity", "player_num", "champion_notice",
                     "guest_count", "is_coach_mode", "need_konamiid", "entry_limit_division",
                 })
            Assert.True(cup.Has(key), key);
        Assert.Equal(("ENTRY", "FALSE", "2.00"), (cup.GetString("status"), cup.GetString("champion_notice"), cup.GetString("dlcver")));

        var schedule = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_LIST", withFields: r => r
            .Set("compe_list_kind", "SCHEDULE")));
        Assert.Equal(0, schedule.GetInt32("compe_num"));
    }

    [Fact]
    public async Task Joining_shows_as_my_entry_and_counts_until_left()
    {
        await using var harness = await HarnessWithCup();
        var s = Player(harness);

        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7))).GetString("result"));
        Assert.Equal(7, Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MYENTRY_COMPEID")).GetInt32("compe_id"));

        var info = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_INFO", withFields: r => r.Set("compe_id", 7)));
        var data = Assert.IsType<KvMessage>(info.GetValue("compe_data"));
        Assert.Equal((7, 1), (data.GetInt32("compe_id"), data.GetInt32("player_num")));

        await harness.DispatchAsync(s, "CMD_LEAVE_COMPLG");
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MYENTRY_COMPEID")).GetInt32("compe_id"));
        Assert.Equal("ERR_COMPE_NOTENTRY", Assert.Single(await harness.DispatchAsync(s, "CMD_LEAVE_COMPLG")).GetString("result"));
    }
}
