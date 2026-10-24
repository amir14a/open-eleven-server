// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Crypto;
using Xunit;

namespace OpenEleven.Protocol.Tests;

/// <summary>
/// Locks the digest contract. Everything downstream — registration, the HTTP login the game
/// performs, and the credential it presents on the game socket — depends on this producing
/// exactly what pes2010.exe produces from the same typed password.
/// </summary>
public class HashPasswordTests
{
    [Fact]
    public void Matches_the_digest_captured_from_the_reference_implementation()
    {
        // The reference server's local session credential, and the value the real client
        // was observed posting as game_id_password.
        Assert.Equal("86d84f975c5afebdea53f5ec3c6abbde", AuthProof.HashPassword("local-session"));
    }

    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("a", "0cc175b9c0f1b6a831c399e269772661")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b")]
    public void Matches_the_rfc_1321_vectors(string input, string expected)
        => Assert.Equal(expected, AuthProof.HashPassword(input));

    [Fact]
    public void Produces_a_value_the_rest_of_the_stack_accepts_as_a_digest()
    {
        Assert.True(AuthProof.TryNormalizeMd5(AuthProof.HashPassword("whatever"), out var normalized));
        Assert.Equal(32, normalized.Length);
    }

    [Fact]
    public void Preserves_surrounding_whitespace()
    {
        // Trimming would change the digest away from what the player typed into the game.
        Assert.NotEqual(AuthProof.HashPassword("secret"), AuthProof.HashPassword(" secret "));
    }

    [Fact]
    public void Is_case_sensitive()
        => Assert.NotEqual(AuthProof.HashPassword("Secret"), AuthProof.HashPassword("secret"));
}
