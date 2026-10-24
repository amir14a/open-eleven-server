// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.State;

/// <summary>Builds the complete room record consumed by the PES2010 room browser.</summary>
public static class RoomPresenter
{
    private const int DesiredPositionSlots = 8;

    public static KvMessage ListEntry(Room room)
    {
        // FUN_00BB9200 reads only room_id out of each entry, into a 0x20-stride slot
        // array; FUN_00BB8F10 then counts the slots with a non-zero room_id at
        // MSG_ROOMLISTEND, which is what the browser shows. Every other field here is
        // consumed by the row renderer and unverified — field order is not load-bearing.
        var entry = new KvMessage()
            .Set("match_type", EmptyAs(room.MatchType, "OC_FREE"))
            .Set("room_id", room.Id)
            .Set("status", room.Status)
            .Set("game_phase", "ENTRY")
            .Set("name", room.Name);

        if (room.GameEnvironment is not null)
            entry.Set("gameenv", room.GameEnvironment);

        entry
            .Set("team[0]", 0)
            .Set("team[1]", 0);

        // No score[N].field group here. Dot-index keys are recorded in the RE handoff
        // (section 10.3) as causing parser failures and, in some experiments, a client
        // crash, and FUN_00BB9020 — the only confirmed parser for these entries — reads
        // nothing but room_id. Re-add them only with a capture proving the shape.

        var members = room.Snapshot();
        var gamers = members.Select(member => GamerEntry(room, member)).ToArray();
        var roomPlayers = members.Select(member => RoomPlayerEntry(room, member)).ToArray();

        // The browser resolves participant profiles through gamer[]. room_player_list[]
        // is the parallel transport/slot list and cannot populate names by itself.
        return entry
            .Set("is_nogame", false)
            .Set("is_passwd", room.Password.Length > 0)
            .Set("max_players", room.MaxMembers)
            .Set("team_category", EmptyAs(room.TeamCategory, "ALL"))
            .Set("is_invite_limit", room.InviteLimited)
            .Set("total_combination", 0)
            .Set("max_combination", 0)
            .Set("dlcontent", "1.00")
            .SetList("gamer_num", "gamer", gamers)
            .Set("enable_guest", room.AllowGuest)
            .SetList("room_player_num", "room_player_list", roomPlayers);
    }

    private static KvMessage GamerEntry(Room room, Session member)
    {
        var gamer = new KvMessage()
            .Set("pid", member.Pid)
            .Set("is_room_owner", member.Pid == room.OwnerPid)
            .Set("is_watcher", false)
            .Set("has_guestplayer", member.HasGuestPlayer)
            .Set("enter_no", member.RoomEntryNo)
            .Set("game_no", member.GameEntryNo);

        // Enum type 0x19 only accepts HOME/AWAY. A waiting occupant has no side yet,
        // and the client treats an absent optional field differently from side=-1.
        if (member.GameSide == 0)
            gamer.Set("side", "HOME");
        else if (member.GameSide == 1)
            gamer.Set("side", "AWAY");

        return gamer;
    }

    public static KvMessage MemberListEntry(Room room, Session member)
        => MemberEntry(room, member, "room_pid");

    private static KvMessage RoomPlayerEntry(Room room, Session member)
        => MemberEntry(room, member, "room_player_id");

    private static KvMessage MemberEntry(Room room, Session member, string roomPlayerIdField)
        => new KvMessage()
            .Set("ex_ip", member.ExternalIp)
            .Set("ex_port", member.ExternalPort)
            .Set("in_ip", member.InternalIp)
            .Set("in_port", member.InternalPort)
            .Set("pid", member.Pid)
            // The room's host, not this member: pid already carries the member, so a
            // duplicate would be redundant. Unverified — FUN_00BB3AD0 consumes the parsed
            // MSG_ROOMINNOTICE struct and would settle it.
            .Set(roomPlayerIdField, room.OwnerPid)
            .Set("room_entry_no", member.RoomEntryNo)
            .Set("game_entry_no", member.GameEntryNo)
            // PES2010 enum type 0x36 accepts only NO/YES. Empty strings decode as -1.
            .Set("desiredPosition", KvArray.Repeat("NO", DesiredPositionSlots));

    private static string EmptyAs(string value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;
}
