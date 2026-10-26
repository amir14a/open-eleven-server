// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Web;

/// <summary>
/// Sign-in for the admin portal (Pages/Admin): one user and password from configuration, kept
/// in a cookie scoped to /admin so the public pages never carry it.
/// </summary>
public static class AdminAuth
{
    public const string Scheme = "Admin";
    private const string StampClaim = "stamp";

    public static IServiceCollection AddAdminPortal(this IServiceCollection services, bool https)
    {
        services.AddAuthentication(Scheme).AddCookie(Scheme, cookie =>
        {
            cookie.LoginPath = "/admin/login";
            cookie.AccessDeniedPath = "/admin/login";
            cookie.Cookie.Name = "oe_admin";
            cookie.Cookie.Path = "/admin";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = https ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
            cookie.SlidingExpiration = true;

            // Changing the password (or turning the portal off) signs every session out.
            cookie.Events.OnValidatePrincipal = context =>
            {
                var admin = Admin(context.HttpContext);
                if (!admin.Enabled || context.Principal?.FindFirstValue(StampClaim) != Stamp(admin))
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };

            // While the portal is off, its pages do not exist rather than asking for a sign-in.
            cookie.Events.OnRedirectToLogin = context =>
            {
                if (Admin(context.HttpContext).Enabled)
                    context.Response.Redirect(context.RedirectUri);
                else
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            };
        });
        services.AddAuthorization();
        return services;
    }

    private static AdminOptions Admin(HttpContext http)
        => http.RequestServices.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue.Admin;

    public static bool Matches(AdminOptions admin, string? user, string? password)
        // Both compared, in fixed time, so a wrong user name answers as fast as a wrong password.
        => admin.Enabled & Same(admin.User, user ?? "") & Same(admin.Password, password ?? "");

    public static ClaimsPrincipal Principal(AdminOptions admin)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, admin.User), new Claim(StampClaim, Stamp(admin))], Scheme));

    private static bool Same(string expected, string actual)
        => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual)));

    private static string Stamp(AdminOptions admin)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{admin.User}\n{admin.Password}")))[..16];
}

/// <summary>Answers 404 for every admin page while the portal is off.</summary>
public sealed class AdminPortalFilter : IPageFilter
{
    public void OnPageHandlerSelected(PageHandlerSelectedContext context) { }

    public void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        // The sign-in page is anonymous, so the cookie's 404 never covers it.
        if (!context.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue.Admin.Enabled)
            context.Result = new NotFoundResult();
    }

    public void OnPageHandlerExecuted(PageHandlerExecutedContext context) { }
}
