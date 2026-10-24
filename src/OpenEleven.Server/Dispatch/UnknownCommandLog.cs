// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Dispatch;

/// <summary>
/// Tallies commands that reached dispatch without a handler. A warning per occurrence
/// drowns in a polling loop; a single ranked summary at shutdown is the reverse-engineering
/// worklist, ordered by how badly the client wants each one.
/// </summary>
public sealed class UnknownCommandLog(ILogger<UnknownCommandLog> log, GameProfile profile)
{
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();

    private readonly record struct Key(string Msg, ServiceRole Role);

    private sealed class Entry
    {
        public long Count;
        public SessionState FirstSeenState;
        public string SampleRequest = "";
    }

    public void Record(string msg, ServiceRole role, SessionState state, string request)
    {
        var entry = _entries.GetOrAdd(new Key(msg, role), _ => new Entry
        {
            FirstSeenState = state,
            SampleRequest = request,
        });

        Interlocked.Increment(ref entry.Count);
    }

    public void Report()
    {
        if (_entries.IsEmpty)
        {
            log.LogInformation("No unhandled commands were seen this session ({Profile}).", profile);
            return;
        }

        var lines = _entries
            .OrderByDescending(e => e.Value.Count)
            .Select(e => string.Format(
                "  {0,-28} {1,-8} x{2,-5} first seen in {3,-14} {4}",
                e.Key.Msg,
                e.Key.Role,
                e.Value.Count,
                e.Value.FirstSeenState,
                e.Value.SampleRequest));

        log.LogWarning(
            "{Count} command(s) had no handler this session ({Profile}):\n{Lines}",
            _entries.Count,
            profile,
            string.Join('\n', lines));
    }
}
