// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.Configuration;

/// <summary>
/// Implemented once per per-title profile assembly. The host locates the referenced
/// profile assembly by finding its <see cref="IProfileMarker"/> and uses the assembly it
/// lives in as the extra command-registry scan source.
/// </summary>
public interface IProfileMarker
{
    GameProfile Profile { get; }
}