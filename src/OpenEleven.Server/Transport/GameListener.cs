// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Transport;

/// <summary>
/// One TCP listener per logical service. The gate port is fixed because the client
/// hardcodes it; every other port is free, because the client learns it from the
/// <c>svrlist</c> payload the gate returns.
/// </summary>
public sealed class GameListener(
    ServiceEndpointOptions endpoint,
    Hub hub,
    ProtocolCodecs codecs,
    CommandDispatcher dispatcher,
    IOptionsMonitor<ServerOptions> options,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _log = loggerFactory.CreateLogger($"OpenEleven.Listener.{endpoint.Role}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listen = options.CurrentValue.Listen;
        var bind = new IPEndPoint(IPAddress.Parse(listen.Host), endpoint.Port);

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(bind);
        listener.Listen(listen.Backlog);

        _log.LogInformation(
            "{Role} ({Name}, gid {Gid}) listening on {Endpoint}",
            endpoint.Role, endpoint.Name, endpoint.Gid, bind);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(stoppingToken);
                client.NoDelay = true;

                var connection = new GameConnection(
                    client, endpoint.Role, hub, codecs, dispatcher, options, _log);

                _ = Task.Run(() => connection.RunAsync(stoppingToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _log.LogInformation("{Role} listener on port {Port} stopped", endpoint.Role, endpoint.Port);
        }
    }
}
