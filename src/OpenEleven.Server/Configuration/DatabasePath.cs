// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.Configuration;

/// <summary>
/// Anchors a relative SQLite file to the application directory.
/// Visual Studio launches with the working directory set to the project folder while
/// <c>dotnet run</c> and the container use different ones, so a relative "Data Source"
/// would otherwise create a separate database per launch method.
/// </summary>
public static class DatabasePath
{
    private const string Key = "Data Source=";

    public static string ResolveSqlite(string connectionString)
        => ResolveSqlite(connectionString, AppContext.BaseDirectory);

    public static string ResolveSqlite(string connectionString, string baseDirectory)
    {
        var index = connectionString.IndexOf(Key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return connectionString;

        var start = index + Key.Length;
        var end = connectionString.IndexOf(';', start);
        var path = (end < 0 ? connectionString[start..] : connectionString[start..end]).Trim();

        // ":memory:" and friends are not file paths; absolute paths are already fine.
        if (path.Length == 0 || path.StartsWith(':') || Path.IsPathRooted(path))
            return connectionString;

        var absolute = Path.GetFullPath(Path.Combine(baseDirectory, path));
        var directory = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return connectionString[..start] + absolute + (end < 0 ? string.Empty : connectionString[end..]);
    }
}
