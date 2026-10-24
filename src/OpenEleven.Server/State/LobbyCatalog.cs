// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Options;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>One block, resolved from configuration with its identity settled.</summary>
public sealed record Lobby(int Id, int Index, string Name, int MaxPlayers, string Type);

/// <summary>
/// Reads the block list from configuration. Ordering matters: CMD_JOIN_BLOCK identifies a
/// block by its <c>index</c> into the list the server just advertised, so the list handed
/// out and the list joined against have to be the same sequence.
/// </summary>
public sealed class LobbyCatalog(IOptionsMonitor<ServerOptions> options)
{
    public IReadOnlyList<Lobby> Lobbies
    {
        get
        {
            // Capped defensively as well as validated at startup: a reload could introduce
            // an over-long list, and silently serving eleven blocks is worse than ten.
            var configured = options.CurrentValue.Lobbies
                .Where(l => l.Enabled)
                .Take(ServerOptions.MaxLobbies)
                .ToArray();
            var lobbies = new Lobby[configured.Length];

            for (var i = 0; i < configured.Length; i++)
            {
                var lobby = configured[i];
                lobbies[i] = new Lobby(
                    Id: lobby.Id > 0 ? lobby.Id : i + 1,
                    Index: i,
                    Name: lobby.Name,
                    MaxPlayers: lobby.MaxPlayers,
                    Type: lobby.Type);
            }

            return lobbies;
        }
    }

    public Lobby? ByIndex(int index)
    {
        var lobbies = Lobbies;
        return index >= 0 && index < lobbies.Count ? lobbies[index] : null;
    }

    public Lobby? ByName(string name)
        => Lobbies.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));

    public Lobby? ById(int id) => Lobbies.FirstOrDefault(l => l.Id == id);
}
