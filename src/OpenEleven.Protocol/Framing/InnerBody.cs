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
/// [N] UTF-8 key=value payload (lengths count bytes, not characters)
/// [P] NUL padding so that 9 + N + P is a multiple of 8
/// </code>
/// The recorded length is the <b>unpadded</b> payload length.
/// </summary>
public static class InnerBody
{
    public const int HeaderSize = 9;

    public static byte[] Wrap(string payloadText, byte flag = 0)
    {
        var payload = Encoding.UTF8.GetBytes(payloadText);
        var padding = (BlowfishEcb.BlockSize - (HeaderSize + payload.Length) % BlowfishEcb.BlockSize)
                      % BlowfishEcb.BlockSize;

        var body = new byte[HeaderSize + payload.Length + padding];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4, 4), payload.Length);
        body[8] = flag;
        payload.CopyTo(body, HeaderSize);
        return body;
    }

    /// <summary>Largest frame body (after padding) the client's frame reader accepts.</summary>
    public const int MaxFrameBody = 0x400;

    public const ushort Single = 0x0060, ChunkStart = 0x0070, ChunkMiddle = 0x0071, ChunkEnd = 0x0072;

    /// <summary>Largest message the client reassembles (pes2011.exe checks cursor + chunk &lt;= 0x19000).</summary>
    public const int MaxMessage = 0x19000;

    private const int ChunkData = 1000;

    /// <summary>
    /// One 0x0060 frame when the text fits in <see cref="MaxFrameBody"/>, otherwise the
    /// 0x0070 / 0x0071... / 0x0072 chunk sequence (uncompressed, flag 0). Bodies are plain
    /// and padded to the Blowfish block; the caller encrypts each.
    /// </summary>
    public static IReadOnlyList<(ushort Id, byte[] Body)> WrapChunked(string payloadText)
    {
        var payload = Encoding.UTF8.GetBytes(payloadText);
        if (HeaderSize + payload.Length + Pad(HeaderSize + payload.Length) <= MaxFrameBody)
            return [(Single, Wrap(payloadText))];

        if (payload.Length > MaxMessage)
            throw new ProtocolException($"Message of {payload.Length} bytes exceeds the client limit of {MaxMessage}.");

        var frames = new List<(ushort, byte[])>();
        for (var offset = 0; offset < payload.Length; offset += ChunkData)
        {
            var count = Math.Min(ChunkData, payload.Length - offset);
            var first = offset == 0;
            var last = offset + count >= payload.Length;
            var header = first ? 13 : 4;
            var body = new byte[header + count + Pad(header + count)];
            var at = 0;
            if (first)
            {
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), payload.Length); // X total
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4, 4), count);          // C this chunk
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8, 4), payload.Length); // Y inflated
                body[12] = 0;                                                            // flag
                at = 13;
            }
            else
            {
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), count);
                at = 4;
            }

            payload.AsSpan(offset, count).CopyTo(body.AsSpan(at));
            frames.Add((first ? ChunkStart : last ? ChunkEnd : ChunkMiddle, body));
        }

        return frames;
    }

    private static int Pad(int length) => (BlowfishEcb.BlockSize - length % BlowfishEcb.BlockSize) % BlowfishEcb.BlockSize;

    /// <summary>Extracts the UTF-8 payload from a decrypted inner body.</summary>
    public static string Unwrap(ReadOnlySpan<byte> inner)
    {
        if (inner.Length < HeaderSize)
        {
            // Short bodies were observed in the reference implementation; fall back to
            // scanning the whole buffer rather than throwing, so RE logging still works.
            return Encoding.UTF8.GetString(inner).TrimEnd('\0');
        }

        var declared = BinaryPrimitives.ReadInt32BigEndian(inner[..4]);
        if (declared < 0 || HeaderSize + declared > inner.Length)
            throw new ProtocolException(
                $"Inner body declares {declared} payload bytes but only " +
                $"{inner.Length - HeaderSize} are present.");

        return Encoding.UTF8.GetString(inner.Slice(HeaderSize, declared)).TrimEnd('\0');
    }
}
