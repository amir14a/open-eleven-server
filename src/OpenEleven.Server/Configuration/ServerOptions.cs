// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.ComponentModel.DataAnnotations;

namespace OpenEleven.Server.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>Hard ceiling on the number of blocks that may be configured.</summary>
    public const int MaxLobbies = 10;

    /// <summary>
    /// Address the client reaches this server on: written into <c>svraddr</c> of the server list
    /// and into every URL of CMD_GET_URLLIST. With HTTPS it must be a host name the certificate
    /// covers (the client validates it with no way to ignore errors); an IP is only accepted
    /// with <c>Https.Enabled: false</c>, where "auto" also resolves the primary outbound NIC.
    /// </summary>
    public string AdvertiseIp { get; set; } = "auto";

    /// <summary>
    /// The single title this process serves. Selects which profile-gated commands
    /// register at startup. One process = one title; per-title values only, never All.
    /// </summary>
    public GameProfile GameProfile { get; set; } = GameProfile.Pes2010Pc;

    public ListenOptions Listen { get; set; } = new();
    public List<ServiceEndpointOptions> Services { get; set; } = new();

    /// <summary>
    /// File the log is also written to, or empty for console only.
    /// </summary>
    /// <remarks>
    /// A relative path is resolved against the application directory, the same anchor the
    /// SQLite file uses, so the location does not depend on where the process was started
    /// from. The absolute path is printed in the startup banner.
    /// </remarks>
    public string LogFile { get; set; } = "";

    /// <summary>
    /// The blocks the client picks from in CMD_GET_BLOCKLIST. Config rather than database
    /// rows: the list is operator policy, it changes as a unit, and the client addresses
    /// entries by their position in it. At most <see cref="MaxLobbies"/> entries.
    /// </summary>
    public List<LobbyOptions> Lobbies { get; set; } = new();

    /// <summary>PES 2011 competitions (KONAMI CUP ONLINE). Operator policy, like the lobbies.</summary>
    public List<CompetitionOptions> Competitions { get; set; } = new();

    /// <summary>
    /// Extra CMD_GET_URLLIST entries (PES 2011), added after the built-in ones and replacing a
    /// built-in of the same type. A type that is listed changes what the client does (DOWNLOAD
    /// enables the DLC download menu), so list only what has something real behind it.
    /// </summary>
    public List<UrlOptions> Urls { get; set; } = new();

    /// <summary>PES 2011 rank system: rating, points, seasons and anti-farming.</summary>
    public RankOptions Rank { get; set; } = new();

    public SiteOptions Site { get; set; } = new();
    public HttpsOptions Https { get; set; } = new();
    public CaptchaOptions Captcha { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public ProtocolOptions Protocol { get; set; } = new();
    public DebugOptions Debug { get; set; } = new();

    public ServiceEndpointOptions RequireService(ServiceRole role)
        => Services.FirstOrDefault(s => s.Role == role)
           ?? throw new InvalidOperationException($"No configured endpoint for service role {role}.");
}

public sealed class ListenOptions
{
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>
    /// HTTPS port. The PES clients turn TLS on for every port except 80, so each title can take
    /// its own port here.
    /// </summary>
    [Range(1, 65535)]
    public int Https { get; set; } = 443;

    /// <summary>
    /// Plain-HTTP port, used only with <c>Https.Enabled: false</c> (local development). The
    /// clients only speak plain HTTP to port 80, so that is the only value they accept.
    /// </summary>
    [Range(1, 65535)]
    public int Http { get; set; } = 80;

    /// <summary>Idle timeout for a game connection with no traffic at all.</summary>
    public int IdleTimeoutSeconds { get; set; } = 300;

    public int Backlog { get; set; } = 128;
}

public sealed class ServiceEndpointOptions
{
    public ServiceRole Role { get; set; }

    /// <summary><c>svrname</c> in the server list.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// <c>svrgid</c>. The client keys its internal service table off this, so the
    /// numbering must stay stable even when ports move.
    /// </summary>
    public int Gid { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; }

    public int MaxPlayers { get; set; } = 1000;

    public bool Enabled { get; set; } = true;

