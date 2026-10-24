// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Security.Cryptography;
using System.Text;

namespace OpenEleven.Protocol.Crypto;

/// <summary>Challenge proof helpers used by the login handshake (shared by all supported titles).</summary>
public static class AuthProof
{
    public static bool TryNormalizeMd5(string? value, out string normalized)
    {
        normalized = "";
        if (value is null || value.Length != 32)
            return false;

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        normalized = value.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// MD5 of the ASCII password bytes. This is the exact digest the game client posts as
    /// <c>game_id_password</c>, and therefore the value that has to end up in
    /// <c>Account.PasswordHash</c> for a registered account to be able to log in.
    /// </summary>
    /// <remarks>
    /// ASCII, deliberately. The encoding the game uses for the password it digests is
    /// unknown, so callers must reject anything outside printable ASCII first — within that
    /// range ASCII, UTF-8, CP1252 and Shift-JIS all agree byte for byte, which makes the
    /// question moot. <see cref="Encoding.ASCII"/> substitutes '?' silently otherwise, which
    /// would produce an account that registers cleanly and can never authenticate.
    /// The password must never be trimmed: that would change the digest away from what the
    /// player typed into the game.
    /// </remarks>
    public static string HashPassword(string password)
        => Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(password)));

    public static bool TryDecodeChallenge(string? challengeCode, out byte[] bytes)
    {
        bytes = [];
        if (!TryNormalizeMd5(challengeCode, out var normalized))
            return false;

        bytes = Convert.FromHexString(normalized);
        return true;
    }

    /// <summary>
    /// Computes MD5(ASCII(credential) || raw challenge bytes || ASCII(gameId)).
    /// The challenge is decoded from hex before hashing.
    /// </summary>
    public static string Compute(string credential, string challengeCode, string gameId)
    {
        if (!TryDecodeChallenge(challengeCode, out var challenge))
            throw new ArgumentException("Challenge code must contain exactly 32 hexadecimal characters.", nameof(challengeCode));

        var credentialBytes = Encoding.ASCII.GetBytes(credential);
        var gameIdBytes = Encoding.ASCII.GetBytes(gameId);
        var input = new byte[credentialBytes.Length + challenge.Length + gameIdBytes.Length];

        credentialBytes.CopyTo(input, 0);
        challenge.CopyTo(input, credentialBytes.Length);
        gameIdBytes.CopyTo(input, credentialBytes.Length + challenge.Length);

        return Convert.ToHexStringLower(MD5.HashData(input));
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
