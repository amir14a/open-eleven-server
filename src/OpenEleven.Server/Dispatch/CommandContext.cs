// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Dispatch;

/// <summary>Everything a handler is allowed to touch, passed by value per command.</summary>
public sealed class CommandContext(
    Session session,
    Hub hub,
    KvMessage request,
    string msg,
    GameProfile profile,
    IServiceProvider services,
    CancellationToken cancellationToken)
{
    public Session Session { get; } = session;
    public Hub Hub { get; } = hub;
    public KvMessage Request { get; } = request;
    public string Msg { get; } = msg;
    public int Rqid { get; } = request.Rqid;
    public ServiceRole Role => Session.Role;

    /// <summary>The title this process serves, for handlers with a small per-title delta.</summary>
    public GameProfile Profile { get; } = profile;

    /// <summary>Scoped provider for this one command. Do not capture it past the call.</summary>
    public IServiceProvider Services { get; } = services;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public T Resolve<T>() where T : notnull
        => (T)Services.GetService(typeof(T))!
           ?? throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");

    // ---- reply shorthands -------------------------------------------------

    public KvMessage Ok() => KvMessage.Ok(Msg, Rqid);

    public KvMessage Ok(string msg) => KvMessage.Ok(msg, Rqid);

    public KvMessage Err(string reason) => KvMessage.Err(Msg, Rqid, reason);

    public KvMessage Err(string msg, string reason) => KvMessage.Err(msg, Rqid, reason);

    public KvMessage Fail(string result) => KvMessage.Fail(Msg, Rqid, result);

    public KvMessage Fail(string msg, string result) => KvMessage.Fail(msg, Rqid, result);
}

public static class Reply
{
    private static readonly KvMessage[] Empty = [];

    public static ValueTask<KvMessage[]> None() => ValueTask.FromResult(Empty);

    public static ValueTask<KvMessage[]> Of(params KvMessage[] messages)
        => ValueTask.FromResult(messages);
}