    /// <summary>Advertise in CMD_GET_SVRLIST. The gate itself is not listed by some builds.</summary>
    public bool Advertise { get; set; } = true;
}

/// <summary>One entry of the block list the client joins through CMD_JOIN_BLOCK.</summary>
public sealed class LobbyOptions
{
    /// <summary>Shown in the client's block list.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Stable identifier used for occupancy bookkeeping. Left at 0 it is assigned from the
    /// entry's position; set it explicitly to keep ids stable when reordering the list.
    /// </summary>
    public int Id { get; set; }

    public int MaxPlayers { get; set; } = 100;

    /// <summary>
    /// Free-form category, mirroring the lobby types the PES5-family servers use
    /// (open, noStats, division names). Not yet interpreted.
    /// </summary>
    public string Type { get; set; } = "open";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// One competition. Its status and the list it shows in (schedule, now playing, past) follow
/// from the UTC times, so nothing has to be flipped by hand as it runs.
/// </summary>
public sealed class CompetitionOptions
{
    /// <summary>compe_id. Must be unique and stay stable: player entries point at it.</summary>
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Enum 0x0a name. The 2011 client lists only KONAMI_CUP_ONLINE outside the archive.</summary>
    public string Kind { get; set; } = "KONAMI_CUP_ONLINE";

    /// <summary>Enum 0x08: PLATINUM, GOLD, SILVER or BRONZE.</summary>
    public string Category { get; set; } = "GOLD";

    public DateTime EntryStart { get; set; }
    public DateTime EntryEnd { get; set; }
    public DateTime MatchStart { get; set; }
    public DateTime MatchEnd { get; set; }
    public DateTime End { get; set; }

    public int Capacity { get; set; } = 64;

    /// <summary>Enum 0x10: TIME_5, TIME_10 or TIME_15.</summary>
    public string GameTime { get; set; } = "TIME_10";

