// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

/// <summary>
/// Boots the real dispatch stack against a throwaway SQLite file. Everything below the
/// socket is exercised: registry lookup, role and state gating, handlers, repositories
/// and the KV writer.
/// </summary>
public sealed class ServerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _databaseFile;
    private readonly int _referenceAccountId;

    private ServerHarness(ServiceProvider services, string databaseFile, int referenceAccountId)
    {
        _services = services;
        _databaseFile = databaseFile;
        _referenceAccountId = referenceAccountId;
    }

    public IServiceProvider Services => _services;

    public Hub Hub => _services.GetRequiredService<Hub>();

    public static async Task<ServerHarness> CreateAsync(
        Action<Dictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"OpenEleven-test-{Guid.NewGuid():N}.db");

        var settings = new Dictionary<string, string?>
        {
            // 127.0.0.1 matches what the reference server hardcoded, so the golden
            // comparisons below line up field for field.
            ["Server:AdvertiseIp"] = "127.0.0.1",
            ["Server:Database:Provider"] = "Sqlite",
            ["Server:Database:ConnectionString"] = $"Data Source={databaseFile}",
            ["Server:Database:AutoCreate"] = "true",
            ["Server:Database:Seed"] = "true",

            // Single block, matching what the reference server advertised.
            ["Server:Lobbies:0:Name"] = "Beginner",
            ["Server:Lobbies:0:MaxPlayers"] = "100",
            ["Server:Lobbies:0:Type"] = "open",
        };

        AddService(settings, 0, ServiceRole.Gate, "Gate", gid: 1, port: 28010);
        AddService(settings, 1, ServiceRole.FdLobby, "FdLobby", gid: 2, port: 28011);
        AddService(settings, 2, ServiceRole.Lobby, "Lobby", gid: 3, port: 28012);
        AddService(settings, 3, ServiceRole.Menu, "Menu", gid: 4, port: 28013);
        AddService(settings, 4, ServiceRole.Account, "Account", gid: 5, port: 28014);
        AddService(settings, 5, ServiceRole.VdpChat, "VdpChat", gid: 6, port: 28015);

        configure?.Invoke(settings);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOpenElevenCore(configuration);
        configureServices?.Invoke(collection);

        var provider = collection.BuildServiceProvider();

        int referenceAccountId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
            await initializer.InitializeAsync(seed: true);
            referenceAccountId = await SeedReferenceProfileAsync(scope.ServiceProvider);
        }

        return new ServerHarness(provider, databaseFile, referenceAccountId);
    }

    public Session NewSession(
        ServiceRole role = ServiceRole.Gate,
        SessionState state = SessionState.Connected)
    {
        var session = new Session
        {
            Id = Guid.NewGuid(),
            Role = role,
            Remote = new IPEndPoint(IPAddress.Loopback, 50000),
            State = state,
            AccountId = state >= SessionState.Authenticated ? _referenceAccountId : 0,
            BlockId = state >= SessionState.InBlock ? 1 : null,
        };

        Hub.Register(session);
        return session;
    }

    public async Task<GameIdAuthResult> AuthenticateHttpAsync(
        string gameId,
        string passwordHash,
        IPAddress? remoteAddress = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<GameIdAuthService>();
        return await authenticator.AuthenticateAsync(
            gameId, passwordHash, remoteAddress ?? IPAddress.Loopback);
    }

    public async Task<IReadOnlyList<KvMessage>> AuthenticateSocketAsync(
        Session session,
        string gameId,
        string credential,
        string regCode,
        string paraHash = "d6861177058a1b4cc4add08c9c829d73",
        string entryHash = "4610ec92ea8e19ef49cdb1d318094fcd")
    {
        if (string.IsNullOrEmpty(session.ChallengeCode))
            await DispatchAsync(session, "MSG_REQCCODE", rqid: 1);

        var hash = AuthProof.Compute(credential, session.ChallengeCode, gameId);
        return await DispatchAsync(session, "MSG_REQAUTH", rqid: 2, withFields: request => request
            .Set("uname", credential)
            .Set("hash", hash)
            .Set("para_hash", paraHash)
            .Set("entry_hash", entryHash)
            .Set("tmpRegcode", regCode));
    }

    /// <summary>Dispatches one command and returns every reply queued for that session.</summary>
    public async Task<IReadOnlyList<KvMessage>> DispatchAsync(
        Session session, string msg, int rqid = 4, Action<KvMessage>? withFields = null)
    {
        var request = new KvMessage().Set("msg", msg).Set("rqid", rqid);
        withFields?.Invoke(request);

        var dispatcher = _services.GetRequiredService<CommandDispatcher>();
        await dispatcher.DispatchAsync(session, request, CancellationToken.None);

        var replies = new List<KvMessage>();
        while (session.Queue.Reader.TryRead(out var item))
            if (item.Message is { } message)
                replies.Add(message);

        return replies;
    }

    /// <summary>The first reply, rendered exactly as it would go on the wire.</summary>
    public async Task<string> RenderFirstAsync(
        Session session, string msg, int rqid = 4, Action<KvMessage>? withFields = null)
    {
        var replies = await DispatchAsync(session, msg, rqid, withFields);
        Assert.NotEmpty(replies);
        return _services.GetRequiredService<KvWriter>().Write(replies[0]);
    }

    public string Render(KvMessage message)
        => _services.GetRequiredService<KvWriter>().Write(message);

    private static void AddService(
        Dictionary<string, string?> settings,
        int index, ServiceRole role, string name, int gid, int port)
    {
        settings[$"Server:Services:{index}:Role"] = role.ToString();
        settings[$"Server:Services:{index}:Name"] = name;
        settings[$"Server:Services:{index}:Gid"] = gid.ToString();
        settings[$"Server:Services:{index}:Port"] = port.ToString();
        settings[$"Server:Services:{index}:MaxPlayers"] = "1000";
        settings[$"Server:Services:{index}:Enabled"] = "true";
        settings[$"Server:Services:{index}:Advertise"] = "true";
    }

    /// <summary>
    /// Installs the reference capture profile used by parity tests. Production startup
    /// deliberately creates no account or player profile.
    /// </summary>
    private static async Task<int> SeedReferenceProfileAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<GameDbContext>();
        var account = new Account
        {
            GameId = "__reference__",
            PasswordHash = "test-only",
            FirstLogin = false,
        };
        var player = new Player
        {
            Account = account,
            Name = "Local Player",
            NormalizedName = PlayerNamePolicy.Normalize("Local Player"),
            Division = "D3C",
            Kind = "NORMAL",
            Lang = "EN",
            Intro = "PLAYERINFO FIELD TEST",
            Rating = 742,
            Point = 12345,
            Rank = 321,
            Manner = 3,
            Country = 50,
            BirthMonth = 9,
            BirthDay = 12,
            FavoriteTeam = 5,
            FavoritePlayer = 4618,
            SelfReportLevel = "PRO",
            PositionWant = "CF",
            DesiredPositionMask = DatabaseInitializer.LegacyDesiredPositionMask,
            AutoMatchWant = true,
            BeginnerMark = true,
            ChatEnabled = true,
            Stats = new PlayerStats
            {
                MatchCount = 37,
                WinCount = 21,
                LoseCount = 9,
                DrawCount = 7,
                ContinuousWins = 5,
                MaxContinuousWins = 8,
                DisconnectCount = 2,
                DisconnectWins = 1,
                DisconnectLosses = 1,
                Goals = 84,
                GoalsAgainst = 42,
                TotalCombination = 18,
                MaxCombination = 4,
            },
        };

        db.Accounts.Add(account);
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return account.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { File.Delete(_databaseFile); } catch (IOException) { }
    }
}
