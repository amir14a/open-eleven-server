// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using System.Threading.Channels;
using OpenEleven.Protocol.Framing;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>One queued outbound packet. Either a text command or a raw acknowledgement.</summary>
public readonly record struct OutboundPacket(
    ushort PacketId,
    KvMessage? Message,
    byte[]? Raw,
    bool CloseAfterSend)
{
    public static OutboundPacket Text(KvMessage message, bool close = false)
        => new(PacketFrame.TextCommand, message, null, close);

    public static OutboundPacket Ack(ushort packetId)
        => new(packetId, null, Array.Empty<byte>(), false);
}

/// <summary>
/// Per-connection state. Deliberately not a DI service: it is created and owned by the
/// connection, so its lifetime is the socket's and nothing can accidentally capture it
/// in a singleton.
/// </summary>
public sealed class Session
{
    private uint _counter;

    public required Guid Id { get; init; }
    public required ServiceRole Role { get; init; }
    public required IPEndPoint Remote { get; init; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Stamped on every inbound packet. CMD_SEND_HEARTBEAT exists so this keeps moving on
    /// an otherwise idle connection, which is what makes an idle timeout safe.
    /// </summary>
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

    public SessionState State { get; set; } = SessionState.Connected;

    public int AccountId { get; set; }
    public string GameId { get; set; } = "";
    public string ChallengeCode { get; set; } = "";
    public string AuthHash { get; set; } = "";
    public string ParaHash { get; set; } = "";
    public string EntryHash { get; set; } = "";

    /// <summary>szDLContentsVersion from MSG_REQAUTH (PES 2011); shown as a room's dlcontent.</summary>
    public string DlcVersion { get; set; } = "";
    public string RegCode { get; set; } = "";
    public bool FirstLogin { get; set; }
    public int Pid { get; set; }
    public string PlayerName { get; set; } = "";
    public string Language { get; set; } = "EN";
    public bool EulaAccepted { get; set; }
    public bool ChatEnabled { get; set; } = true;
    public TextChatSubscription? TextChatWatch { get; set; }
    public WatchListSubscription? RoomListWatch { get; set; }
    public WatchListSubscription? BlockPlayerWatch { get; set; }
    public WatchListSubscription? RoomPlayerWatch { get; set; }

    /// <summary>PES2010's friends-screen watches; changes push under the same names.</summary>
    public int? FriendListWatchRqid { get; set; }
    public int? FriendRequestWatchRqid { get; set; }

    public int? BlockId { get; set; }
    public int? RoomId { get; set; }
    public int RoomEntryNo { get; set; } = -1;
    public int GameEntryNo { get; set; } = -1;
    public int GameSide { get; set; } = -1;
    public int? GameEntryWatchRqid { get; set; }
    public int? RoomStateWatchRqid { get; set; }
    public int? IpAndPortWatchRqid { get; set; }

    /// <summary>
    /// The entry-game screen (FUN_007bacc0) arms these six in one burst alongside
    /// CMD_WATCH_ENTRY_GAME. They are how a non-host member learns what the host is
    /// configuring, so none of them can depend on the member having asked first.
    /// </summary>
    public int? DecideGameEnvWatchRqid { get; set; }
    public int? DecideGamePlayerWatchRqid { get; set; }
    public int? DecideGamePlayerEnvWatchRqid { get; set; }
    public int? DisconPlayerEnvWatchRqid { get; set; }
    public int? DisconPlayerMatchWatchRqid { get; set; }
    public int? UpdateGameRecordWatchRqid { get; set; }

    /// <summary>A second local controller sharing this connection's slot.</summary>
    public bool HasGuestPlayer { get; set; }

    public bool RoomListSubscribed { get; set; }

    /// <summary>Endpoints learned from CMD_JOIN_BLOCK; the peers need them for P2P setup.</summary>
    public string ExternalIp { get; set; } = "127.0.0.1";
    public int ExternalPort { get; set; }
    public string InternalIp { get; set; } = "127.0.0.1";
    public int InternalPort { get; set; }

    public int DesiredPositionMask { get; set; }

    /// <summary>
    /// Bounded so a stalled client cannot grow the server's heap. Dropping the write is
    /// preferable to blocking a handler that may be broadcasting to a whole room.
    /// </summary>
    public Channel<OutboundPacket> Queue { get; } = Channel.CreateBounded<OutboundPacket>(
        new BoundedChannelOptions(512)
        {
            // TryWrite remains non-blocking and reports false when full, allowing the
            // caller to log the dropped push instead of silently losing it.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    /// <summary>Only the writer loop calls this, so no interlocking is needed.</summary>
    public uint NextCounter() => ++_counter;

    public bool Push(KvMessage message, bool closeAfterSend = false)
        => Queue.Writer.TryWrite(OutboundPacket.Text(message, closeAfterSend));

    public bool PushAck(ushort packetId)
        => Queue.Writer.TryWrite(OutboundPacket.Ack(packetId));

    public void CompleteOutbound() => Queue.Writer.TryComplete();

    public override string ToString()
        => $"{Role}/{Remote} pid={Pid} state={State}";
}
