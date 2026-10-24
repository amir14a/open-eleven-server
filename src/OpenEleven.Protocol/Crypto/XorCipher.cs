// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Protocol.Crypto;

/// <summary>
/// Rolling XOR stream cipher used on every byte of the outer packet.
/// </summary>
/// <remarks>
/// The keystream is aligned to the <b>start of each packet frame</b>, not to the
/// TCP stream position. The reference Python implementation got away with never
/// stating this because it XOR-decrypted each <c>recv()</c> buffer from index 0
/// and only ever parsed the first frame in it; with pipelined reads a frame can
/// be split or coalesced, so the alignment has to be explicit. Callers pass the
/// byte offset of the slice relative to the frame start.
/// </remarks>
public sealed class XorCipher
{
    private readonly byte[] _key;

    public XorCipher(byte[] key)
    {
        if (key is null || key.Length == 0)
            throw new ArgumentException("XOR key must not be empty.", nameof(key));
        _key = (byte[])key.Clone();
    }

    public XorCipher(string hexKey)
        : this(Convert.FromHexString(hexKey))
    {
    }

    public int KeyLength => _key.Length;

    /// <summary>XOR is its own inverse, so this both encrypts and decrypts.</summary>
    /// <param name="buffer">Slice to transform in place.</param>
    /// <param name="offset">Byte offset of <paramref name="buffer"/> from the frame start.</param>
    public void Transform(Span<byte> buffer, int offset)
    {
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] ^= _key[(offset + i) % _key.Length];
    }

    public byte[] Transform(ReadOnlySpan<byte> buffer, int offset = 0)
    {
        var copy = buffer.ToArray();
        Transform(copy, offset);
        return copy;
    }
}
