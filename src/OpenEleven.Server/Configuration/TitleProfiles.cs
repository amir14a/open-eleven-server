// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Reflection;

namespace OpenEleven.Server.Configuration;

/// <summary>Maps a configured <see cref="GameProfile"/> to its per-title profile assembly.</summary>
public static class TitleProfiles
{
    /// <summary>
    /// The display name used in log output for a title (e.g. "PES 2011 (PC)").
    /// </summary>
    public static string DisplayName(GameProfile profile) => profile switch
    {
        GameProfile.Pes2010Pc => "PES 2010 (PC)",
        GameProfile.Pes2011Pc => "PES 2011 (PC)",
        GameProfile.Pes2012Pc => "PES 2012 (PC)",
        GameProfile.Pes2013Pc => "PES 2013 (PC)",
        _ => profile.ToString(),
    };

    /// <summary>
    /// The team ids a title's team-select screen accepts (useable_team_id[]). Team ids differ per
    /// title; PES 2012 and 2013 have not been read yet and get none.
    /// </summary>
    public static int[] UseableTeams(GameProfile profile) => profile switch
    {
        GameProfile.Pes2010Pc => Pes2010Teams,
        GameProfile.Pes2011Pc => State.Pes2011Lists.UseableTeams,
        _ => [],
    };

    /// <summary>
    /// Every team of retail PES 2010 in categories 8 (national), 0x16 (club) and 0x20 (classic),
    /// read from the running game's team table (DAT_018bd3cc). CMD_SET_CURRENTPLAYER's
    /// useable_team_id[] (FUN_0072bb20) is the whitelist (FUN_00766230) that the default-team pick
    /// (FUN_00903c10) draws from; with it empty a side has no team (FUN_00b73b20 returns -1) and
    /// the match is abandoned with CMD_SEND_ABNORMALEND right after Start Match.
    /// </summary>
    private static readonly int[] Pes2010Teams =
    [
        21, 9, 1170, 27, 24, 13, 15, 5, 18, 8, 14, 28, 22, 1, 1164, 12, 1551, 10, 2, 16, 19, 6, 26, 31, 3, 1550,
        20, 23, 7, 17, 11, 29, 30, 4, 1041, 36, 1051, 34, 1058, 1059, 1067, 32, 35, 38, 37, 1081, 33, 1099,
        42, 43, 40, 1118, 39, 50, 1128, 45, 47, 44, 51, 48, 46, 49, 1129, 57, 1002, 54, 55, 1010, 52, 1013, 1012,
        1022, 1026, 56, 53, 1029, 1031, 1032, 1184,
        101, 107, 201, 176, 202, 378, 102, 177, 178, 1589, 103, 173, 100, 206, 395, 396, 179, 105, 400, 208, 180,
        115, 1763, 409, 215, 212, 182, 213, 414, 181, 113, 112, 415, 217, 114, 218, 418, 219, 221, 1528, 234, 319,
        186, 320, 321, 188, 124, 323, 119, 120, 122, 325, 121, 327, 238, 123, 125, 240, 241, 190, 243, 116, 242,
        117, 244, 245, 349, 246, 247, 118, 248, 351, 250, 251, 252, 355, 254, 255, 357, 258, 108, 111, 259, 362,
        363, 172, 109, 260, 261, 263, 264, 265, 373, 110, 266, 267, 375, 268, 174, 269, 1197, 1203, 1205, 175,
        1832, 1207, 1733, 270, 133, 198, 2258, 1212, 1215, 2261, 1695, 191, 1974, 192, 193, 1750, 1746, 2270,
        1747, 2272, 2263, 2275, 2265, 271, 135, 1218, 1219, 131, 1221, 132, 1223, 1583, 1227, 1582, 2106, 1706,
        2257, 273, 197, 130, 1809, 2274, 1232, 1805, 134, 139, 138, 1252, 199, 200, 1533, 1534, 1535, 1536, 1537,
        1538, 1539, 1540, 1541, 1542, 1543, 1544, 1545, 1546, 1547, 1548, 1549, 1552,
        3017, 3000, 3003, 3020, 3004, 3022, 3028, 3029, 3018, 3009, 3030, 3031, 3011, 3032, 3035, 3036, 3027,
    ];

    /// <summary>
    /// The simple assembly name of the profile project that carries this title's deltas.
    /// </summary>
    public static string AssemblyName(GameProfile profile) => profile switch
    {
        GameProfile.Pes2010Pc => "OpenEleven.Profiles.Pes2010Pc",
        GameProfile.Pes2011Pc => "OpenEleven.Profiles.Pes2011Pc",
        GameProfile.Pes2012Pc => "OpenEleven.Profiles.Pes2012Pc",
        GameProfile.Pes2013Pc => "OpenEleven.Profiles.Pes2013Pc",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown game profile."),
    };

    /// <summary>
    /// Resolves the profile assembly for a title among the assemblies already loaded,
    /// or loads it by name. Returns null when the profile project is not referenced by
    /// the host (the server then runs on the shared core alone, which serves the
    /// PES 2010 behaviour every title shares today).
    /// </summary>
    public static Assembly? TryLoadAssembly(GameProfile profile)
    {
        var name = AssemblyName(profile);

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));
        if (loaded is not null)
            return loaded;

        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (Exception)
        {
            return null;
        }
    }
}