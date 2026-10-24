// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Buffers.Binary;
using System.Text;
using OpenEleven.Protocol.Crypto;

namespace OpenEleven.Protocol.Framing;

/// <summary>
/// The body of packet 0x0060, sitting inside the Blowfish layer.
/// <code>
/// [4] payload length (big-endian, repeated)
/// [4] payload length
/// [1] flag
/// [N] ASCII key=value payload
/// [P] NUL padding so that 9 + N + P is a multiple of 8
/// </code>
/// The recorded length is the <b>unpadded</b> payload length.
/// </summary>
public static class InnerBody
{
    public const int HeaderSize = 9;

    public static byte[] Wrap(string payloadText, byte flag = 0)
    {
        var payload = Encoding.ASCII.GetBytes(payloadText);
        var padding = (BlowfishEcb.BlockSize - (HeaderSize + payload.Length) % BlowfishEcb.BlockSize)
                      % BlowfishEcb.BlockSize;

        var body = new byte[HeaderSize + payload.Length + padding];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4, 4), payload.Length);
        body[8] = flag;
        payload.CopyTo(body, HeaderSize);
        return body;
    }

    /// <summary>Extracts the ASCII payload from a decrypted inner body.</summary>
    public static string Unwrap(ReadOnlySpan<byte> inner)
    {
        if (inner.Length < HeaderSize)
        {
            // Short bodies were observed in the reference implementation; fall back to
            // scanning the whole buffer rather than throwing, so RE logging still works.
            return Encoding.ASCII.GetString(inner).TrimEnd('\0');
        }

        var declared = BinaryPrimitives.ReadInt32BigEndian(inner[..4]);
        if (declared < 0 || HeaderSize + declared > inner.Length)
            throw new ProtocolException(
                $"Inner body declares {declared} payload bytes but only " +
                $"{inner.Length - HeaderSize} are present.");

        return Encoding.ASCII.GetString(inner.Slice(HeaderSize, declared)).TrimEnd('\0');
    }
}
