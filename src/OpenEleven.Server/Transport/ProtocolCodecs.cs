// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Framing;
using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.Transport;

/// <summary>
/// Bundle of the stateless protocol services. Registered once as a singleton so a
/// connection takes a single dependency instead of five.
/// </summary>
public sealed class ProtocolCodecs(
    XorCipher xor,
    BlowfishEcb blowfish,
    PacketCodec packets,
    KvReader reader,
    KvWriter writer)
{
    public XorCipher Xor { get; } = xor;
    public BlowfishEcb Blowfish { get; } = blowfish;
    public PacketCodec Packets { get; } = packets;
    public KvReader Reader { get; } = reader;
    public KvWriter Writer { get; } = writer;
}
