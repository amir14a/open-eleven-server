// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

public class CaptchaTests
{
    /// <summary>Answers every verify call with <paramref name="body"/> and records what was posted.</summary>
    private sealed class FakeProvider(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Url;
        public string? Posted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri;
            Posted = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static CaptchaVerifier Verifier(CaptchaOptions captcha, HttpMessageHandler handler)
    {
        var options = new ServerOptions { Captcha = captcha };
        var monitor = new OptionsMonitorStub(options);
        return new CaptchaVerifier(new HttpClient(handler), monitor, NullLogger<CaptchaVerifier>.Instance);
    }

    private sealed class OptionsMonitorStub(ServerOptions value) : IOptionsMonitor<ServerOptions>
    {
        public ServerOptions CurrentValue => value;
        public ServerOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ServerOptions, string?> listener) => null;
    }

    private static CaptchaOptions Turnstile => new() { Provider = "Turnstile", SiteKey = "site", SecretKey = "secret" };

    [Fact]
    public async Task Off_by_default_lets_everything_through()
    {
        var handler = new FakeProvider("{\"success\":false}");
        var verifier = Verifier(new CaptchaOptions(), handler);

        Assert.Null(verifier.Provider);
        Assert.True(await verifier.VerifyAsync(null, null, default));
        Assert.Null(handler.Url);
    }

    [Theory]
    [InlineData("Turnstile", "https://challenges.cloudflare.com/turnstile/v0/siteverify", "cf-turnstile-response")]
    [InlineData("HCaptcha", "https://api.hcaptcha.com/siteverify", "h-captcha-response")]
    [InlineData("ReCaptcha", "https://www.google.com/recaptcha/api/siteverify", "g-recaptcha-response")]
    public async Task Named_providers_post_the_token_to_their_verify_url(string name, string verifyUrl, string field)
    {
        var handler = new FakeProvider("{\"success\":true}");
        var verifier = Verifier(new CaptchaOptions { Provider = name, SiteKey = "site", SecretKey = "secret" }, handler);

        Assert.True(await verifier.VerifyAsync("token", "203.0.113.5", default));
        Assert.Equal((verifyUrl, field), (handler.Url!.ToString(), verifier.Provider!.ResponseField));
        Assert.Equal("secret=secret&response=token&remoteip=203.0.113.5", handler.Posted);
    }

    [Theory]
    [InlineData("{\"success\":false}", HttpStatusCode.OK)]
    [InlineData("not json", HttpStatusCode.OK)]
    [InlineData("{\"success\":true}", HttpStatusCode.InternalServerError)]
    public async Task Rejections_and_provider_failures_refuse(string body, HttpStatusCode status)
        => Assert.False(await Verifier(Turnstile, new FakeProvider(body, status)).VerifyAsync("token", null, default));

    [Fact]
    public async Task A_missing_token_is_refused_without_asking_the_provider()
    {
        var handler = new FakeProvider("{\"success\":true}");
        Assert.False(await Verifier(Turnstile, handler).VerifyAsync("", null, default));
        Assert.Null(handler.Url);
    }

    [Fact]
    public void Custom_takes_its_endpoints_from_configuration()
    {
        var provider = CaptchaProvider.From(new CaptchaOptions
        {
            Provider = "Custom", SiteKey = "s", SecretKey = "k",
            ScriptUrl = "https://captcha.example/api.js", VerifyUrl = "https://captcha.example/verify",
            WidgetClass = "x-captcha", ResponseField = "x-captcha-response",
        })!;

        Assert.Equal(("https://captcha.example/verify", "x-captcha"), (provider.VerifyUrl, provider.WidgetClass));
    }

    [Theory]
    [InlineData("Turnstile", "", "secret")]          // missing site key
    [InlineData("HCaptcha", "site", "")]             // missing secret
    [InlineData("Custom", "site", "secret")]         // no endpoints
    [InlineData("SomethingElse", "site", "secret")]  // unknown provider
    public void Incomplete_captcha_settings_stop_the_server(string provider, string site, string secret)
    {
        var options = new ServerOptions
        {
            AdvertiseIp = "oe.test",
            Captcha = new CaptchaOptions { Provider = provider, SiteKey = site, SecretKey = secret },
        };

        Assert.Contains(new ServerOptionsValidator().Validate(null, options).Failures ?? [], f => f.Contains("Captcha"));
    }
}
