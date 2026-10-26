// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text;
using Microsoft.Extensions.Options;
using OpenEleven.Data.Repositories;
using OpenEleven.Server.Configuration;

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
            CaptchaVerifier captcha,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Same gate as the form, or the API would be the way around it.
            if (!await captcha.VerifyAsync(request.CaptchaToken, http.Connection.RemoteIpAddress?.ToString(), ct))
                return Results.BadRequest(new { error = "captchaToken is missing or was not accepted." });

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
        }).WithMetadata(new SensitiveBodyAttribute()).RequireRateLimiting(HttpRateLimits.Register);

        // Server.Urls entries backed by a file (the DLC package and friends), streamed with range
        // support so an interrupted download can resume.
        app.MapMethods("/files/{name}", ["GET", "HEAD"], (string name, IOptionsMonitor<ServerOptions> options) =>
            DownloadFiles.Find(options.CurrentValue, name) is { } path && File.Exists(path)
                ? Results.File(path, "application/octet-stream", enableRangeProcessing: true)
                : Results.NotFound());

        // The DOWNLOAD url: an update manifest pointing at the package under /files/.
        // The client appends ?ver=..&ps3=0 to this url, so the language rides in the path.
        app.MapGet("/dlc/manifest/{lang?}", (string? lang, HttpContext http, IOptionsMonitor<ServerOptions> options) =>
        {
            var dlc = options.CurrentValue.Urls.FirstOrDefault(u => u is { Type: "DOWNLOAD", File.Length: > 0 });
            if (dlc is null || !File.Exists(dlc.FullPath))
                return Results.NotFound();
            var package = $"{http.Request.Scheme}://{http.Request.Host}/files/{Path.GetFileName(dlc.File)}";
            var description = dlc.Description.Length > 0 ? dlc.Description : ServerText.Get("dlc.description", lang);
            return Results.Text(DownloadFiles.Manifest(dlc, package, description), "text/plain", Encoding.UTF8);
        });

        // The RANK and RANK_WEEK urls. The client posts the ranking query as the body (some
        // paths may put it in the query string) and wraps our text in ranking_data="...".
        app.MapMethods("/ranking", ["GET", "POST"], async (HttpContext http, OpenEleven.Data.GameDbContext db, TimeProvider time,
            IOptionsMonitor<ServerOptions> options) =>
        {
            var data = http.Request.QueryString.Value ?? "";
            if (HttpMethods.IsPost(http.Request.Method))
            {
                // The real query is under 100 bytes. The limit can only be set before anything has
                // read the body (the HTTP trace middleware does when it is on), so the read is
                // capped as well.
                if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = 4096;
                using var reader = new StreamReader(http.Request.Body, Encoding.ASCII);
                var buffer = new char[4096];
                var read = await reader.ReadBlockAsync(buffer, http.RequestAborted);
                var body = new string(buffer, 0, read);
                if (body.Length > 0)
                    data = body;
            }

            var text = await new State.RankingQuery(db, options.CurrentValue.GameProfile).RenderAsync(data, time.GetUtcNow().UtcDateTime, http.RequestAborted);
            return Results.Text(text, "text/plain", Encoding.UTF8);
        }).RequireRateLimiting(HttpRateLimits.Ranking);

        // The terms screen: the client GETs the EULA_PES url and shows the body (read_buffer="...").
        // The url list puts the client's language in it (?lang=SP).
        app.MapMethods("/eula_pes.txt", ["GET", "HEAD"], (HttpContext http, IOptionsMonitor<ServerOptions> options) =>
            Results.Text(HttpMethods.IsHead(http.Request.Method) ? "" : EulaText(options.CurrentValue, http.Request.Query["lang"]),
                "text/plain", Encoding.UTF8));

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
            // PES 2010 posts game_id/game_id_password; PES 2011 posts konami_id/passwd (+ type=0),
            // seen in a live client run. The password is the same MD5 hex in both.
            var gameId = form["game_id"].ToString();
            if (gameId.Length == 0) gameId = form["konami_id"].ToString();
            var passwordHash = form["game_id_password"].ToString();
            if (passwordHash.Length == 0) passwordHash = form["passwd"].ToString();

            // The hash is password-equivalent, so it never reaches the log.
            log.LogInformation(
                "GAMEID auth: game_id={GameId} valid_md5={Valid}",
                gameId, GameIdAuthEndpoint.LooksLikeMd5(passwordHash));

            var authenticator = http.RequestServices.GetRequiredService<GameIdAuthService>();
            var result = await authenticator.AuthenticateAsync(
                gameId,
                passwordHash,
                http.Connection.RemoteIpAddress ?? System.Net.IPAddress.None,
                http.RequestAborted);

            if (!result.Success)
                log.LogWarning("GAMEID auth rejected for {GameId}: {Reason}", gameId, result.Reason);

            // Legacy client errors are encoded in the first response line, not HTTP status.
            return Results.Text(result.Body, "text/plain", Encoding.ASCII);
        }).WithMetadata(new SensitiveBodyAttribute());

        app.MapFallback(() => Results.NotFound());

        return app;
    }

    /// <summary>
    /// The terms text in <paramref name="lang"/>: the built-in translation when
    /// Protocol.EulaText is empty, the per-language file (eula.SP.txt next to eula.txt) or the
    /// file itself when it names one, else the configured text. Quotes become apostrophes:
    /// the client embeds the text in read_buffer="...".
    /// </summary>
    public static string EulaText(ServerOptions options, string? lang)
    {
        var eula = options.Protocol.EulaText;
        string text;
        if (eula.Length == 0)
        {
            text = ServerText.Get("eula.text", lang);
        }
        else if (File.Exists(eula))
        {
            // lang goes into a file name: letters only, or ?lang=../.. reads other files.
            if (lang is null || lang.Length is < 2 or > 3 || !lang.All(char.IsAsciiLetter))
                lang = "EN";
            var localized = Path.Combine(Path.GetDirectoryName(eula) ?? "",
                $"{Path.GetFileNameWithoutExtension(eula)}.{lang.ToUpperInvariant()}{Path.GetExtension(eula)}");
            text = File.ReadAllText(File.Exists(localized) ? localized : eula);
        }
        else
        {
            text = eula;
        }

        return text.Replace("\"", "'");
    }
}
