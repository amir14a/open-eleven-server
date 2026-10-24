// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.State;

/// <summary>
/// Ordered login progression. Dispatch compares the session's current value against the
/// minimum declared on a command, so a packet arriving out of sequence is rejected with
/// a logged reason instead of being handled against half-initialised state.
/// </summary>
public enum SessionState
{
    Connected = 0,
    EulaAccepted = 10,
    Challenged = 20,
    Authenticated = 30,
    PlayerSelected = 40,
    InBlock = 50,
    InRoom = 60,
    Matching = 70,
    InMatch = 80,
}
