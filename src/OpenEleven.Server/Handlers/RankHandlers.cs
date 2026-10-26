// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Rank screens: the ranking tables (CMD_GET_RANKING), the archived weeks, the
/// season-end division report and the player's notices.
/// </summary>
public sealed class RankHandlers(GameDbContext db, TimeProvider time, ILogger<RankHandlers> log)
{
    private const ServiceRole AccountRoles = ServiceRole.Account | ServiceRole.Menu | ServiceRole.Gate;

    /// <summary>Notices shown per request; older ones stay stored.</summary>
    private const int NoticesShown = 10;

    /// <summary>Archived weeks offered in the weekly ranking list.</summary>
    private const int WeeksShown = 8;

    /// <summary>
    /// The pid the client names in <paramref name="key"/>, if it is a player on the logged-in
    /// account. These requests come before CMD_SET_CURRENTPLAYER and on a connection of their
    /// own, so the session's pid is not set yet; reading marks the data seen, so another
    /// account's player must not count.
    /// </summary>
    private async Task<int?> OwnPidAsync(CommandContext ctx, string key)
    {
        var pid = ctx.Request.GetInt32(key, ctx.Session.Pid);
        if (await db.Players.AnyAsync(p => p.Pid == pid && p.AccountId == ctx.Session.AccountId, ctx.CancellationToken))
            return pid;

        log.LogWarning("{Msg} from {Session} named pid {Pid}, which is not on its account", ctx.Msg, ctx.Session, pid);
        return null;
    }

    /// <summary>
    /// The player's messages (FUN_00513000): count, then privInfo[i].reldate (unix), iid,
    /// title, info and important (enum 0x01, TRUE|FALSE). Promotions and friend requests land here.
    /// </summary>
    [Command("CMD_GET_PRIVATEINFO", Roles = AccountRoles, RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> GetPrivateInfo(CommandContext ctx)
    {
        // Each notice is shown once: what goes out now is marked delivered and not sent again.
        if (await OwnPidAsync(ctx, "pid") is not { } pid)
            return [ctx.Ok().Set("count", 0)];
        var notices = await db.Notices.Where(n => n.Pid == pid && !n.Delivered)
            .OrderByDescending(n => n.CreatedAt).Take(NoticesShown).ToListAsync(ctx.CancellationToken);
        foreach (var n in notices)
            n.Delivered = true;
        await db.SaveChangesAsync(ctx.CancellationToken);

        // FUN_00731C60 (PES 2010) takes a bare count=0, as the reference server sent it.
        if (notices.Count == 0)
            return [ctx.Ok().Set("count", 0)];

        var entries = notices.Select(n => new KvMessage()
            .Set("reldate", RankSystem.Unix(n.CreatedAt))
            .Set("iid", n.Id)
            .Set("title", n.Title)
            .Set("info", n.Body)
            .Set("important", n.Important ? "TRUE" : "FALSE")
            // FUN_00731C60 requires this field too and stops parsing the entry without it.
            // Enum type 0 is FALSE/TRUE.
            .Set("present", "TRUE")).ToArray();
        return [ctx.Ok().SetList("count", "privInfo", entries)];
    }

    /// <summary>
    /// Shown at login (FUN_00512700): updated YES|NO, then div_result.newdiv/olddiv (enum 0x16),
    /// newrank/oldrank, date, rating and divcount[0..5] (players per division, D5..D1, NONE).
    /// A season move is reported once, then cleared.
    /// </summary>
    [Command("CMD_GET_DIVISIONUPDATE", Roles = AccountRoles, RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> GetDivisionUpdate(CommandContext ctx)
    {
        if (await OwnPidAsync(ctx, "mypid") is not { } pid)
            return [ctx.Ok().Set("updated", false)];
        var player = await db.Players.FindAsync([pid], ctx.CancellationToken);
        var parts = player?.PendingDivisionUpdate.Split(',') ?? [];
        if (player is null || parts.Length != 5)
            return [ctx.Ok().Set("updated", false)];

        var counts = await db.Players.GroupBy(p => p.Division).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ctx.CancellationToken);
        var divcount = RankSystem.Divisions.Select(d => (object?)counts.GetValueOrDefault(d)).Append(0).ToArray();

        player.PendingDivisionUpdate = "";
        await db.SaveChangesAsync(ctx.CancellationToken);

        return
        [
            ctx.Ok().Set("updated", true).Set("div_result", new KvMessage()
                .Set("newdiv", parts[2])
                .Set("newrank", int.Parse(parts[3], CultureInfo.InvariantCulture))
                .Set("olddiv", parts[0])
                .Set("oldrank", int.Parse(parts[1], CultureInfo.InvariantCulture))
                .Set("date", long.Parse(parts[4], CultureInfo.InvariantCulture))
                .Set("rating", player.Rating)
                .Set("divcount", new KvArray(divcount))
                .Set("rankflag", new KvArray([0, 0]))),
        ];
    }

    /// <summary>
    /// The real client fetches the ranking over HTTP (see <see cref="RankingQuery"/> and the
    /// /ranking endpoint); answering it on the socket too costs nothing.
    /// </summary>
    [Command("CMD_GET_RANKING", Roles = ServiceRole.Menu)]
    public async ValueTask<KvMessage[]> GetRanking(CommandContext ctx)
    {
        var text = await new RankingQuery(db).RenderAsync(ctx.Request.GetString("data") ?? "", time.GetUtcNow().UtcDateTime, ctx.CancellationToken);
        return [ctx.Ok().Set("ranking_data", text)];
    }

    /// <summary>
    /// The archived weeks (FUN_005f2870/FUN_005f2be0): date_list_num, then
    /// date_list[i].start_date / end_date as unix seconds, newest first.
    /// </summary>
    [Command("CMD_GET_WEEKLYRANKINGDATELIST", Roles = ServiceRole.Menu)]
    public async ValueTask<KvMessage[]> GetWeeklyRankingDates(CommandContext ctx)
    {
        var weeks = await RankingQuery.ArchivedWeeksAsync(db, WeeksShown, ctx.CancellationToken);
        if (weeks.Count == 0)
            return [ctx.Fail("ERR_WEEKLYRANKINGNOTFOUND")];

        var entries = weeks.Select(start => new KvMessage()
            .Set("start_date", start)
            .Set("end_date", start + 7 * 24 * 3600 - 1)).ToArray();
        return [ctx.Ok().SetList("date_list_num", "date_list", entries)];
    }

}
