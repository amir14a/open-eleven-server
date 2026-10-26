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

        if (lobbies.Any(l => l.MaxPlayers < 1))
            failures.Add("Every lobby needs MaxPlayers of at least 1.");

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

        // With HTTPS the URLs must carry the name on the certificate, and the client only does
        // TLS off port 80. Plain HTTP (local development) works only on 80.
        if (options.Https.Enabled)
        {
            if (Uri.CheckHostName(options.AdvertiseIp) != UriHostNameType.Dns
                || string.Equals(options.AdvertiseIp, "auto", StringComparison.OrdinalIgnoreCase))
                failures.Add($"AdvertiseIp must be a host name with HTTPS (not an IP or empty); got '{options.AdvertiseIp}'. 'auto' only works with Https.Enabled: false.");
            if (options.Listen.Https == 80)
                failures.Add("Listen.Https cannot be 80: the client speaks plain HTTP there, not TLS.");
        }
        else
        {
            if (Uri.CheckHostName(options.AdvertiseIp) == UriHostNameType.Unknown)
                failures.Add($"AdvertiseIp must be a host name or IP; got '{options.AdvertiseIp}'.");
            if (options.Listen.Http != 80)
                failures.Add("Listen.Http must be 80: the client switches to TLS on any other port.");
        }

        var captcha = options.Captcha;
        if (!CaptchaOptions.Providers.Contains(captcha.Provider, StringComparer.OrdinalIgnoreCase))
            failures.Add($"Captcha.Provider: unknown '{captcha.Provider}'. Valid: {string.Join(", ", CaptchaOptions.Providers)}.");
        else if (Web.CaptchaProvider.From(captcha) is { } provider)
        {
            if (captcha.SiteKey.Length == 0 || captcha.SecretKey.Length == 0)
                failures.Add($"Captcha.Provider {captcha.Provider} needs SiteKey and SecretKey.");
            if (provider.ScriptUrl.Length == 0 || provider.VerifyUrl.Length == 0
                || provider.WidgetClass.Length == 0 || provider.ResponseField.Length == 0)
                failures.Add("Captcha.Provider Custom needs ScriptUrl, VerifyUrl, WidgetClass and ResponseField.");
        }

        if (options.Admin.Enabled && (options.Admin.User.Length == 0 || options.Admin.Password.Length < AdminOptions.MinPasswordLength))
            failures.Add($"Admin needs a User and a Password of at least {AdminOptions.MinPasswordLength} characters (or no Password to keep the portal off).");

        // An unknown type silently inherits the previous entry's type in the client's parser.
        foreach (var url in options.Urls)
        {
            if (!UrlOptions.Types.Contains(url.Type))
                failures.Add($"Urls: unknown Type '{url.Type}'. Valid: {string.Join(", ", UrlOptions.Types)}.");
            if ((url.File.Length > 0) == (url.Url.Length > 0))
                failures.Add($"Urls: entry '{url.Type}' needs exactly one of File or Url.");
            else if (url.File.Length > 0 && !File.Exists(url.FullPath))
                failures.Add($"Urls: entry '{url.Type}' points at a missing file '{url.File}'.");
        }

        // FUN_00513de0 keeps at most 23 entries; built-ins take up to 5 of them.
        if (options.Urls.Count > 18)
            failures.Add("Urls: at most 18 entries.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
