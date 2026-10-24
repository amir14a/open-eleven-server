// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using OpenEleven.Data.Entities;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>
/// Bridges a successful HTTP login to the several binary service connections opened by
/// the client. Entries remain reusable for their short TTL because Account, Menu and
/// Lobby authenticate independently during one logical login.
/// </summary>
public sealed class PendingLoginStore(
    IOptionsMonitor<ServerOptions> options,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Guid, PendingLogin> _entries = new();

    public PendingLogin Add(Account account, string credential, IPAddress remoteAddress)
    {
        RemoveExpired();
        var normalizedAddress = NormalizeAddress(remoteAddress);

        // A new HTTP login supersedes an older login for the same account and address.
        foreach (var existing in _entries.Values.Where(entry =>
                     entry.AccountId == account.Id && entry.RemoteAddress.Equals(normalizedAddress)))
            _entries.TryRemove(existing.Id, out _);

        var entry = new PendingLogin(
            Guid.NewGuid(),
            account.Id,
            account.GameId,
            credential,
            normalizedAddress,
            NextExpiration());

        _entries[entry.Id] = entry;
        return entry;
    }

    public IReadOnlyList<PendingLogin> FindCandidates(IPAddress remoteAddress, string credential)
    {
        RemoveExpired();
        var normalizedAddress = NormalizeAddress(remoteAddress);

        return _entries.Values
            .Where(entry => entry.RemoteAddress.Equals(normalizedAddress)
                            && string.Equals(entry.Credential, credential, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// Renews the logical login after a binary service successfully authenticates.
    /// PES opens Account, Menu and Lobby connections at different times, so the HTTP
    /// grant is an idle timeout rather than an absolute login deadline.
    /// </summary>
    public bool Refresh(PendingLogin entry)
    {
        RemoveExpired();
        if (!_entries.TryGetValue(entry.Id, out var current) || !ReferenceEquals(entry, current))
            return false;

        current.Renew(NextExpiration());
        return true;
    }

    /// <summary>
    /// Keeps the logical login alive while one of its authenticated service sockets is
    /// active. The account id and remote address are already established by MSG_REQAUTH.
    /// </summary>
    public bool RefreshForAccount(int accountId, IPAddress remoteAddress)
    {
        RemoveExpired();
        var normalizedAddress = NormalizeAddress(remoteAddress);
        var expiresAt = NextExpiration();
        var refreshed = false;

        foreach (var entry in _entries.Values.Where(entry =>
                     entry.AccountId == accountId && entry.RemoteAddress.Equals(normalizedAddress)))
        {
            entry.Renew(expiresAt);
            refreshed = true;
        }

        return refreshed;
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in _entries.Values.Where(entry => entry.ExpiresAt <= now))
            _entries.TryRemove(entry.Id, out _);
    }

    private DateTimeOffset NextExpiration()
        => timeProvider.GetUtcNow().AddSeconds(
            options.CurrentValue.Protocol.PendingLoginLifetimeSeconds);

    private static IPAddress NormalizeAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return IPAddress.Loopback;

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}

public sealed class PendingLogin(
    Guid id,
    int accountId,
    string gameId,
    string credential,
    IPAddress remoteAddress,
    DateTimeOffset expiresAt)
{
    private readonly SemaphoreSlim _firstLoginGate = new(1, 1);
    private long _expiresAtUtcTicks = expiresAt.UtcTicks;
    private bool? _firstLogin;

    public Guid Id { get; } = id;
    public int AccountId { get; } = accountId;
    public string GameId { get; } = gameId;
    public string Credential { get; } = credential;
    public IPAddress RemoteAddress { get; } = remoteAddress;
    public DateTimeOffset ExpiresAt
        => new(Interlocked.Read(ref _expiresAtUtcTicks), TimeSpan.Zero);

    public void Renew(DateTimeOffset expiresAt)
        => Interlocked.Exchange(ref _expiresAtUtcTicks, expiresAt.UtcTicks);

    /// <summary>
    /// Resolves FirstLogin once for this logical login. Every service connection then
    /// receives the same value, while the database flag is claimed only once.
    /// </summary>
    public async Task<bool> ResolveFirstLoginAsync(
        Func<CancellationToken, Task<bool>> claim,
        CancellationToken ct)
    {
        await _firstLoginGate.WaitAsync(ct);
        try
        {
            _firstLogin ??= await claim(ct);
            return _firstLogin.Value;
        }
        finally
        {
            _firstLoginGate.Release();
        }
    }
}
