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

        if (!frame.IsTextCommand)
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

        var inner = codecs.Blowfish.Decrypt(frame.Data);
        var payload = InnerBody.Unwrap(inner);

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

    private async Task WritePipeAsync(Session session, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await foreach (var item in session.Queue.Reader.ReadAllAsync(ct))
            {
                byte[] body;

                if (item.Message is { } message)
                {
                    var text = codecs.Writer.Write(message);
                    body = codecs.Blowfish.Encrypt(InnerBody.Wrap(text));

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
                    body = item.Raw ?? Array.Empty<byte>();
                }

                var wire = codecs.Packets.Write(item.PacketId, session.NextCounter(), body, codecs.Xor);
                await socket.SendAsync(wire, SocketFlags.None, ct);

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
