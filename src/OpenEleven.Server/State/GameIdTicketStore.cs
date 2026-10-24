// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>What a redeemed game-id ticket proves: one account a client authenticated as.</summary>
public sealed record GameIdTicket(int AccountId, string GameId);

/// <summary>
/// The bearer tokens behind PES 2011's two-stage login.
/// </summary>
/// <remarks>
/// The client never sends its account password to <c>CMD_LOGIN</c>. It sends
/// <c>CMD_AUTH_GAMEID</c> with <c>gameid</c>/<c>gameid_password</c>, caches the
/// <c>loginid</c>/<c>loginid_password</c> pair from the reply, and echoes that pair back in
/// <c>CMD_LOGIN</c> (pes2011.exe <c>FUN_0061c360</c> state 3 stores the pair, state 7 sends it).
/// A ticket therefore has to outlive the socket that minted it, because the client replays a
/// cached pair instead of re-authenticating, and tickets are not single-use for the same
/// reason. Only a digest of the pair is kept, so a heap dump does not leak live tickets.
/// </remarks>
public sealed class GameIdTicketStore
{
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly TimeProvider _time;

    public GameIdTicketStore(IOptionsMonitor<ServerOptions> options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    private static int TokenBytes => 16;

    /// <summary>Mints the pair handed to the client by <c>CMD_AUTH_GAMEID</c>.</summary>
    public (string LoginId, string LoginPassword) Mint(int accountId, string gameId)
    {
        Sweep();

        var loginId = NewToken();
        var loginPassword = NewToken();
        var lifetime = TimeSpan.FromSeconds(
            Math.Max(1, _options.CurrentValue.Protocol.PendingLoginLifetimeSeconds));

        _tickets[loginId] = new Ticket(
            accountId,
            gameId,
            Digest(loginId, loginPassword),
            _time.GetUtcNow() + lifetime);

        return (loginId, loginPassword);
    }

    /// <summary>
    /// Resolves the pair presented to <c>CMD_LOGIN</c>. Returns null for an unknown,
    /// expired or malformed pair; the caller must not distinguish those cases to the client.
    /// </summary>
    public GameIdTicket? Redeem(string? loginId, string? loginPassword)
    {
        if (string.IsNullOrEmpty(loginId) || string.IsNullOrEmpty(loginPassword))
            return null;

        if (!_tickets.TryGetValue(loginId, out var ticket))
            return null;

        if (ticket.ExpiresAt <= _time.GetUtcNow())
        {
            _tickets.TryRemove(loginId, out _);
            return null;
        }

        var presented = Digest(loginId, loginPassword);
        if (!CryptographicOperations.FixedTimeEquals(presented, ticket.PasswordDigest))
            return null;

        return new GameIdTicket(ticket.AccountId, ticket.GameId);
    }

    private static string NewToken()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));

    private static byte[] Digest(string loginId, string loginPassword)
        => SHA256.HashData(Encoding.ASCII.GetBytes($"{loginId}:{loginPassword}"));

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, ticket) in _tickets)
            if (ticket.ExpiresAt <= now)
                _tickets.TryRemove(key, out _);
    }

    private sealed record Ticket(
        int AccountId,
        string GameId,
        byte[] PasswordDigest,
        DateTimeOffset ExpiresAt);
}
