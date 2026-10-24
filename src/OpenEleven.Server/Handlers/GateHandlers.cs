// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Options;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Discovery stage: everything the client asks for before it has an account.
/// Served on the gate port, which is the one port the client knows without being told.
/// </summary>
public sealed class GateHandlers(
    IOptionsMonitor<ServerOptions> options,
    ServerCatalog catalog,
    WebAssets assets,
    ICatalogRepository repository)
{
    private const ServiceRole AnyServiceRole = ServiceRole.All;

    [Command("CMD_GET_SVRLIST", Roles = ServiceRole.Gate)]
    public ValueTask<KvMessage[]> GetServerList(CommandContext ctx)
    {
        if (options.CurrentValue.Protocol.RequireEulaBeforeServerList && !ctx.Session.EulaAccepted)
            return Reply.Of(ctx.Err("EULA"));

        return Reply.Of(ctx.Ok().SetList("server_num", "svrlist", catalog.BuildServerEntries()));
    }

    [Command("CMD_GET_SVRVERSION", Roles = AnyServiceRole)]
    public ValueTask<KvMessage[]> GetVersion(CommandContext ctx)
    {
        var protocol = options.CurrentValue.Protocol;
        return Reply.Of(ctx.Ok()
            .Set("version", protocol.ServerVersion)
            .Set("patch_version", protocol.PatchVersion)
            .Set("dlc_version", protocol.DlcVersion)
            .Set("eula_version", protocol.EulaVersion));
    }

    [Command("CMD_GET_SVRTIME", Roles = AnyServiceRole)]
    public ValueTask<KvMessage[]> GetTime(CommandContext ctx)
        => Reply.Of(ctx.Ok().Set("date", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    [Command("CMD_GET_EULA", Roles = ServiceRole.Gate | ServiceRole.Account)]
    public ValueTask<KvMessage[]> GetEula(CommandContext ctx)
    {
        ctx.Session.EulaAccepted = true;
        if (ctx.Session.State < SessionState.EulaAccepted)
            ctx.Session.State = SessionState.EulaAccepted;

        return Reply.Of(ctx.Ok().Set("eula", "1"));
    }

    [Command("CMD_GET_URLLIST", Roles = ServiceRole.Gate | ServiceRole.Account)]
    public ValueTask<KvMessage[]> GetUrlList(CommandContext ctx)
    {
        // The client fetches these over HTTP right after this reply and does not come
        // back until it succeeds, so the URLs must name the port actually being served.
        // Port 80 is left implicit to keep the payload identical to the reference server.
        var httpPort = options.CurrentValue.Listen.Http;
        var host = httpPort == 80
            ? $"http://{catalog.AdvertiseIp}"
            : $"http://{catalog.AdvertiseIp}:{httpPort}";

        var entries = new[]
        {
            UrlEntry("PC_SPEC", $"{host}/pcspec.bin", assets.SizeOf("/pcspec.bin")),
            UrlEntry("KONAMIID", $"{host}/register", assets.SizeOf("/register")),
            UrlEntry("GAMEID_AUTH", $"{host}/gameid_auth", assets.SizeOf("/gameid_auth")),
            UrlEntry("EULA_PES", $"{host}/eula_pes.txt", assets.SizeOf("/eula_pes.txt")),
        };

        return Reply.Of(ctx.Ok().SetList("url_num", "ulist", entries));
    }

    [Command("CMD_GET_INFORMATIONLIST", Roles = AnyServiceRole)]
    public async ValueTask<KvMessage[]> GetInformation(CommandContext ctx)
    {
        var items = await repository.GetInformationAsync(ctx.CancellationToken);

        var entries = items.Select(i => new KvMessage()
            .Set("date", new DateTimeOffset(i.PublishedAt, TimeSpan.Zero).ToUnixTimeSeconds())
            .Set("info_id", i.Id)
            .Set("mes_subject", i.Subject)
            .Set("mes_body", i.Body)
            .Set("important", i.Important ? "1" : "0")
            .Set("present", i.Present ? "1" : "0")).ToArray();

        return [ctx.Ok().SetList("info_num", "ilist", entries)];
    }

    [Command("CMD_DISCONNECT", Roles = AnyServiceRole)]
    public ValueTask<KvMessage[]> Disconnect(CommandContext ctx)
    {
        // The reference server echoed the whole server list back on disconnect; the
        // client tolerates it and it lets a reconnect skip a round trip.
        return Reply.Of(ctx.Ok().SetList("server_num", "svrlist", catalog.BuildServerEntries()));
    }

    private static KvMessage UrlEntry(string type, string url, int size) => new KvMessage()
        .Set("type", type)
        .Set("url", url)
        .Set("version", 1)
        .Set("file_size", size)
        .Set("md5", new KvRaw("0"));
}