    /// <summary>dlcver sent for this competition; empty uses Protocol.DlcVersion.</summary>
    public string DlcVersion { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The rank system. Rating (Elo) measures skill and never resets fully; points measure
/// activity and feed the weekly ranking and the monthly season, whose end promotes and
/// relegates between D1..D5.
/// </summary>
public sealed class RankOptions
{
    /// <summary>Rating of a new player, and the value a season end pulls ratings toward.</summary>
    public int StartRating { get; set; } = 1000;

    /// <summary>Elo K for a player's first <see cref="SettledAfterMatches"/> matches, then <see cref="KSettled"/>.</summary>
    public int KNew { get; set; } = 40;
    public int KSettled { get; set; } = 24;
    public int SettledAfterMatches { get; set; } = 30;

    public int WinPoints { get; set; } = 3;
    public int DrawPoints { get; set; } = 1;

    /// <summary>Extra point for winning without conceding.</summary>
    public int CleanSheetBonus { get; set; } = 1;

    /// <summary>Ranked matches per UTC day against the same opponent that move rating and points.</summary>
    public int MaxRatedPerOpponentPerDay { get; set; } = 3;

    /// <summary>Only rate a match when both players report the same para_hash (same game data).</summary>
    public bool RequireSameParaHash { get; set; } = true;

    /// <summary>Season end: share of each division (by season points) promoted and relegated, in percent.</summary>
    public int PromotePercent { get; set; } = 20;
    public int RelegatePercent { get; set; } = 20;

    /// <summary>Ranked matches in a season needed to be promoted or relegated at its end.</summary>
    public int MinSeasonMatches { get; set; } = 5;

    /// <summary>Share of the distance from <see cref="StartRating"/> a rating keeps at a season end.</summary>
    public double SeasonRatingCarry { get; set; } = 0.75;

    /// <summary>Matches listed by CMD_GET_GAMERESULTS.</summary>
    public int HistoryLimit { get; set; } = 30;
}

/// <summary>One CMD_GET_URLLIST entry. Set either <see cref="File"/> or <see cref="Url"/>.</summary>
public sealed class UrlOptions
{
    /// <summary>pes2011.exe enum 0x4b. Anything else is rejected at startup.</summary>
    public static readonly string[] Types =
    [
        "RANK", "RANK_WEEK", "PC_SPEC", "DOWNLOAD", "DOWNLOAD_DESC", "KONAMIID", "KONAMIID_ALTER",
        "GAMEID_AUTH", "VIDEO", "EULA_PES", "COMMU_MEMBERS_VS", "COMMU_TEAM_VS", "HTTP_TEST", "ADFILE",
        "PATCH_DL", "BANNER", "CUP", "COMPE_DIGEST_UL", "COMPE_DIGEST_DL", "RECORD", "P2PMATCH",
    ];

    public string Type { get; set; } = "";

    /// <summary>
    /// A file on this machine, served over HTTP at /files/{file name}. Its size and MD5, which the
    /// client checks after a download, are computed from the file.
    /// </summary>
    public string File { get; set; } = "";

    /// <summary><see cref="File"/>, a relative one anchored at the application directory like the SQLite file.</summary>
    public string FullPath => Path.GetFullPath(File, AppContext.BaseDirectory);

    /// <summary>An absolute URL hosted elsewhere, sent as-is with <see cref="FileSize"/> and <see cref="Md5"/>.</summary>
    public string Url { get; set; } = "";

    public int FileSize { get; set; }
    public string Md5 { get; set; } = "0";
    public int Version { get; set; } = 1;

    /// <summary>DOWNLOAD only: the text the client shows for the update (manifest line 5); empty = built-in, translated.</summary>
    public string Description { get; set; } = "";
}

/// <summary>The public web pages.</summary>
public sealed class SiteOptions
{
    /// <summary>Shown at the top of the home page; empty shows nothing.</summary>
    public string Announcement { get; set; } = "";
}

/// <summary>
/// Captcha on the registration page (and /api/register). The CAPTCHA_PROVIDER,
/// CAPTCHA_SITE_KEY and CAPTCHA_SECRET_KEY environment variables override these when set.
/// </summary>
public sealed class CaptchaOptions
{
    public static readonly string[] Providers = ["None", "Turnstile", "HCaptcha", "ReCaptcha", "Custom"];

    /// <summary>None, Turnstile, HCaptcha, ReCaptcha (v2 checkbox) or Custom.</summary>
    public string Provider { get; set; } = "None";

    public string SiteKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    /// <summary>Required for Custom; for the named providers they override the built-in values.</summary>
    public string ScriptUrl { get; set; } = "";
    public string VerifyUrl { get; set; } = "";
    public string WidgetClass { get; set; } = "";
    public string ResponseField { get; set; } = "";
}

public sealed class HttpsOptions
{
    /// <summary>
    /// HTTPS on <c>Listen.Https</c> with the PEM certificate below. False switches to plain HTTP
    /// on <c>Listen.Http</c> for local development; never for a public server.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// PEM certificate chain for <see cref="ServerOptions.AdvertiseIp"/>, e.g. Let's Encrypt's
    /// fullchain.pem. Relative paths resolve against the application directory. Read at startup,
    /// so a renewed certificate takes effect on the next restart.
    /// </summary>
    public string CertificatePath { get; set; } = "";

    /// <summary>PEM private key for <see cref="CertificatePath"/>, e.g. privkey.pem.</summary>
    public string KeyPath { get; set; } = "";
}

public enum DatabaseProvider
{
    Sqlite,
    MySql,
}

public sealed class DatabaseOptions
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    public string ConnectionString { get; set; } = "Data Source=data/OpenEleven.db";

    /// <summary>Create the schema at startup when it does not exist.</summary>
    public bool AutoCreate { get; set; } = true;
}

public sealed class ProtocolOptions
{
    public const int MaxPlayerSearchLimit = 100;

    /// <summary>Fixed 16-byte challenge returned by MSG_CHALLENGE, encoded as 32 hex characters.</summary>
    public string ChallengeCode { get; set; } = "00112233445566778899aabbccddeeff";

    /// <summary>Seconds a successful HTTP login may authorize binary service connections.</summary>
    public int PendingLoginLifetimeSeconds { get; set; } = 300;

