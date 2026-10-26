// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Quick match. In pes2011.exe the room FSM (FUN_005ed6a0) hands "start quick match" to the client's
/// matching engine, which talks MSG_QUICK_* to us and turns the outcome into its own
/// CMD_START_QUICKMATCH result (FUN_00fffdf0), so CMD_START_QUICKMATCH never reaches the wire:
/// <list type="number">
/// <item>MSG_QUICK_REQ_MATCHING_START → ack + MSG_QUICK_MATCHING_START_RES; the client waits 180 s.</item>
/// <item>Opponent found → MSG_QUICK_MATCH_MEMBER_NOTICE; each client pings the others and answers
/// MSG_QUICK_MATCH_MEMBER_REPLY.</item>
/// <item>All replied → MSG_QUICK_MATCHING_RES; 20 s P2P pre-connect, then MSG_QUICK_ACCEPT_MATCH_OFFER.</item>
/// <item>All accepted → the server puts them in a room and sends MSG_QUICK_ACCEPT_MATCH_OFFER_RES; the
/// room tail (CMD_SET_GAMEMEMBERENV, CMD_SET_GAMEMEMBER, in-match commands) is the lobby one.</item>
/// </list>
/// Quick match starts from the menu, before any lobby or block, hence RequiredState PlayerSelected.
/// </summary>
public sealed class QuickMatchHandlers(IPlayerRepository players, ILogger<QuickMatchHandlers> log)
{
    private const ServiceRole Roles = ServiceRole.Menu;

    /// <summary>
    /// Saved quick match options (FUN_00600200). All four keys are required, or the option screen
    /// ends in its error state; the three flags are enum 0x4c (NO|YES), opp_lang enum 0x1c.
    /// </summary>
    [Command("CMD_GET_QUICKMATCHOPTION", Roles = Roles)]
    public async ValueTask<KvMessage[]> GetOption(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var saved = (player?.QuickMatchOptions ?? "").Split(',');
        string At(int i, string fallback) => saved.Length == OptionKeys.Length && saved[i] != "" ? saved[i] : fallback;

        var reply = ctx.Ok();
        for (var i = 0; i < OptionKeys.Length; i++)
            reply.Set(OptionKeys[i], At(i, i == 0 ? "ANY" : "NO"));
        return [reply];
    }

    /// <summary>The options screen saves the same four keys (request built by FUN_005fff90).</summary>
    [Command("CMD_SET_QUICKMATCHOPTION", Roles = Roles)]
    public async ValueTask<KvMessage[]> SetOption(CommandContext ctx)
    {
        if (await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken) is { } player)
        {
            player.QuickMatchOptions = string.Join(',', OptionKeys.Select(k => ctx.Request.GetString(k) ?? ""));
            await players.SaveAsync(ctx.CancellationToken);
        }

