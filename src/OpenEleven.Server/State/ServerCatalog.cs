// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Options;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>
/// Builds the CMD_GET_SVRLIST payload from configuration. Ports and the advertised
/// address come from config rather than constants, which is what makes the one-port-per-
/// service split possible without touching handler code.
/// </summary>
public sealed class ServerCatalog
{
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly Hub _hub;

    public ServerCatalog(IOptionsMonitor<ServerOptions> options, Hub hub)
    {
        _options = options;
        _hub = hub;
        AdvertiseIp = AdvertiseIpResolver.Resolve(options.CurrentValue.AdvertiseIp);
    }

    /// <summary>Resolved once at startup so every response agrees on one address.</summary>
    public string AdvertiseIp { get; }

    public IReadOnlyList<ServiceEndpointOptions> Endpoints
        => _options.CurrentValue.Services.Where(s => s.Enabled).ToArray();

    public IReadOnlyList<KvMessage> BuildServerEntries()
        => _options.CurrentValue.Services
            .Where(s => s is { Enabled: true, Advertise: true })
            .OrderBy(s => s.Gid)
            .Select(s => new KvMessage()
                .Set("svrtype", s.Role.ToSvrType())
                .Set("svrname", s.Name)
                .Set("svrport", s.Port)
                .Set("svraddr", AdvertiseIp)
                .Set("max_player_num", s.MaxPlayers)
                .Set("player_num", _hub.PlayerCount(s.Role))
                .Set("svrgid", s.Gid))
            .ToArray();
}