    /// <summary>Maximum number of profiles returned by CMD_SEARCH_PLAYER.</summary>
    public int PlayerSearchLimit { get; set; } = MaxPlayerSearchLimit;

    /// <summary>Case-insensitive whole words rejected by CMD_CHECK_STRING.</summary>
    public List<string> BlockedTerms { get; set; } = new();

    public string ServerVersion { get; set; } = "1.0";
    public string PatchVersion { get; set; } = "1.0";
    public string DlcVersion { get; set; } = "1.0";
    public string EulaVersion { get; set; } = "1.0";

    /// <summary>
    /// Text of the terms screen (PES 2011 fetches it from the EULA_PES url). Empty uses the
    /// built-in text in the player's language. A file path is read from that file, preferring a
    /// per-language copy next to it: "eula.txt" → "eula.SP.txt" for a Spanish client.
    /// Any other value is sent as-is to everyone.
    /// </summary>
    public string EulaText { get; set; } = "";

    /// <summary>
    /// Refuse to let a player join a room whose owner logged in with a different para_hash
    /// (the client's game data fingerprint), so modified data cannot meet unmodified data.
    /// </summary>
    public bool RequireSameParaHashToJoin { get; set; } = true;

    /// <summary>Reply with a bare NOERR ack to commands that have no handler yet.</summary>
    public bool AckUnknownCommands { get; set; } = true;

    /// <summary>
    /// Echo an empty packet of the same id for non-0x0060 traffic (heartbeats and the
    /// like). The reference implementation did this unconditionally.
    /// </summary>
    public bool AckBinaryPackets { get; set; } = true;

    /// <summary>Reject commands arriving before the session reaches their required state.</summary>
    public bool EnforceSessionState { get; set; } = true;

    /// <summary>
    /// Reject a command that arrives on a service port it is not declared for. Off by
    /// default: the reference server answered everything on one port, so the per-service
    /// declarations are treated as documentation and logged, not enforced, until traffic
    /// confirms which command belongs where.
    /// </summary>
    public bool EnforceServiceRoles { get; set; }

    /// <summary>
    /// Compatibility escape hatch that copies identity by remote IP. Unsafe for public
    /// servers behind NAT; captures show normal clients authenticate each service socket.
    /// </summary>
    public bool ShareIdentityByRemoteAddress { get; set; }

    /// <summary>
    /// Emit messages whose names are inferred rather than observed (room list payloads,
    /// member join notices). A malformed or unexpected message can crash the client, so
    /// these stay off until a capture confirms the name.
    /// </summary>
    public bool EmitUnconfirmedMessages { get; set; }
}

public sealed class DebugOptions
{
    /// <summary>
    /// Hex + ASCII dump of each packet's data section, on both directions and including
    /// binary packets, which carry no readable payload and are otherwise invisible.
    /// </summary>
    public bool HexDump { get; set; } = true;

    /// <summary>
    /// Cap on dumped bytes per packet. 0 removes the cap. A room list runs past a
    /// kilobyte and would otherwise bury everything around it.
    /// </summary>
    public int HexDumpMaxBytes { get; set; } = 1024;

    /// <summary>
    /// Render key=value payloads as an indented tree rather than one long line.
    /// </summary>
    public bool PrettyPrintPayloads { get; set; } = true;

    /// <summary>Colourise packet logs. See <see cref="ConsoleColorMode"/>.</summary>
    public ConsoleColorMode Colors { get; set; } = ConsoleColorMode.Auto;

    public bool LogUnknownCommands { get; set; } = true;
    public bool LogOutboundPayloads { get; set; } = true;
}

public enum ConsoleColorMode
{
    /// <summary>
    /// Colour only on a real terminal: off when output is redirected, when a debugger is
    /// attached, or when NO_COLOR is set. Visual Studio's Output window is fed by the
    /// Debug logger provider, which prints escape sequences literally, so Auto keeps it
    /// clean there.
    /// </summary>
    Auto,

    /// <summary>Always emit colour, whatever the output looks like.</summary>
    Always,

    /// <summary>Never emit colour.</summary>
    Never,
}
