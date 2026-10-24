// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.Web;

/// <summary>
/// Marks an endpoint whose request body must never be written to the log.
/// <see cref="HttpTraceMiddleware"/> hex-dumps every body it sees, which is exactly what the
/// reverse-engineering work needs from the game client's traffic and exactly what must not
/// happen to a registration form carrying a password.
/// </summary>
/// <remarks>
/// Applies to the JSON API as well as the form. The digest that API carries is a
/// password-equivalent bearer token — <c>GameIdAuthService</c> compares it verbatim against
/// the stored hash — so logging it is no better than logging the plaintext.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SensitiveBodyAttribute : Attribute;
