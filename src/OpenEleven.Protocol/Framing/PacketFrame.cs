// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Protocol.Framing;

/// <summary>One decrypted outer packet: header fields plus the still-Blowfished body.</summary>
public readonly record struct PacketFrame(ushort Id, uint Count, byte[] Data)
{
    public const ushort TextCommand = 0x0060;

    public bool IsTextCommand => Id == TextCommand;
}
