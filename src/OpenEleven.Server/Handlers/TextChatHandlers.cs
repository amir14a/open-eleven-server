// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>Persistent live-chat subscription and ROOM/BLOCK delivery.</summary>
public sealed class TextChatHandlers(ChatTextPolicy textPolicy, ILogger<TextChatHandlers> log)
{
    private const ServiceRole ChatRoles = ServiceRole.Menu | ServiceRole.Lobby | ServiceRole.FdLobby;

    /// <summary>
    /// Parks the client's long-lived chat subscription. This request is never answered
    /// here: its reply IS the chat delivery, written later by
    /// <see cref="Hub.PublishTextChat"/> reusing this rqid.
    /// </summary>
    /// <remarks>
    /// FUN_0074A1C0 arms exactly one watch per transition into the MENU or LOBBY service
    /// and feeds whatever comes back straight into FUN_00739D10, so:
    /// <list type="bullet">
    /// <item>Answering with a bare NOERR spends the client's only delivery slot on an
    /// empty message, and it will not re-arm until the service changes again.</item>
    /// <item>Answering with an error is worse: FUN_0074A600(99999) drops the chat module
    /// into a terminal state and no further watch is ever sent for the session.</item>
    /// </list>
    /// Both failure modes are silent and permanent, so this handler refuses the watch only
    /// when parking it would be meaningless, and never over a recoverable disagreement.
    /// </remarks>
    [Command("CMD_WATCH_TEXTCHAT", Roles = ChatRoles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> WatchTextChat(CommandContext ctx)
    {
        // Only ERR_ROOMSTATNOTMATCH, ERR_ISNOTGM and ERR_CHATENABLE are in the client's
        // error table for this command (pes2010.exe 0x01145930-0x0114599F). Anything else,
        // including the dispatcher's generic result="ERR", is outside its vocabulary.
        if (!ctx.Session.ChatEnabled)
            return Reply.Of(ctx.Fail("ERR_CHATENABLE"));

        // The client sends no fields at all, so the scene is inferred rather than checked.
        // A stale request after the player moved between lobby and room is re-pointed
        // instead of refused: refusing would kill chat permanently.
        var scene = ParseScene(ctx.Request.GetString("scene")) ?? CurrentScene(ctx.Session);

        ctx.Session.TextChatWatch = new TextChatSubscription(ctx.Rqid, scene);

        log.LogInformation(
            "Text chat watch parked for pid {Pid} on {Role}/{Scene} rqid={Rqid}",
            ctx.Session.Pid, ctx.Session.Role, scene, ctx.Rqid);

        // Deliberately no reply. The request stays open until there is something to say.
        return Reply.None();
    }

    // Gated at PlayerSelected, not InBlock: the client's send path is not scene-gated at
    // all, so a rejection from the dispatcher would arrive as a generic result="ERR" that
    // this command's error table does not contain.
    [Command("CMD_SEND_TEXTCHAT", Roles = ChatRoles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> SendTextChat(CommandContext ctx)
    {
        // FUN_0074A1C0 sends scene, channel, statement, list_flg and list_pid, then only
        // requires a normal NOERR acknowledgement for CMD_SEND_TEXTCHAT itself.
        // ERR_NOTFOUNDCLIENT is a real code but belongs to CMD_UPDATE_COMBINATION and
        // CMD_SEND_SHORTMAIL. This command's table (0x011460A0-0x011461A3) accepts only
        // ERR_PLAYERISNOTGAMER, ERR_ROOMSTATNOTMATCH, ERR_ROOMNOTFOUND,
        // ERR_TARGETISNOTLOGIN, ERR_TARGETINGAME, ERR_ISNOTGM and ERR_CHATENABLE.
        if (ctx.Session.Pid <= 0)
            return Reply.Of(ctx.Fail("ERR_PLAYERISNOTGAMER"));
        if (!ctx.Session.ChatEnabled)
            return Reply.Of(ctx.Fail("ERR_CHATENABLE"));

        var scene = ParseScene(ctx.Request.GetString("scene"));
        var channel = ParseChannel(ctx.Request.GetString("channel"));
        var listMode = ParseListMode(ctx.Request.GetString("list_flg"));

        // Chat is a broadcast, so the routing fields describe where the sender is, not who
        // is entitled to hear it. Nothing here is used to narrow delivery, and a
        // disagreement is logged rather than refused: the session that owns the sender's
        // room lives on a different connection, so scene and room checks against *this*
        // session would reject perfectly valid traffic.
        // PES 2011 sends no scene at all, so a missing one is not unusual there.
        if ((scene is null && ctx.Request.Has("scene")) || channel is null || listMode != TextChatListMode.ChatBan)
            log.LogWarning(
                "Unusual text chat route from pid {Pid}: scene={Scene} channel={Channel} list={ListMode}",
                ctx.Session.Pid,
                ctx.Request.GetString("scene"),
                ctx.Request.GetString("channel"),
                ctx.Request.GetString("list_flg"));

        if (!textPolicy.TrySanitize(
                ctx.Request.GetString("statement"), out var statement, out var censored))
            return Reply.Of(ctx.Fail("ERR_CHATENABLE"));

        var excludedPids = ReadPidSet(ctx.Request.GetValue("list_pid"));

        // Queue the request ACK first. The same socket may also be its own watch
        // recipient, and the client should complete CMD_SEND_TEXTCHAT before its echo.
        if (!ctx.Session.Push(ctx.Ok()))
            log.LogWarning("Outbound queue full; dropped CMD_SEND_TEXTCHAT ACK for {Session}", ctx.Session);

        var recipientCount = ctx.Hub.PublishTextChat(
            ctx.Session, channel ?? TextChatChannel.Block, statement, excludedPids);

        log.LogInformation(
            "Text chat from pid {Pid} channel={Channel} recipients={Recipients} censored={Censored}",
            ctx.Session.Pid, channel, recipientCount, censored);

        return Reply.None();
    }

    private static TextChatScene CurrentScene(Session session)
        => session.RoomId is null ? TextChatScene.Lobby : TextChatScene.Room;

    private static TextChatScene? ParseScene(string? value)
        => value switch
        {
            "LOBBY" => TextChatScene.Lobby,
            "ROOM" => TextChatScene.Room,
            _ => null,
        };

    private static TextChatChannel? ParseChannel(string? value)
        => value switch
        {
            "BLOCK" => TextChatChannel.Block,
            "ROOM" => TextChatChannel.Room,
            "TEAM" => TextChatChannel.Team,
            "GAME_QUICK" => TextChatChannel.GameQuick,
            "TEAM_QUICK" => TextChatChannel.TeamQuick,
            "COMPETITION" => TextChatChannel.Competition,
            "COMMUNITY" => TextChatChannel.Community,
            _ => null,
        };

    private static TextChatListMode? ParseListMode(string? value)
        => value switch
        {
            "CHAT_BAN" => TextChatListMode.ChatBan,
            "CHAT_SEND" => TextChatListMode.ChatSend,
            _ => null,
        };

    private static HashSet<int> ReadPidSet(object? value)
    {
        if (value is not KvArray array)
            return [];

        var result = new HashSet<int>();
        foreach (var item in array.Values)
        {
            var text = item switch
            {
                KvRaw raw => raw.Text,
                _ => Convert.ToString(item, CultureInfo.InvariantCulture),
            };
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                && pid > 0)
                result.Add(pid);
        }

        return result;
    }
}
