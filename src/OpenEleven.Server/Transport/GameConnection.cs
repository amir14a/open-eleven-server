// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Protocol.Framing;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Transport;

/// <summary>
/// One accepted socket. Runs three cooperating loops: socket to pipe, pipe to dispatch,
/// and queue to socket. The separate writer loop is what allows any handler on any
/// connection to push a packet here, which room and matching flows depend on.
/// </summary>
public sealed class GameConnection(
    Socket socket,
    ServiceRole role,
    Hub hub,
    ProtocolCodecs codecs,
    CommandDispatcher dispatcher,
    IOptionsMonitor<ServerOptions> options,
    ILogger logger)
{
    private const int MinimumReadBuffer = 4096;

    private readonly InnerBodyChunks _chunks = new();

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var remote = (IPEndPoint)socket.RemoteEndPoint!;
        var session = new Session
        {
            Id = Guid.NewGuid(),
            Role = role,
            Remote = remote,
        };

        if (options.CurrentValue.Protocol.ShareIdentityByRemoteAddress)
            hub.TryAdoptIdentity(session);

        hub.Register(session);
        logger.LogInformation("{Role} connection opened from {Remote}", role, remote);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var token = connectionCts.Token;

        try
        {
            var pipe = new Pipe();
            var fill = FillPipeAsync(pipe.Writer, token);
            var read = ReadPipeAsync(pipe.Reader, session, connectionCts);
            var write = WritePipeAsync(session, connectionCts);
            var idle = WatchIdleAsync(session, connectionCts);

            await Task.WhenAll(fill, read, write, idle);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Role} connection from {Remote} failed", role, remote);
        }
        finally
        {
            session.CompleteOutbound();
            hub.Unregister(session);
            try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
            socket.Dispose();
            logger.LogInformation("{Role} connection closed from {Remote}", role, remote);
        }
    }

    private async Task FillPipeAsync(PipeWriter writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(MinimumReadBuffer);
                var received = await socket.ReceiveAsync(memory, SocketFlags.None, ct);
                if (received == 0)
                    break;

                writer.Advance(received);

                var flush = await writer.FlushAsync(ct);
                if (flush.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            logger.LogDebug("Receive ended: {Error}", ex.SocketErrorCode);
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private async Task ReadPipeAsync(PipeReader reader, Session session, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                while (codecs.Packets.TryRead(ref buffer, codecs.Xor, out var frame))
                    await HandleFrameAsync(session, frame, ct);

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ProtocolException ex)
        {
            logger.LogWarning("Protocol error on {Session}: {Message}", session, ex.Message);
        }
        finally
        {
            await reader.CompleteAsync();
            session.CompleteOutbound();
            await cts.CancelAsync();
        }
    }

    /// <summary>
    /// Drops a connection that has gone quiet. Safe only because the client sends
    /// CMD_SEND_HEARTBEAT on an idle connection; without that this would cut live sessions
    /// that simply had nothing to say.
    /// </summary>
    private async Task WatchIdleAsync(Session session, CancellationTokenSource cts)
    {
        var timeout = TimeSpan.FromSeconds(options.CurrentValue.Listen.IdleTimeoutSeconds);
        if (timeout <= TimeSpan.Zero)
            return;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                var idleFor = DateTimeOffset.UtcNow - session.LastActivity;
                if (idleFor < timeout)
                    continue;

                logger.LogInformation(
                    "Closing {Session}: idle for {Seconds:F0}s (timeout {Timeout:F0}s)",
                    session, idleFor.TotalSeconds, timeout.TotalSeconds);

                await cts.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async ValueTask HandleFrameAsync(Session session, PacketFrame frame, CancellationToken ct)
    {
        session.LastActivity = DateTimeOffset.UtcNow;
        var debug = options.CurrentValue.Debug;

        string payload;
        if (InnerBodyChunks.IsChunkPacket(frame.Id))
        {
            var done = _chunks.Feed(frame.Id, codecs.Blowfish.Decrypt(frame.Data));
            if (done is null)
                return;

            payload = done;
        }
        else if (!frame.IsTextCommand)
        {
            // Logged at Information, not Debug: at the default level these packets were
            // invisible, which made a client that sent nothing else look like a client
            // that sent nothing at all.
            logger.LogInformation(
                "IN  binary id=0x{Id:X4} count={Count} length={Length} from {Session}\n{Body}",
                frame.Id, frame.Count, frame.Data.Length, session,
                PacketLogFormatter.FormatBinary(frame.Data, debug));

            if (options.CurrentValue.Protocol.AckBinaryPackets)
                session.PushAck(frame.Id);
            return;
        }
        else
        {
            payload = InnerBody.Unwrap(codecs.Blowfish.Decrypt(frame.Data));
        }

        var request = codecs.Reader.TryParse(payload);
        if (request is null)
        {
            logger.LogWarning(
                "Unparseable text payload from {Session}:\n{Dump}",
                session, HexDump.Format(payload));

            return;
        }

        logger.LogInformation(
            "IN  {Msg} rqid={Rqid} count={Count} from {Session}\n{Payload}",
            request.MsgName, request.Rqid, frame.Count, session,
            PacketLogFormatter.Format(request, payload, debug));

        await dispatcher.DispatchAsync(session, request, ct);
    }

    /// <summary>
    /// Flags an outbound frame whose body is longer than the client's reader accepts.
    /// </summary>
    /// <remarks>
    /// pes2011.exe <c>FUN_01006660</c> errors out (state 0xff) as soon as the frame's 16-bit
    /// length field exceeds 0x400 and never reads the body, so an oversized payload looks like
    /// an immediate disconnect with nothing on the wire to explain it. The packet is still sent
    /// rather than dropped: sending it keeps the failure reproducible and observable, while
    /// dropping it would turn a protocol mistake into a silent hang.
    /// </remarks>
    private void WarnIfOversizedFrame(Session session, OutboundPacket item, int bodyLength)
    {
        // Only the PES 2011 reader has been checked; other titles are left alone.
        if (options.CurrentValue.GameProfile != GameProfile.Pes2011Pc || bodyLength <= Pes2011MaxFrameBytes)
            return;

        logger.LogError(
            "Outbound {Msg} frame body is {Length} bytes, above the {Limit}-byte limit the " +
            "client's frame reader accepts; it will drop the connection instead of reading it " +
            "({Session})",
            item.Message?.MsgName ?? $"0x{item.PacketId:X4}", bodyLength, Pes2011MaxFrameBytes, session);
    }

    private const int Pes2011MaxFrameBytes = 0x400;

    private async Task WritePipeAsync(Session session, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await foreach (var item in session.Queue.Reader.ReadAllAsync(ct))
            {
                var frames = new List<(ushort Id, byte[] Body)>();

                if (item.Message is { } message)
                {
                    var text = codecs.Writer.Write(message);
                    foreach (var (id, plain) in InnerBody.WrapChunked(text))
                        frames.Add((id, codecs.Blowfish.Encrypt(plain)));

                    var debug = options.CurrentValue.Debug;
                    if (debug.LogOutboundPayloads)
                        logger.LogInformation(
                            "OUT {Msg} rqid={Rqid} to {Session}\n{Payload}",
                            message.MsgName, message.Rqid, session,
                            PacketLogFormatter.Format(message, text.TrimEnd('\0'), debug));
                    else
                        logger.LogInformation(
                            "OUT {Msg} rqid={Rqid} to {Session}",
                            message.MsgName, message.Rqid, session);
                }
                else
                {
                    frames.Add((item.PacketId, item.Raw ?? Array.Empty<byte>()));
                }

                foreach (var (id, body) in frames)
                {
                    var wire = codecs.Packets.Write(id, session.NextCounter(), body, codecs.Xor);

                    WarnIfOversizedFrame(session, item, body.Length);

                    await socket.SendAsync(wire, SocketFlags.None, ct);
                }

                if (item.CloseAfterSend)
                {
                    logger.LogInformation("Closing {Session} after {Msg}", session, item.Message?.MsgName);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            logger.LogDebug("Send ended: {Error}", ex.SocketErrorCode);
        }
        finally
        {
            await cts.CancelAsync();
        }
    }
}
