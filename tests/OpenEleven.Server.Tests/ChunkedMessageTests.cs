// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.IO.Compression;
using System.Text;
using OpenEleven.Protocol.Framing;
using Xunit;

namespace OpenEleven.Server.Tests;

public class ChunkedMessageTests
{
    [Fact]
    public void Short_text_stays_a_single_0x60_frame()
    {
        var frames = InnerBody.WrapChunked(new string('a', 1015));
        var only = Assert.Single(frames);
        Assert.Equal(InnerBody.Single, only.Id);
        Assert.True(only.Body.Length <= InnerBody.MaxFrameBody);
    }

    [Theory]
    [InlineData(1016)]
    [InlineData(5000)]
    [InlineData(InnerBody.MaxMessage)]
    public void Long_text_is_chunked_within_frame_limit_and_reassembles(int length)
    {
        var text = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));
        var frames = InnerBody.WrapChunked(text);

        Assert.True(frames.Count > 1);
        Assert.Equal(InnerBody.ChunkStart, frames[0].Id);
        Assert.Equal(InnerBody.ChunkEnd, frames[^1].Id);
        Assert.All(frames, f => Assert.True(f.Body.Length <= InnerBody.MaxFrameBody && f.Body.Length % 8 == 0));
        Assert.All(frames.Skip(1).SkipLast(1), f => Assert.Equal(InnerBody.ChunkMiddle, f.Id));

        var chunks = new InnerBodyChunks();
        string? done = null;
        foreach (var (id, body) in frames)
            done = chunks.Feed(id, body);

        Assert.Equal(text, done);
    }

    [Fact]
    public void Compressed_flag_is_inflated()
    {
        var text = new string('x', 3000);
        var raw = Encoding.ASCII.GetBytes(text);
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        var stored = packed.ToArray();

        var body = new byte[13 + stored.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), stored.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4, 4), stored.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8, 4), raw.Length);
        body[12] = 1;
        stored.CopyTo(body, 13);

        var chunks = new InnerBodyChunks();
        Assert.Null(chunks.Feed(InnerBody.ChunkStart, body));
        var end = new byte[4];
        Assert.Equal(text, chunks.Feed(InnerBody.ChunkEnd, end));
    }
}
