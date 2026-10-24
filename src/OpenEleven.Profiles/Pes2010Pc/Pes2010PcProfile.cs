// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Configuration;

namespace OpenEleven.Profiles.Pes2010Pc;

/// <summary>Marks the Pes2010Pc profile assembly so the host can discover it.</summary>
public sealed class Pes2010PcProfile : IProfileMarker
{
    public GameProfile Profile => GameProfile.Pes2010Pc;
}