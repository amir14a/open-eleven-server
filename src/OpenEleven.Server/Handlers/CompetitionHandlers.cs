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

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Competitions (KONAMI CUP ONLINE): browsing, the detail page, entry and withdrawal.
/// Competitions come from config; a player's entry is <c>Players.CompetitionId</c>.
/// </summary>
/// <remarks>
/// The client parsers drop a whole reply when one required key is missing or one enum name is
/// unknown, so every key below is read by them (list FUN_0055cdb0, detail FUN_0055daf0, kinds
/// FUN_00565da0, my entry FUN_0055c9d0). Times are Unix seconds.
/// </remarks>
public sealed class CompetitionHandlers(
    IPlayerRepository players,
    IOptionsMonitor<ServerOptions> options,
    TimeProvider time)
{
    private const ServiceRole Roles = ServiceRole.Menu;

    /// <summary>FUN_0055cdb0 rejects compe_num above 20.</summary>
    private const int MaxListed = 20;

    private IEnumerable<CompetitionOptions> Competitions
        => options.CurrentValue.Competitions.Where(c => c.Enabled && c.Name.Length > 0);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    [Command("CMD_GET_COMPLG_KIND_LIST", Roles = Roles)]
    public ValueTask<KvMessage[]> GetKindList(CommandContext ctx)
    {
        var kinds = Competitions.GroupBy(c => c.Kind)
            .Select(g => new KvMessage().Set("compe_kind", g.Key).Set("compe_name", g.First().Name))
            .ToArray();
        return Reply.Of(ctx.Ok().SetList("compe_kind_num", "compe_kind_list", kinds));
    }

    /// <summary>
    /// compe_list_kind (enum 0x0b) must be echoed, and for OLD_ALL compe_kind too; otherwise the
    /// client hardcodes KONAMI_CUP_ONLINE, so the kind only filters the archive.
    /// </summary>
    [Command("CMD_GET_COMPLG_LIST", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetList(CommandContext ctx)
    {
        var listKind = ctx.Request.GetString("compe_list_kind") ?? "NOW_PLAY";
        var kind = ctx.Request.GetString("compe_kind");
        var now = Now;
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);

        bool Listed(CompetitionOptions c) => listKind switch
        {
            "SCHEDULE" => now < c.EntryStart,
            "NOW_PLAY" or "NOW_WATCH" => now >= c.EntryStart && now < c.End,
            "OLD_ALL" => now >= c.End && c.Kind == kind,
            "OLD_MY" => now >= c.End && player?.CompetitionId == c.Id,
            _ => false,
        };

        var entries = new List<KvMessage>();
        foreach (var c in Competitions.Where(Listed).OrderBy(c => c.EntryStart).Take(MaxListed))
            entries.Add(await Summary(c, ctx.CancellationToken));

        var reply = ctx.Ok().Set("compe_list_kind", listKind);
        if (listKind == "OLD_ALL")
            reply.Set("compe_kind", kind ?? "");
        return [reply.SetList("compe_num", "compe_list", entries)];
    }

    /// <summary>FUN_0055daf0: everything sits in compe_data, whose compe_id must be the one asked for.</summary>
    [Command("CMD_GET_COMPLG_INFO", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetInfo(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var teams = TitleProfiles.UseableTeams(options.CurrentValue.GameProfile);
        var data = (await Summary(c, ctx.CancellationToken))
            .Set("use_national", "TRUE")
            .Set("use_club", "TRUE")
            .Set("use_ml", "FALSE")
            .Set("useable_team_size", teams.Length)
            .Set("useable_team_id", new KvArray(teams.Cast<object?>().ToArray()))
            .Set("usable_ball_pre_num", 0)
            .Set("usable_ball_playoff_num", 0)
            .Set("usable_ball_final_num", 0)
            // Preliminary, playoff and final rounds.
            .Set("gametime", KvArray.Repeat(c.GameTime, 3))
            .Set("fee", 0)
            .Set("is_digest", "NO");
        return [ctx.Ok().Set("compe_data", data)];
    }

    /// <summary>FUN_0055c9d0: the entered compe_id, 0 when not entered or the entry is over.</summary>
    [Command("CMD_GET_COMPLG_MYENTRY_COMPEID", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetMyEntry(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var entered = player is not null && Find(player.CompetitionId) is { } c && Now < c.End ? c.Id : 0;
        return [ctx.Ok().Set("compe_id", entered)];
    }

    /// <summary>Only the error names in the client's table for CMD_JOIN_COMPLG are used.</summary>
    [Command("CMD_JOIN_COMPLG", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Join(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_DATABASE")];
        if (player.CompetitionId == c.Id)
            return [ctx.Ok()];

        var now = Now;
        if (now < c.EntryStart)
            return [ctx.Fail("ERR_COMPE_BEFORE_ENTRYTIME")];
        if (now >= c.EntryEnd)
            return [ctx.Fail("ERR_COMPE_AFTER_ENTRYTIME")];
        if (Find(player.CompetitionId) is { } other && now < other.End)
            return [ctx.Fail("ERR_COMPE_JOIN_OTHERCOMPE")];
        // ponytail: count-then-save race can overfill by a player or two; a DB constraint if it matters.
        if (await players.CountInCompetitionAsync(c.Id, ctx.CancellationToken) >= c.Capacity)
            return [ctx.Fail("ERR_COMPE_FULL")];

        player.CompetitionId = c.Id;
        await players.SaveAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    [Command("CMD_LEAVE_COMPLG", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Leave(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || player.CompetitionId == 0)
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];

        player.CompetitionId = 0;
        await players.SaveAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// FUN_00561a10 treats a reply without compe_name/trophy/name/team_id/pid/npid as "no
    /// champion yet", which is the truth until competition results exist.
    /// </summary>
    [Command("CMD_GET_COMPLG_TITLEHOLDER", Roles = Roles)]
    public ValueTask<KvMessage[]> GetTitleHolder(CommandContext ctx) => Reply.Of(ctx.Ok());

    private CompetitionOptions? Find(int id) => id == 0 ? null : Competitions.FirstOrDefault(c => c.Id == id);

    /// <summary>The keys the list and the detail page share.</summary>
    private async Task<KvMessage> Summary(CompetitionOptions c, CancellationToken ct)
        => new KvMessage()
            .Set("compe_kind", c.Kind)
            .Set("compe_id", c.Id)
            .Set("entry_start_time", Unix(c.EntryStart))
            .Set("entry_end_time", Unix(c.EntryEnd))
            .Set("match_start_time", Unix(c.MatchStart))
            .Set("match_end_time", Unix(c.MatchEnd))
            .Set("compe_end_time", Unix(c.End))
            .Set("compe_name", c.Name)
            .Set("status", Status(c, Now))
            .Set("category", c.Category)
            .Set("entry_limit", "DIVISION")
            .Set("entry_limit_rank", 0)
            // Non-empty, at most 15 characters (FUN_0055cdb0).
            .Set("dlcver", c.DlcVersion.Length > 0 ? c.DlcVersion : options.CurrentValue.Protocol.DlcVersion)
            .Set("player_capacity", c.Capacity)
            .Set("player_num", await players.CountInCompetitionAsync(c.Id, ct))
            .Set("champion_notice", "FALSE")
            .Set("guest_count", 0)
            .Set("is_coach_mode", "NO")
            .Set("need_konamiid", "NO")
            // D5..D1 and NONE, enum 0x01: every division may enter.
            .Set("entry_limit_division", KvArray.Repeat("TRUE", 6));

    /// <summary>Enum 0x0e, derived from the configured times.</summary>
    internal static string Status(CompetitionOptions c, DateTime now)
        => now < c.EntryStart ? "CREATED"
            : now < c.EntryEnd && now < c.MatchStart ? "ENTRY"
            : now < c.MatchEnd ? (now < c.MatchStart ? "ENTRY_CLOSED" : "STARTED")
            : now < c.End ? "MATCH_CLOSED"
            : "FINISHED";

    private static long Unix(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
