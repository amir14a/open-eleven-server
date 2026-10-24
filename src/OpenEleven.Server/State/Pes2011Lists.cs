// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.State;

/// <summary>A CMD_REQWATCHL_* registration: the reply has to keep using the names the client chose.</summary>
public sealed record WatchListSubscription(string Msg, int Rqid, string NumName, string ListName);

/// <summary>
/// Entry builders for the PES 2011 list watches. Field names are the ones the client's
/// parsers read: room entries from <c>FUN_00515770</c>, block players from <c>FUN_00514a80</c>.
/// </summary>
public static class Pes2011Lists
{
    /// <summary>
    /// Every team of the retail game: categories 8 (national), 0x15 (club) and 0x1f (classic),
    /// read from pes2011.exe's team table (DAT_01940b64). CMD_SET_CURRENTPLAYER's
    /// useable_team_id[] (FUN_00513610, max 400) is the whitelist the team-select screen checks
    /// (FUN_00b02360); with it empty no default team exists and the match is abandoned.
    /// </summary>
    public static readonly int[] UseableTeams =
    [
        21, 9, 1170, 27, 24, 13, 15, 5, 18, 8, 14, 28, 22, 1, 1164, 12, 1551, 10, 2, 16, 19, 6, 26, 31, 3, 1550,
        20, 23, 7, 17, 11, 29, 30, 4, 1040, 1041, 36, 1051, 34, 1058, 1059, 1067, 32, 35, 38, 37, 1081, 33, 1099,
        42, 43, 40, 1118, 39, 50, 1128, 45, 47, 44, 51, 48, 46, 49, 1129, 57, 1002, 54, 55, 1010, 52, 1013, 1012,
        1022, 1026, 56, 53, 1029, 1031, 1032, 1184, 1141,
        101, 107, 201, 176, 1761, 202, 102, 177, 178, 103, 173, 100, 106, 395, 396, 179, 399, 105, 400, 208, 2318,
        180, 115, 1329, 405, 182, 213, 414, 181, 113, 112, 215, 415, 217, 114, 218, 418, 219, 221, 1528, 319, 186,
        187, 320, 321, 1362, 188, 124, 323, 119, 120, 122, 236, 121, 327, 238, 123, 125, 240, 190, 243, 116, 242,
        344, 117, 339, 244, 245, 349, 246, 247, 118, 248, 250, 251, 252, 355, 255, 357, 258, 108, 111, 259, 362,
        363, 1592, 366, 172, 109, 260, 261, 263, 264, 265, 196, 110, 267, 268, 174, 269, 1203, 1205, 175, 1207,
        1733, 185, 127, 270, 133, 198, 1212, 1215, 191, 1974, 192, 193, 1750, 1746, 1747, 135, 1941, 1218, 131,
        132, 1223, 1583, 1706, 273, 197, 130, 1232, 134, 139, 138, 1252, 2293, 2297, 2298, 2308, 2344, 2345, 2346,
        2347, 2348, 2349, 2350, 2351, 2352, 2353, 2354, 2355, 2356, 2357, 2240, 2241, 2242, 2243, 2244, 2245, 2246,
        2247, 2248, 2249, 2250, 2251, 2252, 2253, 2254, 2255, 199, 200, 1700, 1701, 1778, 1781, 1774, 1927, 1923,
        1238, 1929, 1241, 1244, 2282, 2283, 1940, 1247, 274, 1248, 3040, 1255, 2191, 2209, 1256, 2284, 2285, 2210,
        2196, 2286, 2211, 1260, 2198, 2214, 2287, 2288, 2215, 1262, 2289, 2290, 2205, 2291, 2218,
        3000, 3002, 3020, 3004, 3028, 3005, 3041, 3018, 3009, 3042, 3006, 3021, 3045, 3013, 3033, 3044, 3035, 3026,
    ];

