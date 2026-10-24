// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Options;
using OpenEleven.Protocol.Crypto;
using System.Text;

namespace OpenEleven.Server.Configuration;

/// <summary>
/// Cross-field checks that data annotations cannot express. Runs at startup and again on
/// every reload, so a bad edit to the YAML is reported instead of half-applied.
/// </summary>
public sealed class ServerOptionsValidator : IValidateOptions<ServerOptions>
{
    public ValidateOptionsResult Validate(string? name, ServerOptions options)
    {
        var failures = new List<string>();

        // GameProfile must be exactly one named title: undefined bits, zero, flag
        // combinations and All are all rejected because one process serves one title.
        if (!Enum.IsDefined(options.GameProfile) || options.GameProfile == GameProfile.All)
            failures.Add(
                "Server.GameProfile must name exactly one title " +
                "(Pes2010Pc, Pes2011Pc, Pes2012Pc or Pes2013Pc).");

        if (!AuthProof.TryDecodeChallenge(options.Protocol.ChallengeCode, out _))
            failures.Add("Protocol.ChallengeCode must contain exactly 32 hexadecimal characters.");

        if (options.Protocol.PendingLoginLifetimeSeconds is < 30 or > 3600)
            failures.Add("Protocol.PendingLoginLifetimeSeconds must be between 30 and 3600.");

        if (options.Protocol.PlayerSearchLimit is < 1 or > ProtocolOptions.MaxPlayerSearchLimit)
            failures.Add(
                $"Protocol.PlayerSearchLimit must be between 1 and " +
                $"{ProtocolOptions.MaxPlayerSearchLimit}.");

        var invalidBlockedTerms = options.Protocol.BlockedTerms
            .Where(term => string.IsNullOrWhiteSpace(term)
                           || term.Any(char.IsControl)
                           || Encoding.UTF8.GetByteCount(term.Trim()) > ProtocolTextPolicy.MaxEncodedBytes)
            .ToArray();
        if (invalidBlockedTerms.Length > 0)
            failures.Add(
                "Protocol.BlockedTerms cannot contain empty values, control characters, " +
                $"or values longer than {ProtocolTextPolicy.MaxEncodedBytes} UTF-8 bytes.");

        var lobbies = options.Lobbies.Where(l => l.Enabled).ToArray();

        if (lobbies.Length > ServerOptions.MaxLobbies)
            failures.Add(
                $"At most {ServerOptions.MaxLobbies} lobbies may be configured; " +
                $"{lobbies.Length} are enabled.");

        if (lobbies.Any(l => string.IsNullOrWhiteSpace(l.Name)))
            failures.Add("Every lobby needs a Name.");

        var duplicateNames = lobbies
            .GroupBy(l => l.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicateNames.Length > 0)
            failures.Add($"Lobby names must be unique; repeated: {string.Join(", ", duplicateNames)}.");

        // Ids drive occupancy bookkeeping, so a collision would merge two blocks.
        var duplicateIds = lobbies
            .Where(l => l.Id > 0)
            .GroupBy(l => l.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicateIds.Length > 0)
            failures.Add($"Lobby ids must be unique; repeated: {string.Join(", ", duplicateIds)}.");

        var services = options.Services.Where(s => s.Enabled).ToArray();

        var duplicatePorts = services
            .GroupBy(s => s.Port)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicatePorts.Length > 0)
            failures.Add($"Two services cannot share a port; repeated: {string.Join(", ", duplicatePorts)}.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
