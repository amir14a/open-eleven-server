// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

public class RegistrationValidationTests
{
    [Fact]
    public void Uses_the_client_game_id_length_limit()
        => Assert.Equal(32, RegistrationEndpoint.MaxGameIdLength);

    [Fact]
    public void Accepts_a_complete_request()
        => Assert.Null(RegistrationEndpoint.Validate(
            new RegisterAccountRequest("marcos", "86d84f975c5afebdea53f5ec3c6abbde", "5HRVLVRUF75RMV2LRK45")));

    [Theory]
    [InlineData(null, "hash", "code")]
    [InlineData("", "hash", "code")]
    [InlineData("  ", "hash", "code")]
    [InlineData("user", null, "code")]
    [InlineData("user", "", "code")]
    [InlineData("user", "hash", null)]
    [InlineData("user", "hash", "")]
    public void Rejects_missing_fields(string? gameId, string? hash, string? regCode)
        => Assert.NotNull(RegistrationEndpoint.Validate(
            new RegisterAccountRequest(gameId, hash, regCode)));

    [Fact]
    public void Rejects_an_over_long_game_id()
        => Assert.NotNull(RegistrationEndpoint.Validate(new RegisterAccountRequest(
            new string('x', RegistrationEndpoint.MaxGameIdLength + 1),
            "86d84f975c5afebdea53f5ec3c6abbde",
            "code")));

    [Fact]
    public void Rejects_an_over_long_reg_code()
        => Assert.NotNull(RegistrationEndpoint.Validate(new RegisterAccountRequest(
            "user",
            "86d84f975c5afebdea53f5ec3c6abbde",
            new string('x', RegistrationEndpoint.MaxRegCodeLength + 1))));

    [Fact]
    public void Rejects_a_password_that_is_not_an_md5_digest()
        => Assert.NotNull(RegistrationEndpoint.Validate(new RegisterAccountRequest(
            "user", "not-a-digest", "SERIAL")));
}

/// <summary>
/// The plaintext rules the form applies before hashing. The API never runs these — it only
/// ever sees a digest.
/// </summary>
public class PasswordValidationTests
{
    [Fact]
    public void Uses_the_client_password_length_limit()
        => Assert.Equal(16, RegistrationEndpoint.MaxPasswordLength);

    [Fact]
    public void Accepts_a_matching_printable_ascii_password()
        => Assert.Null(RegistrationEndpoint.ValidatePassword("p@ssw0rd!", "p@ssw0rd!"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Requires_a_password(string? password)
        => Assert.NotNull(RegistrationEndpoint.ValidatePassword(password, password));

    [Fact]
    public void Requires_the_confirmation_to_match()
        => Assert.Equal(
            "The two passwords do not match.",
            RegistrationEndpoint.ValidatePassword("secret", "Secret"));

    [Fact]
    public void Rejects_an_over_long_password()
    {
        var password = new string('x', RegistrationEndpoint.MaxPasswordLength + 1);

        Assert.NotNull(RegistrationEndpoint.ValidatePassword(password, password));
    }

    [Fact]
    public void Accepts_a_password_at_the_limit()
    {
        var password = new string('x', RegistrationEndpoint.MaxPasswordLength);

        Assert.Null(RegistrationEndpoint.ValidatePassword(password, password));
    }

    [Theory]
    [InlineData("café")]
    [InlineData("密码")]
    [InlineData("naïve")]
    public void Rejects_non_ascii(string password)
    {
        // Encoding.ASCII would substitute '?', producing a digest the game cannot reproduce.
        Assert.Contains(
            "printable ASCII",
            RegistrationEndpoint.ValidatePassword(password, password));
    }

    [Theory]
    [InlineData("with\ttab")]
    [InlineData("with\nnewline")]
    public void Rejects_control_characters(string password)
        => Assert.NotNull(RegistrationEndpoint.ValidatePassword(password, password));

    [Fact]
    public void Treats_surrounding_whitespace_as_part_of_the_password()
    {
        // Not trimmed: the spaces are part of what the player typed, and stripping them
        // would change the digest.
        Assert.NotNull(RegistrationEndpoint.ValidatePassword(" secret ", "secret"));
        Assert.Null(RegistrationEndpoint.ValidatePassword(" secret ", " secret "));
    }
}

public class AccountRegistrationTests
{
    [Fact]
    public async Task Creates_an_account_with_normalized_credentials_and_first_login()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();

        var (result, account) = await accounts.RegisterAsync(
            "marcos", "86D84F975C5AFE BDEA53F5EC3C6ABBDE".Replace(" ", ""), "5hrvlvruf75rmv2lrk45");

        Assert.Equal(RegistrationResult.Created, result);
        Assert.NotNull(account);
        Assert.Equal("marcos", account!.GameId);
        Assert.Equal("5HRVLVRUF75RMV2LRK45", account.RegCode);
        Assert.Equal("86d84f975c5afebdea53f5ec3c6abbde", account.PasswordHash);
        Assert.True(account.FirstLogin);
    }

    [Fact]
    public async Task Refuses_a_duplicate_game_id()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();

        await accounts.RegisterAsync("marcos", "hash", "SERIAL-1");
        var (result, account) = await accounts.RegisterAsync("marcos", "hash", "SERIAL-2");

        Assert.Equal(RegistrationResult.GameIdTaken, result);
        Assert.Null(account);
    }

