// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OpenEleven.Protocol.Framing;

/// <summary>
/// Reassembles one connection's multi-frame messages (packets 0x0070 start, 0x0071 middle,
/// 0x0072 end), the layout pes2011.exe reads in FUN_01010da0 / FUN_01010380 / FUN_01010fe0:
/// <code>
/// 0x70: [X][C][Y][flag][C bytes]   0x71 / 0x72: [C][C bytes]      (big-endian u32)
/// </code>
/// X = total stored length, Y = final length, flag != 0 = the stored bytes are a zlib stream.
/// </summary>
public sealed class InnerBodyChunks
{
    /// <summary>Ceiling on Y, so a small zlib stream cannot inflate without bound.</summary>
    private const int MaxInflated = 1024 * 1024;

    private byte[]? _buffer;
    private int _filled;
    private int _inflated;
    private bool _compressed;

    public static bool IsChunkPacket(ushort id) => id is InnerBody.ChunkStart or InnerBody.ChunkMiddle or InnerBody.ChunkEnd;

    /// <summary>Feeds one decrypted frame body; returns the full text once the end chunk arrives.</summary>
    public string? Feed(ushort id, ReadOnlySpan<byte> inner)
    {
        var at = 0;
        if (id == InnerBody.ChunkStart)
        {
            if (inner.Length < 13)
                throw new ProtocolException("Chunk start frame is shorter than its 13-byte header.");

            var total = BinaryPrimitives.ReadInt32BigEndian(inner[..4]);
            _inflated = BinaryPrimitives.ReadInt32BigEndian(inner[8..12]);
            _compressed = inner[12] != 0;
            if (total < 0 || total > InnerBody.MaxMessage || _inflated < 0)
                throw new ProtocolException($"Chunked message declares {total} bytes, above the {InnerBody.MaxMessage} limit.");
            if (_compressed && _inflated > MaxInflated)
                throw new ProtocolException($"Chunked message inflates to {_inflated} bytes, above the {MaxInflated} limit.");

            _buffer = new byte[total];
            _filled = 0;
            at = 4;
            var chunk = BinaryPrimitives.ReadInt32BigEndian(inner[at..(at + 4)]);
            return Append(inner[13..], chunk, last: false);
        }

        if (_buffer is null)
            throw new ProtocolException($"Chunk packet 0x{id:X4} arrived without a 0x0070 start.");

        if (inner.Length < 4)
            throw new ProtocolException("Chunk frame is shorter than its length prefix.");

        var c = BinaryPrimitives.ReadInt32BigEndian(inner[..4]);
        return Append(inner[4..], c, last: id == InnerBody.ChunkEnd);
    }

    private string? Append(ReadOnlySpan<byte> data, int count, bool last)
    {
        if (count < 0 || count > data.Length || _filled + count > _buffer!.Length)
            throw new ProtocolException($"Chunk of {count} bytes does not fit the declared message.");

        data[..count].CopyTo(_buffer.AsSpan(_filled));
        _filled += count;
        if (!last)
            return null;

        var stored = _buffer.AsSpan(0, _filled).ToArray();
        _buffer = null;
        if (!_compressed)
            return Encoding.UTF8.GetString(stored).TrimEnd('\0');

        using var input = new MemoryStream(stored);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[_inflated];
        var length = 0;
        try
        {
            int read;
            while (length < output.Length && (read = zlib.Read(output, length, output.Length - length)) > 0)
                length += read;
            if (zlib.ReadByte() >= 0)
                throw new ProtocolException($"Chunked message inflates past its declared {_inflated} bytes.");
        }
        catch (InvalidDataException ex)
        {
            throw new ProtocolException($"Chunked message is not a valid zlib stream: {ex.Message}");
        }

        return Encoding.UTF8.GetString(output, 0, length).TrimEnd('\0');
    }
}