        return [ctx.Ok()];
    }

    private static readonly string[] OptionKeys = ["opp_lang", "oc_is_rating_rank", "oc_is_rating_free", "ml_is_division"];

    [Command("CMD_SEND_QUICK_GAMEBEGINNING_HOPE", Roles = Roles)]
    [Command("CMD_CANCEL_QUICK_GAMEBEGINNING_HOPE", Roles = Roles)]
    public ValueTask<KvMessage[]> Ack(CommandContext ctx) => Reply.Of(ctx.Ok());

    /// <summary>Players searching right now, per mode (FUN_005dad00).</summary>
    [Command("CMD_GET_QUICKMATCHNUMBERS", Roles = Roles)]
    public ValueTask<KvMessage[]> GetNumbers(CommandContext ctx)
    {
        var q = ctx.Hub.QuickMatch;
        return Reply.Of(ctx.Ok()
            .Set("rank_1vs1_num", q.Searching("OC_Rank", 2))
            .Set("rank_2vs2_num", q.Searching("OC_Rank", 4))
            .Set("free_2vs2_num", q.Searching("OC_Free", 4)));
    }

    /// <summary>
    /// Start searching. Request: <c>type</c> (enum 0x40 OC_Rank|OC_Free|...), <c>player_num</c>,
    /// <c>game_time</c> (GT05|GT10), <c>opp_lang</c>, <c>frrate</c>, ... Players are paired on type,
    /// player count, game time and frame rate.
    /// </summary>
    [Command("MSG_QUICK_REQ_MATCHING_START", Roles = Roles,
        RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> RequestMatchingStart(CommandContext ctx)
    {
        var r = ctx.Request;
        var type = r.GetString("type") ?? "OC_Rank";
        var playerNum = Math.Clamp(r.GetInt32("player_num", 2), 2, 4);
        var gameTime = r.GetString("game_time") ?? "GT10";
        var frrate = r.GetString("frrate") ?? "FRAME_60";

        // The saved options: opp_lang, then the rating filters for Rank and Free.
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var saved = (player?.QuickMatchOptions ?? "").Split(',');
        string Saved(int i) => saved.Length == OptionKeys.Length ? saved[i] : "";
        var seeker = new QuickSeeker(ctx.Session, ctx.Rqid, type, playerNum, gameTime, frrate,
            $"{type}|{playerNum}|{gameTime}|{frrate}")
        {
            Lang = player?.Lang ?? "",
            OppLang = r.GetString("opp_lang") ?? (Saved(0) is { Length: > 0 } lang ? lang : "ANY"),
            Rating = player?.Rating ?? 0,
            RatingFilter = Saved(type == "OC_Free" ? 2 : 1) == "YES",
        };

        ctx.Session.Push(ctx.Ok());
        ctx.Session.Push(MatchingStartResponse(ctx.Rqid));
        log.LogInformation("pid {Pid} searching for a quick match ({Key})", ctx.Session.Pid, seeker.Key);

        if (ctx.Hub.QuickMatch.Search(seeker) is { } group)
            await SendMemberNoticeAsync(ctx, group);
        return [];
    }

    /// <summary>A client finished pinging the others; once all have, the match is offered.</summary>
    [Command("MSG_QUICK_MATCH_MEMBER_REPLY", Roles = Roles,
        RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> MemberReply(CommandContext ctx)
    {
        ctx.Session.Push(ctx.Ok());
        if (ctx.Hub.QuickMatch.GroupOf(ctx.Session) is { } group && group.Replied(ctx.Session.Pid))
            foreach (var m in group.Members)
                m.Session.Push(KvMessage.Ok("MSG_QUICK_MATCHING_RES", m.Rqid));
        return Reply.None();
    }

    /// <summary>
    /// After the pre-connect: <c>result</c> NOERR accepts, ERR_PING_REFUSAL turns the match down.
    /// Once everyone accepted, the room is created and MSG_QUICK_ACCEPT_MATCH_OFFER_RES lists the
    /// members (<c>member[i].pid|matching_order|frrate</c>; pids must be the notice's).
    /// </summary>
    [Command("MSG_QUICK_ACCEPT_MATCH_OFFER", Roles = Roles,
        RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> AcceptMatchOffer(CommandContext ctx)
    {
        ctx.Session.Push(ctx.Ok());
        var quick = ctx.Hub.QuickMatch;
        if (quick.GroupOf(ctx.Session) is not { } group)
            return Reply.None();

        if (ctx.Request.GetString("result") != "NOERR")
        {
            log.LogInformation("pid {Pid} refused the quick match ({Result})", ctx.Session.Pid, ctx.Request.GetString("result"));
            quick.Leave(ctx.Session);
            group.TellCancelled(ctx.Session);
            return Reply.None();
        }

        if (!group.Accepted(ctx.Session.Pid))
            return Reply.None();

        quick.Complete(group);
        var owner = group.Members[0];
        ctx.Hub.CreateQuickMatchRoom(
            group.Members.Select(m => m.Session).ToArray(),
            owner.Type == "OC_Rank" ? "OC_RANKING" : "OC_FREE",
            new KvMessage().Set("type", owner.Type).Set("game_time", owner.GameTime).Set("frrate", owner.Frrate));

        var list = group.Members.Select((m, i) => new KvMessage()
            .Set("pid", m.Session.Pid)
            .Set("matching_order", i)
            .Set("frrate", m.Frrate)).ToArray();
        foreach (var m in group.Members)
            m.Session.Push(KvMessage.Ok("MSG_QUICK_ACCEPT_MATCH_OFFER_RES", m.Rqid).Set("member", list));
        return Reply.None();
    }

    /// <summary>
    /// The client rejects this event as ERR_CLIENT_SVRFORMAT unless all six tuning fields
    /// are present, so they are always written together.
    /// </summary>
    private static KvMessage MatchingStartResponse(int rqid)
        => KvMessage.Ok("MSG_QUICK_MATCHING_START_RES", rqid)
            .Set("tryCount", 3)
            .Set("ping", 100)
            .Set("pingAdd", 50)
            .Set("bandTryCount", 3)
            .Set("band", 128)
            .Set("bandAdd", 64);

    /// <summary>The player stopped searching (FUN_005e9400); NOERR is what confirms it.</summary>
    [Command("CMD_CANCEL_STARTQUICKMATCH", Roles = Roles)]
    [Command("CMD_CANCEL_DECIDE_QUICKMATCH", Roles = Roles)]
    public ValueTask<KvMessage[]> Cancel(CommandContext ctx)
    {
        if (ctx.Hub.QuickMatch.Leave(ctx.Session) is { } group)
            group.TellCancelled(ctx.Session);
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// MSG_QUICK_MATCH_MEMBER_NOTICE, one per recipient because the endpoints are per pair
    /// (Hub.EndpointsFor). FUN_00fffdf0 rejects the notice as ERR_CLIENT_SVRFORMAT when any member
    /// key other than matching_order is missing; YES/NO fields are compared to "YES".
    /// </summary>
    private async Task SendMemberNoticeAsync(CommandContext ctx, QuickGroup group)
    {
        var profiles = new Dictionary<int, Data.Entities.Player?>();
        foreach (var m in group.Members)
            profiles[m.Session.Pid] = await players.GetAsync(m.Session.Pid, ctx.CancellationToken);

        log.LogInformation("Quick match formed: pids {Pids}", string.Join(", ", group.Members.Select(m => m.Session.Pid)));
        foreach (var viewer in group.Members)
        {
            var entries = group.Members.Select((m, i) =>
            {
                var s = m.Session;
                var p = profiles[s.Pid];
                var at = ctx.Hub.EndpointsFor(viewer.Session, s);
                return new KvMessage()
                    .Set("matching_order", i)
                    .Set("is_owner", i == 0 ? "YES" : "NO")
                    .Set("pid", s.Pid)
                    .Set("pname", s.PlayerName)
                    .Set("has_guestplayer", "NO")
                    .Set("is_begin", "NO")
                    .Set("ex_ip", at.ExternalIp)
                    .Set("ex_port", at.ExternalPort)
                    .Set("in_ip", at.InternalIp)
                    .Set("in_port", at.InternalPort)
                    .Set("manner", p?.Manner ?? 3)
                    .Set("from_country", p?.Country ?? 0)
                    .Set("from_area", p?.Area ?? 0)
                    .Set("player_name", s.PlayerName)
                    .Set("fd_pic_url", "")
                    .Set("fd_selfreport_level", p?.SelfReportLevel ?? "")
                    .Set("fd_beginnermark", p?.BeginnerMark == true ? "YES" : "NO")
                    .Set("fd_position_want", p?.PositionWant ?? "")
                    .Set("fd_desired_position", p?.DesiredPositionMask ?? 0)
                    .Set("division", RankSystem.Label(p?.Division, ctx.Profile))
                    .Set("rating", p?.Rating ?? 0)
                    .Set("ml_division", "ML_DIVISION_5")
                    .Set("ml_point", 0);
            }).ToArray();

            viewer.Session.Push(KvMessage.Ok("MSG_QUICK_MATCH_MEMBER_NOTICE", viewer.Rqid)
                .Set("is_old_member", "NO")
                .SetList("member_num", "member", entries));
        }
    }
}
