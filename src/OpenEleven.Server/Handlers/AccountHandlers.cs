// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Challenge, authentication and profile selection. The ccode is intentionally static,
/// but the client proof, HTTP login, account and registered serial are all validated.
/// </summary>
public sealed class AccountHandlers(
    IOptionsMonitor<ServerOptions> options,
    IPlayerRepository players,
    IAccountRepository accounts,
    PendingLoginStore pendingLogins,
    OpenEleven.Data.GameDbContext db,
    ILogger<AccountHandlers> log)
{
    // Captures show the client repeats this handshake on Account, Menu and Lobby.
    private const ServiceRole AuthRoles = ServiceRole.All;

    [Command("MSG_REQCCODE", Roles = AuthRoles, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> RequestChallengeCode(CommandContext ctx)
    {
        var challengeCode = options.CurrentValue.Protocol.ChallengeCode.ToLowerInvariant();
        ctx.Session.ChallengeCode = challengeCode;
        ctx.Session.State = SessionState.Challenged;

        return Reply.Of(
            ctx.Ok(),
            ctx.Ok("MSG_CHALLENGE").Set("ccode", challengeCode));
    }

    [Command("MSG_REQAUTH", Roles = AuthRoles, RequiredState = SessionState.Challenged)]
    public async ValueTask<KvMessage[]> RequestAuth(CommandContext ctx)
    {
        // PES 2010 sends the MD5 credential as uname; PES 2011 sends the plain game id there
        // and hashes MD5(gameId || challenge || credential) (seen in a live client run).
        var uname = ctx.Request.GetString("uname");
        var is2011 = !AuthProof.TryNormalizeMd5(uname, out var credential);
        if ((is2011 && string.IsNullOrWhiteSpace(uname))
            || !AuthProof.TryNormalizeMd5(ctx.Request.GetString("hash"), out var reportedHash)
            || !AuthProof.TryNormalizeMd5(ctx.Request.GetString("para_hash"), out var paraHash)
            || !AuthProof.TryNormalizeMd5(ctx.Request.GetString("entry_hash"), out var entryHash)
            || !AuthProof.TryDecodeChallenge(ctx.Session.ChallengeCode, out _))
        {
            log.LogWarning("Authentication refused: malformed or incomplete proof fields");
            return [ctx.Err("MSG_AUTHRESULT", "AUTH")];
        }

        // para_hash and entry_hash are client fingerprints whose source digest is not
        // available to the server. Their shape is validated above and their reported
        // values are retained on the session for auditing and later policy decisions.

        var candidates = is2011
            ? pendingLogins.FindByGameId(ctx.Session.Remote.Address, uname!.Trim())
            : pendingLogins.FindCandidates(ctx.Session.Remote.Address, credential);
        if (candidates.Count == 0)
        {
            log.LogWarning(
                "Authentication refused: no pending HTTP login from {RemoteAddress}",
                ctx.Session.Remote.Address);
            return [ctx.Err("MSG_AUTHRESULT", "NOACCOUNT")];
        }

        var matches = candidates
            .Where(candidate => AuthProof.FixedTimeEquals(
                is2011
                    ? AuthProof.Compute2011(candidate.GameId, ctx.Session.ChallengeCode, candidate.Credential)
                    : AuthProof.Compute(credential, ctx.Session.ChallengeCode, candidate.GameId),
                reportedHash))
            .ToArray();

        if (matches.Length != 1)
        {
            log.LogWarning(
                "Authentication refused: challenge proof matched {Count} pending accounts",
                matches.Length);
            return [ctx.Err("MSG_AUTHRESULT", "AUTH")];
        }

        var pendingLogin = matches[0];
        credential = pendingLogin.Credential;
        var account = await accounts.GetByGameIdAsync(pendingLogin.GameId, ctx.CancellationToken);
        if (account is null || account.Id != pendingLogin.AccountId)
            return [ctx.Err("MSG_AUTHRESULT", "NOACCOUNT")];

        if (!AuthProof.TryNormalizeMd5(account.PasswordHash, out var registeredCredential)
            || !AuthProof.FixedTimeEquals(registeredCredential, credential))
            return [ctx.Err("MSG_AUTHRESULT", "AUTH")];

        if (account.Banned)
        {
            log.LogWarning("Banned account {GameId} tried to authenticate", account.GameId);
            return [ctx.Err("MSG_AUTHRESULT", "BANNED")];
        }

        // The serial is supplied when the account is registered and checked here against
        // the one the client presents. It is never learned from the client.
        // PES 2011 sends the serial without its dashes.
        var presented = ctx.Request.GetString("tmpRegcode")?.Trim().ToUpperInvariant().Replace("-", "");
        var registeredRegCode = account.RegCode?.Trim().ToUpperInvariant().Replace("-", "");
        if (string.IsNullOrEmpty(presented)
            || !string.Equals(registeredRegCode, presented, StringComparison.Ordinal))
        {
            log.LogWarning(
                "Authentication refused for {GameId}: the serial presented by the client " +
                "does not match the one registered for this account",
                account.GameId);
            return [ctx.Err("MSG_AUTHRESULT", "REGCODE")];
        }

        // A successful service handshake proves this logical login is still active.
        // Renew before opening the next Account/Menu/Lobby socket can cross the original
        // HTTP grant deadline.
        pendingLogins.Refresh(pendingLogin);

        ctx.Session.AccountId = account.Id;
        ctx.Session.GameId = account.GameId;
        ctx.Session.AuthHash = reportedHash;
        ctx.Session.ParaHash = paraHash;
        ctx.Session.EntryHash = entryHash;
        ctx.Session.RegCode = presented;

        // PES 2011 reports its patch and DLC versions; they show on the player's profile.
        if (ctx.Request.GetString("szPatchVersion") is { Length: > 0 and <= 16 } patch)
            account.PatchVersion = patch;
        if (ctx.Request.GetString("szDLContentsVersion") is { Length: > 0 and <= 16 } dlc)
            account.DlcVersion = ctx.Session.DlcVersion = dlc;
        ctx.Session.FirstLogin = await pendingLogin.ResolveFirstLoginAsync(
            token => accounts.ClaimFirstLoginAsync(account.Id, token),
            ctx.CancellationToken);
        ctx.Session.State = SessionState.Authenticated;
        await accounts.TouchLoginAsync(account.Id, ctx.CancellationToken);

        log.LogInformation("Account {GameId} authenticated with a valid serial", account.GameId);

        return
        [
            ctx.Ok(),
            ctx.Ok("MSG_AUTHRESULT").Set("first_login", ctx.Session.FirstLogin),
        ];
    }

    [Command("CMD_GET_PLAYERLIST", Roles = AuthRoles, RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> GetPlayerList(CommandContext ctx)
    {
        try
        {
            // PES2010 FUN_007C5F30/FUN_007C6940 consume this response through
            // FUN_00732D20. The parser reads player_data[0] and treats count=0 as
            // the path to profile creation.
            var owned = await LoadAccountPlayersAsync(ctx);
            var entries = owned.Select(PlayerPresenter.ListEntry).ToArray();

            return [ctx.Ok().SetList("player_count", "player_data", entries)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to load player list for account {AccountId}", ctx.Session.AccountId);
            return [ctx.Fail("ERR_DATABASE")];
        }
    }

    [Command("CMD_GET_PLAYERINFO", Roles = ServiceRole.All, RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> GetPlayerInfo(CommandContext ctx)
    {
        try
        {
            // Full profile records are parsed by PES2010 FUN_00732230. Explicit PID
            // requests remain public for lobby inspection; an omitted PID means the
            // authenticated account's own profile.
            var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
            var player = pid > 0
                ? await players.GetAsync(pid, ctx.CancellationToken)
                : (await LoadAccountPlayersAsync(ctx)).SingleOrDefault();

            if (player is null)
                return [ctx.Fail("ERR_DATABASE")];

            // pes2011.exe maps div through D5|D4|D3|D2|D1|NONE (table 0x16) and stores -1 for
            // the PES 2010 value "D3C", which the player-info screen then trips over.
            var is2011 = options.CurrentValue.GameProfile == GameProfile.Pes2011Pc;
            var division = RankSystem.Applies(options.CurrentValue.GameProfile)
                           && player.Division is not ("D5" or "D4" or "D3" or "D2" or "D1" or "NONE")
                ? "D5"
                : null;

            var teams = await players.GetRecentTeamsAsync(player.Pid, 5, ctx.CancellationToken);
            var reply = PlayerPresenter.FillPlayerInfo(ctx.Ok(), player, division, listSyntax: is2011, teams);
            if (is2011)
            {
                // Profile page 2 "Weekly Ranking TOP 10 / TOP 100": weeks finished in that range.
                // Page 4 (Legends) "Matches": Legends rooms played; its combos are total/max_combination.
                // FUN_00572590 reads all of these as optional; ML (page 3) and podium finishes
                // (page 5, list_num + compe_list[]) stay empty until those modes exist.
                var me = player.Pid;
                reply.Set("top10_num", await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                        db.WeeklyRankings, r => r.Pid == me && r.Rank <= 10, ctx.CancellationToken))
                    .Set("top100_num", await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                        db.WeeklyRankings, r => r.Pid == me && r.Rank <= 100, ctx.CancellationToken))
                    .Set("FD_match_num", await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                        db.Matches, m => m.Completed && (m.HomePid == me || m.AwayPid == me)
                                         && m.Settings.Contains("match_type=\"LEGENDS\""), ctx.CancellationToken));
            }

            return [reply];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to load player information for account {AccountId}", ctx.Session.AccountId);
            return [ctx.Fail("ERR_DATABASE")];
        }
    }

    /// <summary>
    /// Creates the account's only profile. PES2010 FUN_007AECA0 sends only the name,
    /// accepts a minimal NOERR reply, then follows with CMD_SET_LANGUAGE.
    /// </summary>
    [Command("CMD_CREATE_PLAYER", Roles = ServiceRole.Account,
        RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> CreatePlayer(CommandContext ctx)
    {
        if (ctx.Session.AccountId <= 0)
            return [ctx.Fail("ERR_DATABASE")];

        if (!PlayerNamePolicy.TryValidate(
                ctx.Request.GetString("name"), out var name, out var normalizedName))
            return [ctx.Fail("ERR_INVALIDLETTER")];

        var language = string.IsNullOrWhiteSpace(ctx.Session.Language)
            ? "EN"
            : ctx.Session.Language.Trim();
        if (language.Length > 4)
            language = language[..4];

        try
        {
            // PES 2010 ranks on D1..D3C and starts in D3C; the rank-system titles start in D5.
            var profile = options.CurrentValue.GameProfile;
            var (result, player) = await players.CreateForAccountAsync(
                ctx.Session.AccountId, name, normalizedName, language,
                options.CurrentValue.Rank.StartRating,
                RankSystem.Applies(profile) ? RankSystem.Divisions[0] : "D3C",
                ctx.CancellationToken);

            if (result != PlayerCreateResult.Created || player is null)
                return [ctx.Fail("ERR_ALREADYEXISTS")];

            log.LogInformation(
                "Created player {Name} (pid {Pid}) for account {AccountId}",
                player.Name, player.Pid, ctx.Session.AccountId);
            return [ctx.Ok()];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to create player for account {AccountId}", ctx.Session.AccountId);
            return [ctx.Fail("ERR_DATABASE")];
        }
    }

    /// <summary>
    /// Deletes the account's only profile. PES2010 FUN_007AF0B0 sends no PID because
    /// the profile is hard-linked to the authenticated account.
    /// </summary>
    [Command("CMD_DEL_PLAYER", Roles = ServiceRole.Account,
        RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> DeletePlayer(CommandContext ctx)
    {
        if (ctx.Session.AccountId <= 0)
            return [ctx.Fail("ERR_NOPLAYER")];

        try
        {
            var player = (await LoadAccountPlayersAsync(ctx)).SingleOrDefault();
            if (player is null)
                return [ctx.Fail("ERR_NOPLAYER")];

            var active = ctx.Hub.SessionsForAccount(ctx.Session.AccountId).Any(session =>
                session.BlockId is not null
                || session.RoomId is not null
                || session.State >= SessionState.InBlock);
            if (active)
            {
                log.LogWarning(
                    "Refused deletion of pid {Pid}: account {AccountId} has an active game session",
                    player.Pid, ctx.Session.AccountId);
                return [ctx.Fail("ERR_DATABASE")];
            }

            var result = await players.DeleteForAccountAsync(
                ctx.Session.AccountId, ctx.CancellationToken);
            if (result == PlayerDeleteResult.NotFound)
                return [ctx.Fail("ERR_NOPLAYER")];

            ctx.Hub.ResetPlayerIdentity(ctx.Session.AccountId, player.Pid);
            log.LogInformation(
                "Deleted player {Name} (pid {Pid}) for account {AccountId}",
                player.Name, player.Pid, ctx.Session.AccountId);
            return [ctx.Ok()];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to delete player for account {AccountId}", ctx.Session.AccountId);
            return [ctx.Fail("ERR_DATABASE")];
        }
    }

    [Command("CMD_SET_CURRENTPLAYER", Roles = AuthRoles, RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> SetCurrentPlayer(CommandContext ctx)
    {
        var requestedName = ctx.Request.GetString("name");
        var requestedPid = ctx.Request.GetInt32("pid", 0);

        var owned = await LoadAccountPlayersAsync(ctx);
        var player =
            (requestedPid > 0 ? owned.FirstOrDefault(p => p.Pid == requestedPid) : null)
            ?? (requestedName is not null ? owned.FirstOrDefault(p => p.Name == requestedName) : null)
            ?? owned.FirstOrDefault();

        if (player is null)
            return [ctx.Fail("ERR_DATABASE")];

        // A fresh profile starts at the rank system's start rating, in D5.
        if (RankSystem.Applies(options.CurrentValue.GameProfile))
        {
            RankSystem.Normalize(player, options.CurrentValue.Rank);
            await players.SaveAsync(ctx.CancellationToken);
        }

        ctx.Session.Pid = player.Pid;
        ctx.Session.PlayerName = player.Name;
        ctx.Session.DesiredPositionMask = player.DesiredPositionMask;
        ctx.Session.ChatEnabled = player.ChatEnabled;
        ctx.Session.State = SessionState.PlayerSelected;

        log.LogInformation(
            "Session {Session} selected player {Name} (pid {Pid})",
            ctx.Session.Id, player.Name, player.Pid);

        // KvMessage.Set appends, so the size is written exactly once.
        var teams = TitleProfiles.UseableTeams(options.CurrentValue.GameProfile);
        var reply = ctx.Ok().Set("useable_team_size", teams.Length);
        if (teams.Length > 0)
            reply.Set("useable_team_id", new KvArray(teams.Cast<object?>().ToArray()));

        var (mailCount, newMail) = await MailHandlers.InboxStateAsync(db, player.Pid, ctx.CancellationToken);
        return
        [
            reply
                .Set("mailcount", mailCount)
                .Set("newmail", newMail)
                .Set("chat_probi", false)
                .Set("camera_probi", false)
                .Set("voice_probi", false)
                .Set("band_probi", false)
                .Set("first_login", ctx.Session.FirstLogin),
        ];
    }

    /// <summary>
    /// Profile edits made in-game. The payload nests the fields in a brace-wrapped record
    /// (<c>profile={date=0,country=50,...}</c>) and uses snake_case names, while
    /// CMD_GET_PLAYERINFO reads them back in camelCase — the two spellings are not a typo.
    /// </summary>
    [Command("CMD_SET_PLAYERPROFILE", Roles = ServiceRole.All,
        RequiredState = SessionState.Authenticated)]
    public async ValueTask<KvMessage[]> SetPlayerProfile(CommandContext ctx)
    {
        var profile = ctx.Request.GetValue("profile") as KvMessage;
        if (profile is null)
        {
            log.LogWarning("CMD_SET_PLAYERPROFILE without a profile record: {Request}", ctx.Request);
            return [ctx.Ok()];
        }

        var player = ctx.Session.Pid > 0
            ? await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken)
            : null;

        if (player is null || player.AccountId != ctx.Session.AccountId)
        {
            log.LogWarning(
                "CMD_SET_PLAYERPROFILE refused for pid {Pid} on account {AccountId}",
                ctx.Session.Pid, ctx.Session.AccountId);
            return [ctx.Fail("ERR_DATABASE")];
        }

        if (profile.Has("birthmonth")) player.BirthMonth = profile.GetInt32("birthmonth", player.BirthMonth);
        if (profile.Has("birthday")) player.BirthDay = profile.GetInt32("birthday", player.BirthDay);
        if (profile.Has("country")) player.Country = profile.GetInt32("country", player.Country);
        if (profile.Has("area")) player.Area = profile.GetInt32("area", player.Area);
        if (profile.Has("favorite_team")) player.FavoriteTeam = profile.GetInt32("favorite_team", player.FavoriteTeam);
        if (profile.Has("favorite_player")) player.FavoritePlayer = profile.GetInt32("favorite_player", player.FavoritePlayer);
        if (profile.GetString("intro") is { } intro) player.Intro = intro;
        if (profile.GetString("lang") is { } lang) player.Lang = lang;
        if (profile.GetString("selfreport_level") is { } level) player.SelfReportLevel = level;
        if (profile.GetString("position_want") is { } position) player.PositionWant = position;
        if (ReadFlag(profile, "automatch_want") is { } automatch) player.AutoMatchWant = automatch;
        if (ReadFlag(profile, "beginnermark") is { } beginner) player.BeginnerMark = beginner;
        if (ReadFlag(profile, "enable_chat") is { } chat) player.ChatEnabled = chat;

        await players.SaveAsync(ctx.CancellationToken);
        ctx.Hub.SetPlayerChatEnabled(ctx.Session.AccountId, player.Pid, player.ChatEnabled);

        log.LogInformation(
            "Profile saved for pid {Pid}: country={Country} team={Team} intro='{Intro}'",
            player.Pid, player.Country, player.FavoriteTeam, player.Intro);

        return [ctx.Ok()];
    }

    /// <summary>Reads a YES/NO flag, returning null when the field is absent.</summary>
    private static bool? ReadFlag(KvMessage message, string key)
        => message.GetString(key) switch
        {
            null => null,
            "YES" or "1" => true,
            _ => false,
        };

    /// <summary>
    /// Players belonging to the authenticated account. An account with no profile must
    /// remain empty so the client enters its CMD_CREATE_PLAYER flow.
    /// </summary>
    private async Task<IReadOnlyList<Player>> LoadAccountPlayersAsync(CommandContext ctx)
    {
        if (ctx.Session.AccountId <= 0)
            return [];

        return await players.GetForAccountAsync(ctx.Session.AccountId, ctx.CancellationToken);
    }
}
