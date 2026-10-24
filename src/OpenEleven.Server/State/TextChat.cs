// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.State;

public enum TextChatScene
{
    Lobby,
    Room,
}

public enum TextChatChannel
{
    Block,
    Room,
    Team,
    GameQuick,
    TeamQuick,
    Competition,
    Community,
}

public enum TextChatListMode
{
    ChatBan,
    ChatSend,
}

public sealed record TextChatSubscription(int Rqid, TextChatScene Scene);
