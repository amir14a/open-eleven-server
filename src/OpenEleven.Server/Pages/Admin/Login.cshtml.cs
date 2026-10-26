// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages.Admin;

[SensitiveBody]
[EnableRateLimiting(HttpRateLimits.AdminLogin)]
public sealed class LoginModel(IOptionsMonitor<ServerOptions> options, ILogger<LoginModel> log) : PageModel
{
    [BindProperty] public string? UserName { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var admin = options.CurrentValue.Admin;
        var address = HttpContext.Connection.RemoteIpAddress;
        if (!AdminAuth.Matches(admin, UserName, Password))
        {
            log.LogWarning("Admin sign-in refused for '{User}' from {Address}", UserName, address);
            Error = "Wrong user name or password.";
            Password = null;
            return Page();
        }

        await HttpContext.SignInAsync(AdminAuth.Scheme, AdminAuth.Principal(admin));
        log.LogWarning("Admin {User} signed in from {Address}", admin.User, address);
        return LocalRedirect(ReturnUrl is { } back && back.StartsWith("/admin", StringComparison.Ordinal) && Url.IsLocalUrl(back) ? back : "/admin");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(AdminAuth.Scheme);
        return RedirectToPage();
    }
}
