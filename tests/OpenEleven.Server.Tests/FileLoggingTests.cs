// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using OpenEleven.Server.Logging;

namespace OpenEleven.Server.Tests;

/// <summary>
/// The log file is the only durable record of a client attempt, so it has to actually be
/// written, created on demand, and harmless when the path is unusable.
/// </summary>
public class FileLoggingTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openeleven-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Writes_the_line_with_its_category_and_level()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "server.log");

        try
        {
            using (var provider = new FileLoggerProvider(path))
            {
                provider.CreateLogger("OpenEleven.Test").LogInformation("hello {Value}", 42);
            }

            var text = File.ReadAllText(path);

            Assert.Contains("[info]", text);
            Assert.Contains("OpenEleven.Test", text);
            Assert.Contains("hello 42", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Creates_the_missing_directory()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "nested", "deeper", "server.log");

        try
        {
            using (var provider = new FileLoggerProvider(path))
                provider.CreateLogger("C").LogWarning("nested");

            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_bad_path_reports_closed_instead_of_throwing()
    {
        // A directory that cannot be created (a file already occupies the name).
        var directory = TempDirectory();
        var blocker = Path.Combine(directory, "blocker");
        File.WriteAllText(blocker, "x");

        try
        {
            using var provider = new FileLoggerProvider(Path.Combine(blocker, "server.log"));

            Assert.False(provider.IsOpen);
            // Logging through a closed provider must be a no-op, not a crash.
            provider.CreateLogger("C").LogError("dropped");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Exceptions_are_recorded_with_the_message()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "server.log");

        try
        {
            using (var provider = new FileLoggerProvider(path))
                provider.CreateLogger("C").LogError(new InvalidOperationException("boom"), "failed");

            var text = File.ReadAllText(path);

            Assert.Contains("failed", text);
            Assert.Contains("boom", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Appends_across_providers_rather_than_truncating()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "server.log");

        try
        {
            using (var first = new FileLoggerProvider(path))
                first.CreateLogger("C").LogInformation("first run");

            using (var second = new FileLoggerProvider(path))
                second.CreateLogger("C").LogInformation("second run");

            var text = File.ReadAllText(path);

            Assert.Contains("first run", text);
            Assert.Contains("second run", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
