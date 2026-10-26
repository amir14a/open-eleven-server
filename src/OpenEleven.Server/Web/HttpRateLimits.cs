// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace OpenEleven.Server.Web;

/// <summary>
/// Per-address limits on the HTTP routes anyone can call without logging in. Logins have their
/// own limit (<see cref="State.LoginThrottle"/>), shared with the socket path.
/// </summary>
public static class HttpRateLimits
{
    /// <summary>Account creation: posts only, so reloading the form costs nothing.</summary>
    public const string Register = "register";

    /// <summary>The ranking screen: every request reads the whole players table.</summary>
    public const string Ranking = "ranking";

    /// <summary>Admin sign-in attempts: posts only.</summary>
    public const string AdminLogin = "admin-login";

    // ponytail: fixed numbers; make them configurable if a host ever needs different ones.
    public static IServiceCollection AddHttpRateLimits(this IServiceCollection services)
        => services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limits.AddPolicy(Register, http => HttpMethods.IsPost(http.Request.Method)
                ? PerAddress(http, permits: 10, TimeSpan.FromHours(1))
                : RateLimitPartition.GetNoLimiter(""));
            limits.AddPolicy(Ranking, http => PerAddress(http, permits: 60, TimeSpan.FromMinutes(1)));
            limits.AddPolicy(AdminLogin, http => HttpMethods.IsPost(http.Request.Method)
                ? PerAddress(http, permits: 10, TimeSpan.FromMinutes(5))
                : RateLimitPartition.GetNoLimiter(""));
        });

    private static RateLimitPartition<string> PerAddress(HttpContext http, int permits, TimeSpan window)
        => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window });
}
