// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages;

/// <summary>
/// The registration form. The password is posted as plaintext and hashed here, because the
/// digest the server stores has to be the one the game client will present, and deriving it
/// in one place server-side is the only way to be sure of that.
/// </summary>
/// <remarks>
/// Deliberately avoids ModelState, TempData and Url: all three dereference PageContext,
/// which is null on a hand-constructed PageModel, and staying off them is what lets the
/// tests do <c>new RegisterModel(service)</c> without a host. Using a plain Error string
/// also keeps the form's wording identical to the JSON API's, since both come from
/// <see cref="RegistrationEndpoint"/>.
/// </remarks>
[SensitiveBody]
[EnableRateLimiting(HttpRateLimits.Register)]
public sealed class RegisterModel(RegistrationService registrations, CaptchaVerifier captcha) : PageModel
{
    [BindProperty]
    public string? GameId { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    [BindProperty]
    public string? Confirm { get; set; }

    [BindProperty]
    public string? RegCode { get; set; }

    /// <summary>Set after a successful post, via the redirect, so a refresh cannot repost.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Created { get; set; }

    public string? Error { get; private set; }

    /// <summary>The captcha widget to render, or null when the captcha is off.</summary>
    public CaptchaProvider? Captcha => captcha.Provider;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Request is only touched with the captcha on, so a hostless PageModel still works.
        if (Captcha is { } provider && !await captcha.VerifyAsync(
                Request.Form[provider.ResponseField], HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken))
            return Failed("Please complete the captcha.");

        // Checked before hashing: the digest is only meaningful once the plaintext is known
        // to be within the range every candidate encoding agrees on.
        if (RegistrationEndpoint.ValidatePassword(Password, Confirm) is { } problem)
            return Failed(problem);

        var request = new RegisterAccountRequest(
            GameId,
            AuthProof.HashPassword(Password!),
            RegCode);

        var outcome = await registrations.RegisterAsync(request, cancellationToken);

        if (!outcome.Success)
            return Failed(outcome.Error ?? "Registration failed.");

        return RedirectToPage(new { created = outcome.Account!.GameId });
    }

    private PageResult Failed(string error)
    {
        Error = error;

        // Never re-render what was typed into a password field.
        Password = null;
        Confirm = null;

        return Page();
    }
}
