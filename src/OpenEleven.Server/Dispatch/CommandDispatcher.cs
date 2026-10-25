// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Dispatch;

public sealed class CommandDispatcher(
    CommandRegistry registry,
    IServiceScopeFactory scopeFactory,
    Hub hub,
    UnknownCommandLog unknownCommands,
    IOptionsMonitor<ServerOptions> options,
    GameProfile profile,
    ILogger<CommandDispatcher> log)
{
    public async ValueTask DispatchAsync(Session session, KvMessage request, CancellationToken ct)
    {
        var msg = request.MsgName;
        if (string.IsNullOrEmpty(msg))
        {
            log.LogWarning("Text packet without a msg field from {Session}", session);
            return;
        }

        var protocol = options.CurrentValue.Protocol;

        if (!registry.TryGet(msg, out var entry))
        {
            unknownCommands.Record(msg, session.Role, session.State, request.ToString());

            if (options.CurrentValue.Debug.LogUnknownCommands)
                log.LogWarning(
                    "Unhandled command {Msg} on {Role} in state {State}: {Request}",
                    msg, session.Role, session.State, request);

            if (protocol.AckUnknownCommands)
                session.Push(KvMessage.Ok(msg, request.Rqid));

            return;
        }

        if ((entry.Roles & session.Role) == 0)
        {
            log.LogWarning(
                "Command {Msg} arrived on {Role} but is declared for {Allowed}",
                msg, session.Role, entry.Roles);

            if (protocol.EnforceServiceRoles)
            {
                session.Push(KvMessage.Err(msg, request.Rqid, "SVRTYPE"));
                return;
            }
        }

        // ponytail: a rejected command is only answered with SEQUENCE; the connection stays open and
        // nothing limits how fast or how many connections a client opens. Upgrade: close a session
        // after a few rejections, rate-limit commands per session, cap connections per IP in GameListener.
        if (protocol.EnforceSessionState && session.State < entry.RequiredState)
        {
            log.LogWarning(
                "Command {Msg} rejected: session state {Actual} is below required {Required}",
                msg, session.State, entry.RequiredState);
            session.Push(KvMessage.Err(msg, request.Rqid, "SEQUENCE"));
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = new CommandContext(session, hub, request, msg, profile, scope.ServiceProvider, ct);
        var handler = scope.ServiceProvider.GetRequiredService(entry.HandlerType);

        KvMessage[] replies;
        try
        {
            replies = await entry.Invoke(handler, context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Handler for {Msg} failed on {Session}", msg, session);
            session.Push(KvMessage.Err(msg, request.Rqid, "SERVER"));
            return;
        }

        foreach (var reply in replies)
        {
            var closeAfter = string.Equals(reply.MsgName, "CMD_DISCONNECT", StringComparison.Ordinal);
            if (!session.Push(reply, closeAfter))
                log.LogWarning("Outbound queue full; dropped {Msg} for {Session}", reply.MsgName, session);
        }
    }
}
