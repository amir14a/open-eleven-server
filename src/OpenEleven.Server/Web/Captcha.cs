// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Web;

/// <summary>
/// What the registration page needs to render a captcha widget and the server needs to check
/// it. Turnstile, hCaptcha and reCAPTCHA v2 share one protocol: a script renders a widget
/// (an element with a class and data-sitekey), the widget posts its token in a form field,
/// and the server posts secret + response + remoteip to a verify URL that answers
/// <c>{"success": true|false}</c>. Any other provider speaking it works as <c>Custom</c>.
/// </summary>
public sealed record CaptchaProvider(
    string SiteKey, string ScriptUrl, string VerifyUrl, string WidgetClass, string ResponseField)
{
    public static readonly IReadOnlyDictionary<string, CaptchaProvider> Presets =
        new Dictionary<string, CaptchaProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["Turnstile"] = new("",
                "https://challenges.cloudflare.com/turnstile/v0/api.js",
                "https://challenges.cloudflare.com/turnstile/v0/siteverify",
                "cf-turnstile", "cf-turnstile-response"),
            ["HCaptcha"] = new("",
                "https://js.hcaptcha.com/1/api.js",
                "https://api.hcaptcha.com/siteverify",
                "h-captcha", "h-captcha-response"),
            ["ReCaptcha"] = new("",
                "https://www.google.com/recaptcha/api.js",
                "https://www.google.com/recaptcha/api/siteverify",
                "g-recaptcha", "g-recaptcha-response"),
        };

    /// <summary>The configured provider, or null when the captcha is off.</summary>
    public static CaptchaProvider? From(CaptchaOptions o)
    {
        if (string.Equals(o.Provider, "None", StringComparison.OrdinalIgnoreCase) || o.Provider.Length == 0)
            return null;

        var preset = Presets.GetValueOrDefault(o.Provider) ?? new CaptchaProvider("", "", "", "", "");
        return new CaptchaProvider(
            o.SiteKey,
            Or(o.ScriptUrl, preset.ScriptUrl),
            Or(o.VerifyUrl, preset.VerifyUrl),
            Or(o.WidgetClass, preset.WidgetClass),
            Or(o.ResponseField, preset.ResponseField));
    }

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
}

/// <summary>Checks a captcha token with the configured provider. Fails closed.</summary>
public sealed class CaptchaVerifier(
    HttpClient http, IOptionsMonitor<ServerOptions> options, ILogger<CaptchaVerifier> log)
{
    public CaptchaProvider? Provider => CaptchaProvider.From(options.CurrentValue.Captcha);

    /// <summary>True when the captcha is off, or the provider accepted <paramref name="token"/>.</summary>
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        if (Provider is not { } provider)
            return true;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var form = new Dictionary<string, string>
        {
            ["secret"] = options.CurrentValue.Captcha.SecretKey,
            ["response"] = token,
        };
        if (!string.IsNullOrEmpty(remoteIp))
            form["remoteip"] = remoteIp;

        try
        {
            using var response = await http.PostAsync(provider.VerifyUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Captcha verify answered {Status}", (int)response.StatusCode);
                return false;
            }

            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return body.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Unreachable provider: refuse rather than let registrations through unchecked.
            log.LogWarning(ex, "Captcha verify failed");
            return false;
        }
    }
}
