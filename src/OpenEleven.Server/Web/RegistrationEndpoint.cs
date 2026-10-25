// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text.Json.Serialization;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;

namespace OpenEleven.Server.Web;

/// <summary>
/// Account creation request. The password arrives already hashed — the server never sees,
/// stores or transports a plaintext password.
/// </summary>
public sealed record RegisterAccountRequest(
    [property: JsonPropertyName("gameId")] string? GameId,
    [property: JsonPropertyName("passwordHash")] string? PasswordHash,
    [property: JsonPropertyName("regCode")] string? RegCode,
    [property: JsonPropertyName("captchaToken")] string? CaptchaToken = null);

public sealed record RegisterAccountResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("gameId")] string GameId,
    [property: JsonPropertyName("regCode")] string? RegCode);

public static class RegistrationEndpoint
{
    /// <summary>Maximum Game ID length accepted by the PES2010 client input field.</summary>
    public const int MaxGameIdLength = 32;
    public const int PasswordHashLength = 32;
    public const int MaxRegCodeLength = 64;

    /// <summary>Maximum plaintext password length accepted by the PES2010 client input field.</summary>
    public const int MaxPasswordLength = 16;

    /// <summary>
    /// Validates the request without touching the database.
    /// Returns null when the request is acceptable.
    /// </summary>
    public static string? Validate(RegisterAccountRequest request)
    {
        var gameId = request.GameId?.Trim();
        var passwordHash = request.PasswordHash?.Trim();
        var regCode = request.RegCode?.Trim();

        if (string.IsNullOrEmpty(gameId))
            return "gameId is required.";
        if (gameId.Length > MaxGameIdLength)
            return $"gameId must be at most {MaxGameIdLength} characters.";
        if (gameId.Contains('\r') || gameId.Contains('\n'))
            return "gameId cannot contain line breaks.";

        if (string.IsNullOrEmpty(passwordHash))
            return "passwordHash is required.";
        if (!AuthProof.TryNormalizeMd5(passwordHash, out _))
            return $"passwordHash must contain exactly {PasswordHashLength} hexadecimal characters.";

        if (string.IsNullOrEmpty(regCode))
            return "regCode is required.";
        if (regCode.Length > MaxRegCodeLength)
            return $"regCode must be at most {MaxRegCodeLength} characters.";

        return null;
    }

    /// <summary>
    /// Validates the plaintext a person typed into the registration form, before it is
    /// hashed. Returns null when the password is acceptable.
    /// </summary>
    /// <remarks>
    /// The printable-ASCII restriction is what makes <see cref="AuthProof.HashPassword"/>
    /// safe: the encoding the game uses is unknown, and within 0x20-0x7E every candidate
    /// encoding produces identical bytes. Accepting anything wider risks an account that
    /// registers cleanly and can never authenticate from the game.
    /// The password is deliberately not trimmed — leading and trailing spaces are part of
    /// what the player typed, and stripping them here would change the digest.
    /// </remarks>
    public static string? ValidatePassword(string? password, string? confirm)
    {
        if (string.IsNullOrEmpty(password))
            return "A password is required.";

        if (password.Length > MaxPasswordLength)
            return $"The password must be at most {MaxPasswordLength} characters.";

        foreach (var character in password)
        {
            if (character is < ' ' or > '~')
                return "The password may only contain printable ASCII characters.";
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
            return "The two passwords do not match.";

        return null;
    }

    public static string Describe(RegistrationResult result) => result switch
    {
        RegistrationResult.GameIdTaken => "That gameId is already registered.",
        _ => "Created.",
    };
}
