// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using System.Threading.RateLimiting;

namespace OpenEleven.Server.State;

/// <summary>
/// Password checks per source address. Shared by /gameid_auth and CMD_AUTH_GAMEID, so
/// alternating the two paths does not double the guesses.
/// </summary>
public sealed class LoginThrottle : IDisposable
{
    // ponytail: fixed numbers, generous enough for a LAN party behind one address; make them
    // configurable if a host ever needs more.
    public const int AttemptsPerMinute = 20;

    private readonly PartitionedRateLimiter<IPAddress> _limiter = PartitionedRateLimiter.Create<IPAddress, IPAddress>(
        address => RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = AttemptsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        }));

    /// <summary>Counts one attempt; false once the address has used up this minute's.</summary>
    public bool TryAttempt(IPAddress address)
    {
        using var lease = _limiter.AttemptAcquire(address);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
