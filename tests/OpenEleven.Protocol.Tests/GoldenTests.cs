// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Framing;
using Xunit;

namespace OpenEleven.Protocol.Tests;

/// <summary>
/// Byte-for-byte comparison against vectors produced by the reference Python server.
/// Regenerate with <c>python tools/generate_goldens.py</c>; the generator imports
/// main.py read-only.
/// </summary>
public class GoldenTests
{
    private static readonly Goldens Data = Load();

    private readonly XorCipher _xor = new(Data.XorKey);
    private readonly BlowfishEcb _blowfish = new(Data.BlowfishKey);
    private readonly PacketCodec _codec = new();

    [Fact]
    public void Keys_match_the_reference_server()
    {
        Assert.Equal("5B9F2E64", Data.XorKey);
        Assert.Equal(56, Convert.FromHexString(Data.BlowfishKey).Length);
    }

    [Fact]
    public void Inner_bodies_are_byte_identical()
    {
        Assert.NotEmpty(Data.InnerBodies);

        foreach (var vector in Data.InnerBodies)
            Assert.Equal(vector.Wrapped, Convert.ToHexString(InnerBody.Wrap(vector.Payload)));
    }

    [Fact]
    public void Blowfish_bodies_are_byte_identical()
    {
        foreach (var vector in Data.BlowfishBlocks)
        {
            var encrypted = _blowfish.Encrypt(InnerBody.Wrap(vector.Payload));

            Assert.Equal(vector.Encrypted, Convert.ToHexString(encrypted));
            Assert.Equal(vector.Payload, InnerBody.Unwrap(_blowfish.Decrypt(encrypted)));
        }
    }

    [Fact]
    public void Wire_frames_are_byte_identical()
    {
        foreach (var vector in Data.Packets)
        {
            var body = Convert.FromHexString(vector.Body);
            var wire = _codec.Write((ushort)vector.PacketId, (uint)vector.Count, body, _xor);

            Assert.Equal(vector.Wire, Convert.ToHexString(wire));
        }
    }

    [Fact]
    public void Reference_frames_parse_back()
    {
        foreach (var vector in Data.Packets)
        {
            var buffer = new ReadOnlySequence<byte>(Convert.FromHexString(vector.Wire));

            Assert.True(_codec.TryRead(ref buffer, _xor, out var frame));
            Assert.Equal((ushort)vector.PacketId, frame.Id);
            Assert.Equal((uint)vector.Count, frame.Count);
            Assert.Equal(vector.Body, Convert.ToHexString(frame.Data));
        }
    }

    private static Goldens Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "goldens.json");
        return JsonSerializer.Deserialize<Goldens>(File.ReadAllText(path))
               ?? throw new InvalidOperationException("goldens.json is empty.");
    }

    public sealed record Goldens(
        [property: JsonPropertyName("xorKey")] string XorKey,
        [property: JsonPropertyName("blowfishKey")] string BlowfishKey,
        [property: JsonPropertyName("innerBodies")] IReadOnlyList<InnerVector> InnerBodies,
        [property: JsonPropertyName("blowfishBlocks")] IReadOnlyList<BlowfishVector> BlowfishBlocks,
        [property: JsonPropertyName("packets")] IReadOnlyList<PacketVector> Packets,
        [property: JsonPropertyName("responses")] IReadOnlyList<ResponseVector> Responses);

    public sealed record InnerVector(
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("wrapped")] string Wrapped);

    public sealed record BlowfishVector(
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("encrypted")] string Encrypted);

    public sealed record PacketVector(
        [property: JsonPropertyName("packetId")] int PacketId,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("wire")] string Wire);

    public sealed record ResponseVector(
        [property: JsonPropertyName("msg")] string Msg,
        [property: JsonPropertyName("rqid")] int Rqid,
        [property: JsonPropertyName("text")] string Text);
}
