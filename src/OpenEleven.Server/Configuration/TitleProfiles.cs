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
    /// title; only PES 2011's table has been read so far, the others get none.
    /// </summary>
    public static int[] UseableTeams(GameProfile profile)
        => profile == GameProfile.Pes2011Pc ? State.Pes2011Lists.UseableTeams : [];

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