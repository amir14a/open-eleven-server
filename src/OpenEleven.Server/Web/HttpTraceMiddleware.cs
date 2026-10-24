// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text;
using Microsoft.Extensions.Options;
using OpenEleven.Protocol.Framing;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Web;

/// <summary>
/// Logs the client's HTTP traffic in the same shape the reference implementation did.
/// Reverse engineering depends on seeing the raw request, so this stays on by default
/// and is switched off through Debug.HexDump when the noise is not wanted.
/// Bodies of endpoints marked <see cref="SensitiveBodyAttribute"/> are never written out.
/// </summary>
public sealed class HttpTraceMiddleware(
    RequestDelegate next,
    IOptionsMonitor<ServerOptions> options,
    ILogger<HttpTraceMiddleware> log)
{
    private const int MaxLoggedBodyBytes = 64 * 1024;

    /// <summary>Headers that carry credentials and have no reverse-engineering value.</summary>
    private static readonly string[] RedactedHeaders = ["Cookie", "Set-Cookie", "Authorization"];

    /// <summary>
    /// Pure so it can be tested without a host. Depends on routing having run, which is why
    /// Program.cs calls UseRouting() explicitly ahead of this middleware rather than relying
    /// on WebApplication inserting it implicitly.
    /// </summary>
    public static bool ShouldRedactBody(Endpoint? endpoint)
        => endpoint?.Metadata.GetMetadata<SensitiveBodyAttribute>() is not null;

    public async Task InvokeAsync(HttpContext context)
    {
        var debug = options.CurrentValue.Debug;
        if (!debug.HexDump)
        {
            await next(context);
            return;
        }

        var request = context.Request;
        var trace = new StringBuilder();
        trace.Append($"HTTP {request.Method} {request.Path}{request.QueryString}");

        foreach (var header in request.Headers)
        {
            var value = RedactedHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)
                ? "<redacted>"
                : header.Value.ToString();
            trace.Append($"\n  {header.Key}: {value}");
        }

        if (request.ContentLength is > 0)
        {
            if (ShouldRedactBody(context.GetEndpoint()))
            {
                // Not read at all: cheaper, and nothing sensitive is materialised.
                trace.Append($"\n  body: <redacted, {request.ContentLength} bytes>");
            }
            else
            {
                request.EnableBuffering();
                var body = await ReadBodyAsync(request);
                request.Body.Position = 0;

                trace.Append($"\n  body ({body.Length} bytes):\n{HexDump.Format(body)}");
                trace.Append($"\n  body text: {Encoding.UTF8.GetString(body)}");
            }
        }

        log.LogInformation("{Trace}", trace.ToString());

        await next(context);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        return bytes.Length > MaxLoggedBodyBytes ? bytes[..MaxLoggedBodyBytes] : bytes;
    }
}
