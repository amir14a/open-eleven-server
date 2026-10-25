// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Server.Pages;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

/// <summary>
/// Exercises the form's PageModel directly. It takes only RegistrationService and never
/// touches PageContext, so it constructs without a host — which matters because booting the
/// real Program would start six TCP listeners on 28010-28015.
/// </summary>
public class RegisterPageModelTests
{
    private const string Password = "correct-horse";
    private const string Serial = "5HRVLVRUF75RMV2LRK45";

    private static RegisterModel NewModel(IServiceProvider scope) => new(
        scope.GetRequiredService<RegistrationService>(),
        scope.GetRequiredService<CaptchaVerifier>())
    {
        GameId = "marcos",
        Password = Password,
        Confirm = Password,
        RegCode = Serial,
    };

    [Fact]
    public async Task The_digest_it_stores_is_the_one_the_game_will_present()
    {
        // The load-bearing test of the whole change: an account created through the form
        // must satisfy the HTTP login the game itself performs. If the form and
        // AuthProof.HashPassword ever disagree on encoding, this is what catches it.
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();

        var result = await NewModel(scope.ServiceProvider).OnPostAsync(CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(result);

        var login = await harness.AuthenticateHttpAsync("marcos", AuthProof.HashPassword(Password));

        Assert.True(login.Success);
    }

    [Fact]
    public async Task Creates_the_account_and_redirects_with_the_name()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();

        var result = await NewModel(scope.ServiceProvider).OnPostAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("marcos", redirect.RouteValues!["created"]);

        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var account = await accounts.GetByGameIdAsync("marcos");

        Assert.NotNull(account);
        Assert.Equal(AuthProof.HashPassword(Password), account!.PasswordHash);
        Assert.Equal(Serial, account.RegCode);
    }

    [Fact]
    public async Task Mismatched_confirmation_is_refused_without_touching_the_database()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();

        var model = NewModel(scope.ServiceProvider);
        model.Confirm = "something-else";

        var result = await model.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("The two passwords do not match.", model.Error);

        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        Assert.Null(await accounts.GetByGameIdAsync("marcos"));
    }

    [Fact]
    public async Task A_non_ascii_password_is_refused()
    {
        // Accepting it would store a digest the game can never reproduce.
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();

        var model = NewModel(scope.ServiceProvider);
        model.Password = "contraseña";
        model.Confirm = "contraseña";

        var result = await model.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Contains("printable ASCII", model.Error);
    }

    [Fact]
    public async Task Duplicate_game_id_is_refused_with_the_shared_wording()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();

        await NewModel(scope.ServiceProvider).OnPostAsync(CancellationToken.None);

        var second = NewModel(scope.ServiceProvider);
        var result = await second.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(
            RegistrationEndpoint.Describe(RegistrationResult.GameIdTaken),
            second.Error);
    }

    [Fact]
    public async Task Never_re_renders_the_plaintext_password()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();

        var model = NewModel(scope.ServiceProvider);
        model.GameId = "";                       // force a failure after binding

        await model.OnPostAsync(CancellationToken.None);

        Assert.Null(model.Password);
        Assert.Null(model.Confirm);
        Assert.NotNull(model.Error);
    }

    [Fact]
    public void Is_marked_so_its_body_is_never_logged()
        => Assert.NotNull(
            typeof(RegisterModel).GetCustomAttributes(typeof(SensitiveBodyAttribute), false)
                .FirstOrDefault());
}
