// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text;
using OpenEleven.Data.Repositories;

namespace OpenEleven.Server.Web;

public static class GameEndpoints
{
    public static IEndpointRouteBuilder MapGameEndpoints(this IEndpointRouteBuilder app)
    {
        // Account creation for scripts and tools. The browser form lives at GET/POST
        // /register (Pages/Register.cshtml) and converges on the same RegistrationService;
        // the two are on separate paths because one path cannot serve two POST handlers.
        app.MapPost("/api/register", async (
            RegisterAccountRequest request,
            RegistrationService registrations,
            CancellationToken ct) =>
        {
            var outcome = await registrations.RegisterAsync(request, ct);

            return outcome.Failure switch
            {
                RegistrationFailure.Invalid => Results.BadRequest(new { error = outcome.Error }),
                RegistrationFailure.Conflict => Results.Conflict(new { error = outcome.Error }),
                _ => Results.Created(
                    $"/accounts/{outcome.Account!.Id}",
                    new RegisterAccountResponse(
                        outcome.Account.Id, outcome.Account.GameId, outcome.Account.RegCode)),
            };
        }).WithMetadata(new SensitiveBodyAttribute());

        // Static files the client fetches by URL from CMD_GET_URLLIST.
        app.MapMethods("/{file}", ["GET", "HEAD"], (string file, HttpContext http, WebAssets assets) =>
        {
            if (!assets.TryGet("/" + file, out var content))
                return Results.NotFound();

            var body = HttpMethods.IsHead(http.Request.Method) ? Array.Empty<byte>() : content;
            return Results.Bytes(body, "application/octet-stream");
        });

        // The HTTP half of login. Three LF-separated lines; a leading "1" is what the
        // client's transport turns into an internal NOERR.
        //
        // Antiforgery must never reach this endpoint — the game client has no token to send.
        // It is safe today on two counts: UseAntiforgery() is not in the pipeline, and the
        // handler reads the form manually from HttpContext rather than taking a [FromForm]
        // parameter, so ASP.NET never attaches the IAntiforgeryMetadata that triggers
        // validation. Keep both properties if you touch this.
        app.MapPost("/gameid_auth", async (HttpContext http, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("OpenEleven.GameIdAuth");

            var form = await http.Request.ReadFormAsync();
            var gameId = form["game_id"].ToString();
            var passwordHash = form["game_id_password"].ToString();

            log.LogInformation(
                "GAMEID auth: game_id={GameId} hash={Hash} valid_md5={Valid}",
                gameId, passwordHash, GameIdAuthEndpoint.LooksLikeMd5(passwordHash));

            var authenticator = http.RequestServices.GetRequiredService<GameIdAuthService>();
            var result = await authenticator.AuthenticateAsync(
                gameId,
                passwordHash,
                http.Connection.RemoteIpAddress ?? System.Net.IPAddress.None,
                http.RequestAborted);

            if (!result.Success)
                log.LogWarning("GAMEID auth rejected for {GameId}: {Reason}", gameId, result.Reason);

            log.LogInformation(
                "GAMEID auth response: {Body}", result.Body.Replace("\n", "\\n"));

            // Legacy client errors are encoded in the first response line, not HTTP status.
            return Results.Text(result.Body, "text/plain", Encoding.ASCII);
        });

        app.MapFallback(() => Results.NotFound());

        return app;
    }
}
