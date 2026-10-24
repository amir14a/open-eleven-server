// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace OpenEleven.Protocol.Crypto;

/// <summary>
/// Blowfish in ECB mode with no padding, matching PyCryptodome's
/// <c>Blowfish.new(key, Blowfish.MODE_ECB)</c>. Used for the body of packet 0x0060.
/// </summary>
public sealed class BlowfishEcb
{
    public const int BlockSize = 8;

    private readonly byte[] _key;

    public BlowfishEcb(byte[] key)
    {
        if (key is null || key.Length is < 4 or > 56)
            throw new ArgumentException("Blowfish key must be 4-56 bytes.", nameof(key));
        _key = (byte[])key.Clone();
    }

    public BlowfishEcb(string hexKey)
        : this(Convert.FromHexString(hexKey))
    {
    }

    public byte[] Encrypt(ReadOnlySpan<byte> data) => Process(data, forEncryption: true);

    public byte[] Decrypt(ReadOnlySpan<byte> data) => Process(data, forEncryption: false);

    private byte[] Process(ReadOnlySpan<byte> data, bool forEncryption)
    {
        if (data.Length % BlockSize != 0)
            throw new ArgumentException(
                $"Blowfish-ECB input must be a multiple of {BlockSize} bytes (got {data.Length}).",
                nameof(data));

        var engine = new BlowfishEngine();
        engine.Init(forEncryption, new KeyParameter(_key));

        var input = data.ToArray();
        var output = new byte[input.Length];
        for (var offset = 0; offset < input.Length; offset += BlockSize)
            engine.ProcessBlock(input, offset, output, offset);

        return output;
    }
}