    public static KvMessage RoomEntry(Room room)
    {
        var env = room.GameEnvironment;
        string Env(string key, string fallback) => env?.GetString(key) ?? fallback;
        int EnvInt(string key, int fallback) => env?.GetInt32(key, fallback) ?? fallback;

        var members = room.Snapshot();
        // enter_no / game_no are the real slot numbers; game_no is -1 until the player enters
        // the match (signed in the client's reader, like game_entry_no below).
        var gamers = members.Select(m => new KvMessage()
            .Set("pid", m.Pid)
            .Set("is_room_owner", m.Pid == room.OwnerPid ? "YES" : "NO")
            .Set("side", m.GameSide == 1 ? "AWAY" : "HOME")
            .Set("is_watcher", "NO")
            .Set("mpid", m.Pid)
            .Set("guest_mpid", 0)
            .Set("enter_no", m.RoomEntryNo)
            .Set("game_no", m.GameEntryNo)).ToArray();

        return new KvMessage()
            .Set("match_type", room.MatchType.Length > 0 ? room.MatchType : "OC_FREE")
            .Set("room_id", room.Id)
            .Set("status", room.Status)
            .Set("game_phase", room.Phase)
            .Set("name", room.Name)
            // FUN_00515770 drops the whole row unless room_list[i].gameenv is present.
            .Set("gameenv", new KvMessage()
                .Set("cpuLevel", Env("cpuLevel", "NORMAL"))
                .Set("gametime", Env("gametime", "10MINUTES"))
                .Set("injury", Env("injury", "NO"))
                .Set("condition", Env("condition", "NORMAL"))
                .Set("ball_type", EnvInt("ball_type", 0))
                .Set("exGame", Env("exGame", "NO"))
                .Set("pkOnOff", Env("pkOnOff", "NO"))
                .Set("substitution", EnvInt("substitution", 3))
                .Set("limitTime", Env("limitTime", "NOSET")))
            .Set("is_nogame", "NO")
            .Set("is_passwd", room.Password.Length > 0 ? "YES" : "NO")
            .Set("max_players", room.MaxMembers)
            .Set("team_flag", room.TeamFlag)
            .Set("is_invite_limit", room.InviteLimited ? "YES" : "NO")
            .Set("team_name", room.TeamName)
            .Set("short_teamname", room.ShortTeamName)
            .Set("is_legends_vs_player", room.LegendsVsPlayer)
            .Set("total_combination", room.TotalCombination)
            .Set("max_combination", room.MaxCombination)
            .Set("dlcontent", room.DlcVersion.Length > 0 ? room.DlcVersion : "1.00")
            .SetList("gamer_num", "gamer", gamers)
            .Set("enable_guest", room.AllowGuest ? "YES" : "NO");
    }

    /// <summary>
    /// One member of a room as the watcher sees it. FUN_00640e10 reads pid + ex_ip/ex_port/in_ip/in_port
    /// out of every entry to build the P2P peer table (it skips the viewer's own pid); without
    /// them the client never opens a connection to the host and gives up with CMD_LEAVE_ROOM after 40 s.
    /// </summary>
    public static KvMessage RoomPlayerEntry(Session member, PeerEndpoints at)
        => new KvMessage()
            .Set("pid", member.Pid)
            .Set("room_player_id", member.Pid)
            .Set("room_entry_no", member.RoomEntryNo)
            .Set("game_entry_no", member.GameEntryNo)
            .Set("desiredPosition", KvArray.Repeat("NO", 13))
            .Set("gameplayer_name", member.PlayerName)
            .Set("ex_ip", at.ExternalIp)
            .Set("ex_port", at.ExternalPort)
            .Set("in_ip", at.InternalIp)
            .Set("in_port", at.InternalPort);

    /// <summary>Makes a PES 2010 style block entry acceptable to the 2011 parser.</summary>
    public static KvMessage BlockPlayerEntry(KvMessage e)
    {
        e.Set("division", RankSystem.DivisionName(e.GetString("division")));
        foreach (var (key, value) in new (string, object)[]
                 {
                     ("npid", ""), ("xuid", 0), ("manner", 3), ("selfreport_level", "PRO"),
                     ("beginnermark", "NO"), ("position_want", "CF"),
                 })
        {
            if (!e.Has(key))
                e.Set(key, value);
        }

        if (!e.Has("desired_position"))
            e.Set("desired_position", KvArray.Repeat("NO", 13));

        return e;
    }
}
