// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server;
using OpenEleven.Server.Configuration;

// Pes2011Pc: one process serves exactly one title. The profile selects which
// profile-gated commands register and which profile assembly is scanned.
return await OpenElevenServerHost.Run(GameProfile.Pes2011Pc, args);