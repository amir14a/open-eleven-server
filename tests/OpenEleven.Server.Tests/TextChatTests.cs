// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class TextChatTests
{
    [Fact]
    public async Task Room_chat_acknowledges_then_echoes_and_delivers_through_each_watch()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var host = ChatSession(harness, 1, "Host");
        var roomId = (await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM"))[0]
            .GetInt32("room_id");

        var guest = ChatSession(harness, 2, "Guest");
        await harness.DispatchAsync(guest, "CMD_CREATEJOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        await WatchAsync(harness, host, "ROOM", rqid: 41);
        await WatchAsync(harness, guest, "ROOM", rqid: 52);

        var senderReplies = await SendAsync(
            harness, host, scene: "ROOM", channel: "ROOM", statement: "hello room");

        Assert.Equal("CMD_SEND_TEXTCHAT", senderReplies[0].MsgName);
        Assert.Equal("CMD_WATCH_TEXTCHAT", senderReplies[1].MsgName);
        Assert.Equal(41, senderReplies[1].Rqid);
        AssertDelivery(senderReplies[1], host, "ROOM", "hello room");

        var guestDelivery = Assert.Single(Drain(guest));
        Assert.Equal(52, guestDelivery.Rqid);
        AssertDelivery(guestDelivery, host, "ROOM", "hello room");
    }

    [Fact]
    public async Task Block_chat_is_contained_to_the_block_and_honors_chat_ban_pids()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Sender");
        var recipient = ChatSession(harness, 2, "Recipient");
        var excluded = ChatSession(harness, 3, "Excluded");
        var sameBlockOtherService = ChatSession(harness, 4, "Other service", ServiceRole.Menu);
        var otherBlock = ChatSession(harness, 5, "Other block");
        otherBlock.BlockId = 2;

        await WatchAsync(harness, sender, "LOBBY", 11);
        await WatchAsync(harness, recipient, "LOBBY", 12);
        await WatchAsync(harness, excluded, "LOBBY", 13);
        await WatchAsync(harness, sameBlockOtherService, "LOBBY", 14);
        await WatchAsync(harness, otherBlock, "LOBBY", 15);

        var senderReplies = await SendAsync(
            harness,
            sender,
            scene: "LOBBY",
            channel: "BLOCK",
            statement: "hello block",
            listPid: new KvArray(new object?[] { 0, excluded.Pid }));

        Assert.Equal(2, senderReplies.Count);
        AssertDelivery(senderReplies[1], sender, "BLOCK", "hello block");
        AssertDelivery(Assert.Single(Drain(recipient)), sender, "BLOCK", "hello block");

        // Same block, different service connection: still in the area.
        AssertDelivery(Assert.Single(Drain(sameBlockOtherService)), sender, "BLOCK", "hello block");

        // Contained: a different block never hears it.
        Assert.Empty(Drain(otherBlock));

        // The sender's block list removes a player inside the area.
        Assert.Empty(Drain(excluded));
    }

    [Fact]
    public async Task Room_chat_is_contained_to_the_room()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var host = ChatSession(harness, 1, "Host");
        var guest = ChatSession(harness, 2, "Guest");
        var inBlockOnly = ChatSession(harness, 3, "Block only");

        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");
        await harness.DispatchAsync(guest, "CMD_CREATEJOIN_ROOM", withFields: request =>
            request.Set("room_id", roomId));

        await WatchAsync(harness, host, "ROOM", 31);
        await WatchAsync(harness, guest, "ROOM", 32);
        await WatchAsync(harness, inBlockOnly, "LOBBY", 33);

        await SendAsync(harness, host, scene: "ROOM", channel: "ROOM", statement: "in room");

        AssertDelivery(Assert.Single(Drain(guest)), host, "ROOM", "in room");

        // Same block, but not in the room: contained.
        Assert.Empty(Drain(inBlockOnly));
    }

    [Fact]
    public async Task One_player_on_several_connections_is_delivered_to_once()
    {
        // A player holds a socket per service but has a single chat window.
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Sender");
        var onLobby = ChatSession(harness, 2, "Two connections");
        var onMenu = ChatSession(harness, 2, "Two connections", ServiceRole.Menu);

        await WatchAsync(harness, onMenu, "LOBBY", 41);

        await SendAsync(harness, sender, "LOBBY", "BLOCK", "once please");

        // The connection with the parked watch is the one written to.
        AssertDelivery(Assert.Single(Drain(onMenu)), sender, "BLOCK", "once please");
        Assert.Empty(Drain(onLobby));
    }

    [Fact]
    public async Task A_session_with_no_parked_watch_still_receives_the_broadcast()
    {
        // The client arms a watch only on a screen transition, so most sessions have none
        // for most of their life. Requiring one would discard everything said in between.
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Sender");
        var neverWatched = ChatSession(harness, 2, "Never watched");

        await SendAsync(harness, sender, "LOBBY", "BLOCK", "heard me?");

        var delivery = Assert.Single(Drain(neverWatched));
        Assert.Equal(Hub.UnsolicitedTextChatRqid, delivery.Rqid);
        AssertDelivery(delivery, sender, "BLOCK", "heard me?");
    }

    [Fact]
    public async Task The_sender_hears_its_own_message_even_when_alone()
    {
        // There is no local echo in the client: FUN_00739D10 is the only path that writes
        // into the chat window, so a lone player sees nothing unless the server sends it.
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Alone");

        var replies = await SendAsync(harness, sender, "LOBBY", "BLOCK", "anyone there?");

        Assert.Equal("CMD_SEND_TEXTCHAT", replies[0].MsgName);
        AssertDelivery(replies[1], sender, "BLOCK", "anyone there?");
    }

    [Fact]
    public async Task Block_chat_reaches_a_subscribed_player_while_inside_a_room()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Sender");
        var roomPlayer = ChatSession(harness, 2, "Room player");
        await harness.DispatchAsync(roomPlayer, "CMD_CREATEJOIN_ROOM");

        await WatchAsync(harness, sender, "LOBBY", 21);
        await WatchAsync(harness, roomPlayer, "ROOM", 22);

        await SendAsync(harness, sender, "LOBBY", "BLOCK", "block notice");

        var delivery = Assert.Single(Drain(roomPlayer));
        Assert.Equal(22, delivery.Rqid);
        AssertDelivery(delivery, sender, "BLOCK", "block notice");
    }

    [Fact]
    public async Task Configured_terms_are_replaced_without_rejecting_the_message()
    {
        await using var harness = await ServerHarness.CreateAsync(settings =>
            settings["Server:Protocol:BlockedTerms:0"] = "insult");
        var sender = ChatSession(harness, 1, "Sender");
        await WatchAsync(harness, sender, "LOBBY", 31);

        var replies = await SendAsync(
            harness, sender, "LOBBY", "BLOCK", "An INSULT, then insulted and insult.");

        Assert.Equal("An ***, then insulted and ***.", replies[1].GetString("statement"));
    }

    [Fact]
    public async Task Rewatch_replaces_the_delivery_rqid()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Sender");
        await WatchAsync(harness, sender, "LOBBY", 7);
        await WatchAsync(harness, sender, "LOBBY", 99);

        var replies = await SendAsync(harness, sender, "LOBBY", "BLOCK", "latest watch");

        Assert.Equal(99, replies[1].Rqid);
    }

    [Theory]
    [InlineData("TEAM", "CHAT_BAN")]
    [InlineData("ROOM", "CHAT_SEND")]
    [InlineData("COMMUNITY", "CHAT_BAN")]
    public async Task An_unusual_route_is_still_delivered_rather_than_refused(
        string channel, string listMode)
    {
        // The routing fields describe where the sender is, not who may hear it. Refusing
        // on a disagreement would drop valid traffic, because the session that owns the
        // sender's room is a different connection from the one carrying the chat.
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Sender");

        var replies = await harness.DispatchAsync(sender, "CMD_SEND_TEXTCHAT", withFields: request =>
            request
                .Set("scene", "LOBBY")
                .Set("channel", channel)
                .Set("statement", "test")
                .Set("list_flg", listMode)
                .Set("list_pid", new KvArray(new object?[] { 0 })));

        // Accepted, not refused. Where it lands is the area's business: a ROOM channel
        // from a player who is in no room simply has an empty scope.
        Assert.Equal("NOERR", replies[0].GetString("result"));
        Assert.Equal("CMD_SEND_TEXTCHAT", replies[0].MsgName);
    }

    [Fact]
    public async Task Invalid_or_disabled_chat_uses_native_chat_error()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Sender");

        var oversized = await SendAsync(
            harness,
            sender,
            "LOBBY",
            "BLOCK",
            new string('x', ChatTextPolicy.MaxEncodedBytes + 1));
        Assert.Equal("ERR_CHATENABLE", oversized[0].GetString("result"));

        sender.ChatEnabled = false;
        var watch = await harness.DispatchAsync(sender, "CMD_WATCH_TEXTCHAT", withFields: request =>
            request.Set("scene", "LOBBY"));
        Assert.Equal("ERR_CHATENABLE", watch[0].GetString("result"));
    }

    [Fact]
    public async Task Profile_chat_setting_is_synchronized_across_account_sessions()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var editor = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        await harness.DispatchAsync(editor, "CMD_SET_CURRENTPLAYER");

        var sibling = harness.NewSession(ServiceRole.Lobby, SessionState.PlayerSelected);
        sibling.AccountId = editor.AccountId;
        sibling.Pid = editor.Pid;
        sibling.ChatEnabled = true;
        sibling.TextChatWatch = new TextChatSubscription(4, TextChatScene.Lobby);

        await harness.DispatchAsync(editor, "CMD_SET_PLAYERPROFILE", withFields: request =>
            request.Set("profile", new KvMessage().Set("enable_chat", "NO")));

        Assert.False(editor.ChatEnabled);
        Assert.False(sibling.ChatEnabled);
        Assert.Null(sibling.TextChatWatch);
    }

    [Fact]
    public async Task Adopted_identity_carries_chat_permission()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var donor = ChatSession(harness, 1, "Donor", ServiceRole.Account);
        donor.State = SessionState.PlayerSelected;
        donor.ChatEnabled = false;

        var adopted = harness.NewSession(ServiceRole.Lobby, SessionState.Connected);

        Assert.True(harness.Hub.TryAdoptIdentity(adopted));
        Assert.False(adopted.ChatEnabled);
    }

    [Fact]
    public async Task Full_recipient_queue_drops_delivery_without_blocking_sender_ack()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var sender = ChatSession(harness, 1, "Sender");
        var recipient = ChatSession(harness, 2, "Recipient");
        await WatchAsync(harness, sender, "LOBBY", 61);
        await WatchAsync(harness, recipient, "LOBBY", 62);

        for (var i = 0; i < 512; i++)
            Assert.True(recipient.Push(KvMessage.Ok("MSG_FILL", i + 1)));
        Assert.False(recipient.Push(KvMessage.Ok("MSG_OVERFLOW", 513)));

        var replies = await SendAsync(harness, sender, "LOBBY", "BLOCK", "still responsive");

        Assert.Equal("CMD_SEND_TEXTCHAT", replies[0].MsgName);
        Assert.Equal("CMD_WATCH_TEXTCHAT", replies[1].MsgName);
        Assert.Equal(512, Drain(recipient).Count);
    }

    [Fact]
    public async Task The_watch_is_parked_before_the_player_reaches_a_block()
    {
        // FUN_0074A1C0 arms the watch on entering the MENU or LOBBY service, which happens
        // before CMD_JOIN_BLOCK. Rejecting it there — including with the dispatcher's
        // generic result="ERR" for a state violation — is permanent: FUN_0074A600(99999)
        // parks the client's chat module in a terminal state for the rest of the session.
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.PlayerSelected);
        session.Pid = 1;
        session.PlayerName = "Early";
        session.ChatEnabled = true;

        var replies = await harness.DispatchAsync(session, "CMD_WATCH_TEXTCHAT", rqid: 5);

        Assert.Empty(replies);
        Assert.NotNull(session.TextChatWatch);
        Assert.Equal(5, session.TextChatWatch!.Rqid);
    }

    [Fact]
    public async Task Send_rejections_stay_inside_the_clients_error_vocabulary()
    {
        // pes2010.exe 0x011460A0-0x011461A3 lists the only codes CMD_SEND_TEXTCHAT accepts.
        // ERR_NOTFOUNDCLIENT is a real code but belongs to other commands.
        string[] accepted =
        [
            "ERR_PLAYERISNOTGAMER", "ERR_ROOMSTATNOTMATCH", "ERR_ROOMNOTFOUND",
            "ERR_TARGETISNOTLOGIN", "ERR_TARGETINGAME", "ERR_ISNOTGM", "ERR_CHATENABLE",
        ];

        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.PlayerSelected);
        session.PlayerName = "No player";
        session.ChatEnabled = true;
        // Pid left at 0: the "not a gamer yet" path.

        var replies = await SendAsync(harness, session, "LOBBY", "BLOCK", "hello");

        Assert.Contains(replies[0].GetString("result"), accepted);
    }

    private static Session ChatSession(
        ServerHarness harness,
        int pid,
        string name,
        ServiceRole role = ServiceRole.Lobby)
    {
        var session = harness.NewSession(role, SessionState.InBlock);
        session.Pid = pid;
        session.PlayerName = name;
        session.ChatEnabled = true;
        session.BlockId = 1;
        return session;
    }

    private static async Task WatchAsync(
        ServerHarness harness, Session session, string scene, int rqid)
    {
        // Discard anything already queued (room join notices and the like) so what remains
        // is only what the watch itself produced.
        Drain(session);

        var replies = await harness.DispatchAsync(
            session, "CMD_WATCH_TEXTCHAT", rqid, request => request.Set("scene", scene));

        // The watch is parked, never answered. Its reply IS the chat delivery, sent later
        // under this same rqid; answering it here would spend the client's only delivery
        // slot on an empty message and it would not re-arm until the service changed.
        Assert.Empty(replies);
        Assert.Equal(rqid, session.TextChatWatch!.Rqid);
    }

    private static Task<IReadOnlyList<KvMessage>> SendAsync(
        ServerHarness harness,
        Session sender,
        string scene,
        string channel,
        string statement,
        KvArray? listPid = null)
        => harness.DispatchAsync(sender, "CMD_SEND_TEXTCHAT", rqid: 100, withFields: request =>
            request
                .Set("scene", scene)
                .Set("channel", channel)
                .Set("statement", statement)
                .Set("list_flg", "CHAT_BAN")
                .Set("list_pid", listPid ?? new KvArray(new object?[] { 0 })));

    private static IReadOnlyList<KvMessage> Drain(Session session)
    {
        var messages = new List<KvMessage>();
        while (session.Queue.Reader.TryRead(out var packet))
            if (packet.Message is { } message)
                messages.Add(message);
        return messages;
    }

    private static void AssertDelivery(
        KvMessage delivery,
        Session sender,
        string channel,
        string statement)
    {
        Assert.Equal("NOERR", delivery.GetString("result"));
        Assert.Equal("CMD_WATCH_TEXTCHAT", delivery.MsgName);
        Assert.Equal(sender.Pid, delivery.GetInt32("from_pid"));
        Assert.Equal(channel, delivery.GetString("channel"));
        Assert.Equal(sender.PlayerName, delivery.GetString("name"));
        Assert.Equal(statement, delivery.GetString("statement"));
    }
}
