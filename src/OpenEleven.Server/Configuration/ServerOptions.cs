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
    /// IP written into <c>svraddr</c> of the server list. "auto" resolves the primary
    /// outbound NIC at startup. A literal 127.0.0.1 only works when the game runs on
    /// this same machine, which is why it is not the default.
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
    /// The blocks the client picks from in CMD_GET_BLOCKLIST. Config rather than database
    /// rows: the list is operator policy, it changes as a unit, and the client addresses
    /// entries by their position in it. At most <see cref="MaxLobbies"/> entries.
    /// </summary>
    public List<LobbyOptions> Lobbies { get; set; } = new();
    public HttpsOptions Https { get; set; } = new();
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

    [Range(0, 65535)]
    public int Http { get; set; } = 80;

    [Range(0, 65535)]
    public int Https { get; set; } = 443;

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

public sealed class HttpsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>PFX path. When missing, 443 is skipped with a warning rather than silently downgraded.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }
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

    /// <summary>Insert the default block/information/demo-player rows on an empty database.</summary>
    public bool Seed { get; set; } = true;
}

public sealed class ProtocolOptions
{
    public const int MaxPlayerSearchLimit = 100;

    /// <summary>
    /// Reply ERR/EULA to CMD_GET_SVRLIST until the client has fetched the EULA.
    /// Off by default: forcing this on was recorded as a dead end during RE.
    /// </summary>
    public bool RequireEulaBeforeServerList { get; set; }

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
    public int HexDumpMaxBytes { get; set; } = 320;

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