    [Fact]
    public async Task Allows_a_serial_to_be_shared_by_multiple_accounts()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();

        await accounts.RegisterAsync("first", "hash", "SERIAL-1");
        var (result, account) = await accounts.RegisterAsync("second", "hash", "SERIAL-1");

        Assert.Equal(RegistrationResult.Created, result);
        Assert.NotNull(account);
        Assert.Equal("second", account!.GameId);
    }
}

public class AuthenticationTests
{
    private const string Credential = "86d84f975c5afebdea53f5ec3c6abbde";
    private const string Serial = "5HRVLVRUF75RMV2LRK45";

    private static async Task RegisterAsync(
        ServerHarness harness,
        string gameId,
        string credential = Credential,
        string serial = Serial)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var result = await accounts.RegisterAsync(gameId, credential, serial);
        Assert.Equal(RegistrationResult.Created, result.Result);
    }

    private static Session NewAuthSession(ServerHarness harness)
        => harness.NewSession(ServiceRole.Account);

    [Fact]
    public async Task Pes2011_proof_uses_plain_game_id_and_dashless_serial()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");
        Assert.True((await harness.AuthenticateHttpAsync("marcos", Credential)).Success);

        var session = NewAuthSession(harness);
        await harness.DispatchAsync(session, "MSG_REQCCODE", rqid: 1);
        var replies = await harness.DispatchAsync(session, "MSG_REQAUTH", rqid: 2, withFields: request => request
            .Set("uname", "marcos")
            .Set("hash", AuthProof.Compute2011("marcos", session.ChallengeCode, Credential))
            .Set("para_hash", "d6861177058a1b4cc4add08c9c829d73")
            .Set("entry_hash", "4610ec92ea8e19ef49cdb1d318094fcd")
            .Set("tmpRegcode", Serial.Replace("-", "")));

        Assert.Equal("NOERR", replies[^1].GetString("result"));
        Assert.Equal(SessionState.Authenticated, session.State);
    }

    [Fact]
    public async Task Http_login_and_matching_binary_proof_bind_the_account()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");

        var http = await harness.AuthenticateHttpAsync("marcos", Credential);
        Assert.True(http.Success);
        Assert.Equal($"1\nmarcos\n{Credential}\n{Credential}\n", http.Body);

        var session = NewAuthSession(harness);
        var replies = await harness.AuthenticateSocketAsync(session, "marcos", Credential, Serial);

        Assert.Equal("NOERR", replies[1].GetString("result"));
        Assert.Equal(SessionState.Authenticated, session.State);
        Assert.Equal("marcos", session.GameId);
        Assert.True(session.AccountId > 0);
        Assert.Equal(
            AuthProof.Compute(Credential, session.ChallengeCode, "marcos"),
            session.AuthHash);
        Assert.Equal("d6861177058a1b4cc4add08c9c829d73", session.ParaHash);
        Assert.Equal("4610ec92ea8e19ef49cdb1d318094fcd", session.EntryHash);
    }

    [Fact]
    public async Task Wrong_http_password_is_refused_without_opening_a_binary_login()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");

        var result = await harness.AuthenticateHttpAsync(
            "marcos", "00000000000000000000000000000000");

        Assert.False(result.Success);
        Assert.Equal("0\n\n\n", result.Body);
        Assert.Equal("PASSWORD", result.Reason);
    }

    [Fact]
    public async Task Binary_auth_without_a_pending_http_login_is_refused()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");

        var session = NewAuthSession(harness);
        var replies = await harness.AuthenticateSocketAsync(session, "marcos", Credential, Serial);

        Assert.Equal("ERR", replies[0].GetString("result"));
        Assert.Equal("NOACCOUNT", replies[0].GetString("reason"));
        Assert.Equal(SessionState.Challenged, session.State);
    }

    [Fact]
    public async Task Wrong_serial_is_refused_after_the_account_is_selected_by_username_and_proof()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");
        await harness.AuthenticateHttpAsync("marcos", Credential);

        var session = NewAuthSession(harness);
        var replies = await harness.AuthenticateSocketAsync(
            session, "marcos", Credential, "SOMEONE-ELSES-SERIAL");

        Assert.Equal("REGCODE", replies[0].GetString("reason"));
        Assert.Equal(SessionState.Challenged, session.State);
        Assert.Equal(0, session.AccountId);
    }

    [Fact]
    public async Task Same_password_and_serial_are_disambiguated_by_the_http_username_and_proof()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");
        await RegisterAsync(harness, "sofia");
        await harness.AuthenticateHttpAsync("marcos", Credential);
        await harness.AuthenticateHttpAsync("sofia", Credential);

        var marcos = NewAuthSession(harness);
        await harness.AuthenticateSocketAsync(marcos, "marcos", Credential, Serial);
        var sofia = NewAuthSession(harness);
        await harness.AuthenticateSocketAsync(sofia, "sofia", Credential, Serial);

        Assert.Equal("marcos", marcos.GameId);
        Assert.Equal("sofia", sofia.GameId);
        Assert.NotEqual(marcos.AccountId, sofia.AccountId);
    }

    [Fact]
    public async Task Malformed_fingerprint_fields_are_refused()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");
        await harness.AuthenticateHttpAsync("marcos", Credential);

        var session = NewAuthSession(harness);
        var replies = await harness.AuthenticateSocketAsync(
            session, "marcos", Credential, Serial, paraHash: "not-an-md5");

        Assert.Equal("AUTH", replies[0].GetString("reason"));
        Assert.Equal(SessionState.Challenged, session.State);
    }

    [Fact]
    public async Task A_banned_account_is_refused_at_http_login()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
            var account = await accounts.GetByGameIdAsync("marcos");
            account!.Banned = true;
            await accounts.SaveAsync();
        }

        var result = await harness.AuthenticateHttpAsync("marcos", Credential);
        Assert.False(result.Success);
        Assert.Equal("BANNED", result.Reason);
    }

    [Fact]
    public async Task Client_serial_never_overwrites_the_registered_serial()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");
        await harness.AuthenticateHttpAsync("marcos", Credential);

        var session = NewAuthSession(harness);
        await harness.AuthenticateSocketAsync(session, "marcos", Credential, "OVERWRITE-ME");

        await using var scope = harness.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        Assert.Equal(Serial, (await accounts.GetByGameIdAsync("marcos"))!.RegCode);
    }

    [Fact]
    public async Task First_login_is_true_for_one_logical_login_then_false_for_the_next()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await RegisterAsync(harness, "marcos");
        await harness.AuthenticateHttpAsync("marcos", Credential);

        var accountSession = NewAuthSession(harness);
        var first = await harness.AuthenticateSocketAsync(
            accountSession, "marcos", Credential, Serial);
        var menuSession = NewAuthSession(harness);
        var sameLogin = await harness.AuthenticateSocketAsync(
            menuSession, "marcos", Credential, Serial);

        Assert.Equal(true, first[1].GetValue("first_login"));
        Assert.Equal(true, sameLogin[1].GetValue("first_login"));
        Assert.True(accountSession.FirstLogin);
        Assert.True(menuSession.FirstLogin);

        await harness.AuthenticateHttpAsync("marcos", Credential);
        var laterSession = NewAuthSession(harness);
        var later = await harness.AuthenticateSocketAsync(
            laterSession, "marcos", Credential, Serial);

        Assert.Equal(false, later[1].GetValue("first_login"));
        Assert.False(laterSession.FirstLogin);

        await using var scope = harness.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        Assert.False((await accounts.GetByGameIdAsync("marcos"))!.FirstLogin);
    }

    [Fact]
    public async Task Active_login_grant_survives_service_changes_past_its_original_deadline()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        await using var harness = await ServerHarness.CreateAsync(
            settings => settings["Server:Protocol:PendingLoginLifetimeSeconds"] = "30",
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        await RegisterAsync(harness, "marcos");
        await harness.AuthenticateHttpAsync("marcos", Credential);

        clock.Advance(TimeSpan.FromSeconds(25));
        var account = harness.NewSession(ServiceRole.Account);
        var accountAuth = await harness.AuthenticateSocketAsync(
            account, "marcos", Credential, Serial);
        Assert.Equal("NOERR", accountAuth[1].GetString("result"));

        clock.Advance(TimeSpan.FromSeconds(25));
        await harness.DispatchAsync(account, "CMD_SEND_HEARTBEAT", rqid: 3);

        clock.Advance(TimeSpan.FromSeconds(25));
        var lobby = harness.NewSession(ServiceRole.Lobby);
        var lobbyAuth = await harness.AuthenticateSocketAsync(
            lobby, "marcos", Credential, Serial);

        Assert.Equal("NOERR", lobbyAuth[1].GetString("result"));
        Assert.Equal(SessionState.Authenticated, lobby.State);
        Assert.Equal(account.AccountId, lobby.AccountId);
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
}
