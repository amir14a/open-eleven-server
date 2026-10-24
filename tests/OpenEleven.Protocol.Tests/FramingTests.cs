// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Buffers;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Framing;
using Xunit;

namespace OpenEleven.Protocol.Tests;

public class XorCipherTests
{
    private static readonly XorCipher Cipher = new("5B9F2E64");

    [Fact]
    public void Is_its_own_inverse()
    {
        var plain = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        var encrypted = Cipher.Transform(plain);
        var decrypted = Cipher.Transform(encrypted);

        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void Offset_continues_the_keystream()
    {
        var whole = new byte[] { 10, 20, 30, 40, 50, 60 };

        var atOnce = Cipher.Transform(whole);

        var head = Cipher.Transform((ReadOnlySpan<byte>)whole.AsSpan(0, 4), offset: 0);
        var tail = Cipher.Transform((ReadOnlySpan<byte>)whole.AsSpan(4), offset: 4);

        Assert.Equal(atOnce, head.Concat(tail));
    }
}

public class PacketCodecTests
{
    private static readonly XorCipher Cipher = new("5B9F2E64");
    private readonly PacketCodec _codec = new();

    [Fact]
    public void Round_trips_a_frame()
    {
        var body = "hello world!"u8.ToArray();
        var wire = _codec.Write(0x0060, 7, body, Cipher);

        var buffer = new ReadOnlySequence<byte>(wire);
        Assert.True(_codec.TryRead(ref buffer, Cipher, out var frame));

        Assert.Equal((ushort)0x0060, frame.Id);
        Assert.Equal(7u, frame.Count);
        Assert.Equal(body, frame.Data);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void Wire_size_is_body_plus_24()
    {
        var wire = _codec.Write(0x0060, 1, new byte[40], Cipher);

        Assert.Equal(64, wire.Length);
    }

    [Fact]
    public void Waits_for_a_partial_frame()
    {
        var wire = _codec.Write(0x0060, 1, new byte[16], Cipher);

        var partial = new ReadOnlySequence<byte>(wire.AsMemory(0, wire.Length - 4));
        Assert.False(_codec.TryRead(ref partial, Cipher, out _));
    }

    [Fact]
    public void Reads_two_frames_coalesced_into_one_buffer()
    {
        var first = _codec.Write(0x0060, 1, "one"u8.ToArray(), Cipher);
        var second = _codec.Write(0x0005, 2, "two"u8.ToArray(), Cipher);

        var buffer = new ReadOnlySequence<byte>(first.Concat(second).ToArray());

        Assert.True(_codec.TryRead(ref buffer, Cipher, out var a));
        Assert.True(_codec.TryRead(ref buffer, Cipher, out var b));

        Assert.Equal("one"u8.ToArray(), a.Data);
        Assert.Equal((ushort)0x0005, b.Id);
        Assert.Equal("two"u8.ToArray(), b.Data);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void Reads_a_frame_split_across_two_pipe_segments()
    {
        var wire = _codec.Write(0x0060, 3, "split me up"u8.ToArray(), Cipher);

        // Mirrors what PipeReader hands over when a frame straddles two reads.
        var first = new MemorySegment<byte>(wire.AsMemory(0, 10));
        var second = first.Append(wire.AsMemory(10));
        var buffer = new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);

        Assert.True(_codec.TryRead(ref buffer, Cipher, out var frame));
        Assert.Equal("split me up"u8.ToArray(), frame.Data);
    }

    [Fact]
    public void Rejects_a_corrupted_body()
    {
        var wire = _codec.Write(0x0060, 1, "payload!"u8.ToArray(), Cipher);
        wire[^1] ^= 0xFF;

        var buffer = new ReadOnlySequence<byte>(wire);
        Assert.Throws<ProtocolException>(() => { var b = buffer; _codec.TryRead(ref b, Cipher, out _); });
    }

    private sealed class MemorySegment<T> : ReadOnlySequenceSegment<T>
    {
        public MemorySegment(ReadOnlyMemory<T> memory) => Memory = memory;

        public MemorySegment<T> Append(ReadOnlyMemory<T> memory)
        {
            var segment = new MemorySegment<T>(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}

public class InnerBodyTests
{
    [Fact]
    public void Pads_to_a_blowfish_block()
    {
        // 9 header bytes + 5 payload bytes = 14, so 2 bytes of padding reach 16.
        var body = InnerBody.Wrap("hello");

        Assert.Equal(16, body.Length);
        Assert.Equal(0, body.Length % BlowfishEcb.BlockSize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("1234567")]
    [InlineData("result=\"NOERR\",msg=\"CMD_GET_SVRTIME\",rqid=1,date=0\0")]
    [InlineData("mes_subject=\"À propos d'OpenEleven\",name=\"Быстрый матч\",x=\"快速比賽\"")]
    public void Round_trips_the_payload(string payload)
    {
        var body = InnerBody.Wrap(payload);

        Assert.Equal(0, body.Length % BlowfishEcb.BlockSize);
        Assert.Equal(payload.TrimEnd('\0'), InnerBody.Unwrap(body));
    }

    [Fact]
    public void Records_the_unpadded_length_twice()
    {
        var body = InnerBody.Wrap("abc");

        Assert.Equal(body[0..4], body[4..8]);
        Assert.Equal((byte)3, body[3]);
    }
}

public class BlowfishTests
{
    private const string Key =
        "D8890AF066C96B40D701AEFC436FF9FEC98998167A74483D" +
        "3914730C5C01C03CE28E86E589C4A185F8540651D2ECA36B" +
        "5C1A40EEC5E9DAAE";

    [Fact]
    public void Round_trips_a_block()
    {
        var cipher = new BlowfishEcb(Key);
        var plain = InnerBody.Wrap("result=\"NOERR\",msg=\"MSG_CHALLENGE\",rqid=1");

        Assert.Equal(plain, cipher.Decrypt(cipher.Encrypt(plain)));
    }

    [Fact]
    public void Rejects_input_that_is_not_block_aligned()
    {
        var cipher = new BlowfishEcb(Key);

        Assert.Throws<ArgumentException>(() => cipher.Encrypt(new byte[7]));
    }
}
