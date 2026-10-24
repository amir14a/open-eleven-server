// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenEleven.Protocol.Crypto;

namespace OpenEleven.Protocol.Framing;

/// <summary>
/// Outer packet framing.
/// <code>
/// [2]  packet id      big-endian uint16
/// [2]  data length     big-endian uint16
/// [4]  packet counter  big-endian uint32
/// [16] MD5             over (id|length|counter) + data
/// [N]  data
/// </code>
/// Whole frame is XOR-encrypted with the keystream aligned to the frame start.
/// </summary>
public sealed class PacketCodec
{
    public const int HeaderSize = 24;
    public const int PrefixSize = 8;   // id + length + counter, the part covered by MD5
    public const int HashSize = 16;

    /// <summary>
    /// Tries to decrypt and parse a single frame from the front of <paramref name="buffer"/>.
    /// On success the buffer is advanced past the frame.
    /// </summary>
    public bool TryRead(ref ReadOnlySequence<byte> buffer, XorCipher xor, out PacketFrame frame)
    {
        frame = default;

        if (buffer.Length < HeaderSize)
            return false;

        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);
        xor.Transform(header, offset: 0);

        var id = BinaryPrimitives.ReadUInt16BigEndian(header[..2]);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header[2..4]);
        var count = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);

        var total = HeaderSize + length;
        if (buffer.Length < total)
            return false;                       // partial frame, wait for more bytes

        var data = new byte[length];
        buffer.Slice(HeaderSize, length).CopyTo(data);
        xor.Transform(data, offset: HeaderSize);

        if (!VerifyHash(header[..PrefixSize], data, header.Slice(PrefixSize, HashSize)))
            throw new ProtocolException(
                $"MD5 mismatch on packet id=0x{id:X4} length={length} count={count}");

        frame = new PacketFrame(id, count, data);
        buffer = buffer.Slice(total);
        return true;
    }

    /// <summary>Builds a complete encrypted frame ready for the wire.</summary>
    public byte[] Write(ushort packetId, uint count, ReadOnlySpan<byte> data, XorCipher xor)
    {
        if (data.Length > ushort.MaxValue)
            throw new ProtocolException($"Packet body too large: {data.Length} bytes.");

        var wire = new byte[HeaderSize + data.Length];
        var span = wire.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span[..2], packetId);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], (ushort)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], count);
        data.CopyTo(span[HeaderSize..]);

        ComputeHash(span[..PrefixSize], data, span.Slice(PrefixSize, HashSize));

        xor.Transform(span, offset: 0);
        return wire;
    }

    private static bool VerifyHash(
        ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[HashSize];
        ComputeHash(prefix, data, actual);
        return actual.SequenceEqual(expected);
    }

    private static void ComputeHash(
        ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var buffer = new byte[prefix.Length + data.Length];
        prefix.CopyTo(buffer);
        data.CopyTo(buffer.AsSpan(prefix.Length));
        MD5.HashData(buffer, destination);
    }
}
