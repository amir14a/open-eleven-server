// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text;

namespace OpenEleven.Server.Web;

/// <summary>
/// The small files the client downloads over HTTP. Kept in one place because
/// CMD_GET_URLLIST has to report the exact byte length of each of them, and the
/// reference implementation derived that length from the same table it served.
/// </summary>
public sealed class WebAssets
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/pcspec.bin"] = [0x00],
        ["/adfile.dat"] = [0x00],
        ["/eula_pes.txt"] = "OK"u8.ToArray(),
        ["/eula_konami.txt"] = "OK"u8.ToArray(),
        ["/gameid_auth"] = "OK\n"u8.ToArray(),
    };

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public bool TryGet(string path, out byte[] content) => _files.TryGetValue(path, out content!);

    public int SizeOf(string path) => _files.TryGetValue(path, out var content) ? content.Length : 0;

    /// <summary>Overrides a served file at runtime; handy while probing client behaviour.</summary>
    public void Set(string path, byte[] content) => _files[path] = content;

    public void Set(string path, string content) => Set(path, Encoding.ASCII.GetBytes(content));
}
