// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Dispatch;

/// <summary>
/// Marks a handler method for one protocol command. Adding a command means adding a
/// method; nothing in the dispatch core changes.
/// Methods must have the signature
/// <c>ValueTask&lt;KvMessage[]&gt; Name(CommandContext ctx)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CommandAttribute(string msg) : Attribute
{
    public string Msg { get; } = msg;

    /// <summary>Service ports this command is accepted on.</summary>
    public ServiceRole Roles { get; init; } = ServiceRole.All;

    /// <summary>
    /// Titles this command registers for. The registry keeps the method only when the
    /// server's configured <see cref="GameProfile"/> overlaps this set, so a per-title
    /// variant declares its titles here instead of branching at runtime. Defaults to
    /// every title; commands shared by all titles leave this unset.
    /// </summary>
    public GameProfile Profiles { get; init; } = GameProfile.All;

    /// <summary>Minimum session state; anything earlier is rejected before the handler runs.</summary>
    public SessionState RequiredState { get; init; } = SessionState.Connected;
}
