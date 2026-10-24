// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Web;

public sealed record GameIdAuthResult(bool Success, string Body, string Reason);

/// <summary>Validates the HTTP credentials and opens the binary-login window.</summary>
public sealed class GameIdAuthService(
    IAccountRepository accounts,
    PendingLoginStore pendingLogins,
    ILogger<GameIdAuthService> log)
{
    public async Task<GameIdAuthResult> AuthenticateAsync(
        string gameId,
        string passwordHash,
        IPAddress remoteAddress,
        CancellationToken ct = default)
    {
        gameId = gameId.Trim();
        if (string.IsNullOrEmpty(gameId)
            || !AuthProof.TryNormalizeMd5(passwordHash.Trim(), out var credential))
            return Failure("FORMAT");

        var account = await accounts.GetByGameIdAsync(gameId, ct);
        if (account is null)
            return Failure("NOACCOUNT");

        if (account.Banned)
            return Failure("BANNED");

        if (!AuthProof.TryNormalizeMd5(account.PasswordHash, out var registeredCredential)
            || !AuthProof.FixedTimeEquals(registeredCredential, credential))
            return Failure("PASSWORD");

        pendingLogins.Add(account, credential, remoteAddress);
        log.LogInformation(
            "HTTP login accepted for {GameId} from {RemoteAddress}",
            account.GameId, remoteAddress);

        return new GameIdAuthResult(
            true,
            GameIdAuthEndpoint.BuildSuccessResponse(account.GameId, credential),
            "NOERR");
    }

    private static GameIdAuthResult Failure(string reason)
        => new(false, GameIdAuthEndpoint.BuildFailureResponse(), reason);
}
