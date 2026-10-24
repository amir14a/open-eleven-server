// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Tests;

/// <summary>
/// Each title runs its own database. This pins the convention that a per-title config
/// pack points at a DB file named for that title, so two titles can never share one.
/// </summary>
public class PerTitleDatabaseTests
{
    private static string ConfigPath(GameProfile profile)
        => Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "conf", $"server.{profile.ToString().ToLowerInvariant()}.yaml");

    [Theory]
    [InlineData(GameProfile.Pes2010Pc, "pes2010pc")]
    [InlineData(GameProfile.Pes2011Pc, "pes2011pc")]
    [InlineData(GameProfile.Pes2012Pc, "pes2012pc")]
    [InlineData(GameProfile.Pes2013Pc, "pes2013pc")]
    public void Each_title_config_pack_points_at_its_own_database(GameProfile profile, string expectedDb)
    {
        var path = ConfigPath(profile);
        Assert.True(File.Exists(path), $"Missing config pack: {path}");

        var text = File.ReadAllText(path);
        Assert.Contains($"Data Source=data/{expectedDb}.db", text);
        Assert.Contains($"GameProfile: {profile}", text);
    }

    [Fact]
    public void All_four_titles_use_distinct_databases()
    {
        var dbs = new[]
        {
            GameProfile.Pes2010Pc, GameProfile.Pes2011Pc, GameProfile.Pes2012Pc, GameProfile.Pes2013Pc
        }
        .Select(p => File.ReadAllText(ConfigPath(p)))
        .Select(t => t.Split('\n').First(l => l.Contains("Data Source=")).Trim())
        .ToArray();

        Assert.Equal(dbs.Length, dbs.Distinct().Count());
    }
}
