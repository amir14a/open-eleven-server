// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Handlers;

namespace OpenEleven.Server.Web;

/// <summary>
/// Files from <see cref="ServerOptions.Urls"/> served from disk. A DLC package can be hundreds of
/// megabytes, so it is streamed rather than held in <see cref="WebAssets"/>, and its MD5 is
/// computed once per file version rather than on every CMD_GET_URLLIST.
/// </summary>
public static class DownloadFiles
{
    private static readonly ConcurrentDictionary<(string Path, DateTime Written, long Length), string> Md5s = new();

    public static KvMessage Entry(UrlOptions url, string href)
    {
        var info = new FileInfo(url.FullPath);
        var md5 = Md5s.GetOrAdd((info.FullName, info.LastWriteTimeUtc, info.Length), key =>
        {
            using var stream = File.OpenRead(key.Path);
            // ponytail: lowercase hex is a guess; the downloader's compare is not traced yet.
            return Convert.ToHexStringLower(MD5.HashData(stream));
        });
        // file_size is a signed 32-bit int in the client.
        return GateHandlers.UrlEntry(url.Type, href, (int)Math.Min(info.Length, int.MaxValue), url.Version, md5);
    }

    /// <summary>
    /// Body of <c>GET {DOWNLOAD url}?ver=&lt;installed&gt;&amp;ps3=0</c>, parsed by FUN_00c4ba60 as
    /// LF-separated lines: "1" (update available; anything else is refused), package size, its
    /// signature, the package URL, and a description. All five are required.
    /// </summary>
    public static string Manifest(UrlOptions dlc, string packageUrl, string? description = null)
    {
        var info = new FileInfo(dlc.FullPath);
        var signature = Signatures.GetOrAdd((info.FullName, info.LastWriteTimeUtc, info.Length), key =>
        {
            using var stream = File.OpenRead(key.Path);
            return Convert.ToHexStringLower(HMACSHA256.HashData(SignatureKey, stream));
        });
        return $"1\n{info.Length}\n{signature}\n{packageUrl}\n{description ?? dlc.Description}\n";
    }

    private static readonly ConcurrentDictionary<(string Path, DateTime Written, long Length), string> Signatures = new();

    /// <summary>
    /// FUN_00c49bb0 checks a finished download with HMAC-SHA256 (lowercase hex, "%02x") keyed by
    /// SHA-256 of these 256 bytes from pes2011.exe (0x01523378).
    /// </summary>
    private static readonly byte[] SignatureKey = SHA256.HashData(Convert.FromHexString(
        "ac780d0e8a9f891de59917f53edf3c280f580aff633dbc71eeab254c46f399f36ca601f6468b142b242b20620b5d8b1a" +
        "b5961918d3d68ac281b00ec8a4a7bb104ebd0794481cbf6c47e0cf523d5b6df2f1870bc55d9687df4695a7ea3d63fb8b" +
        "20031f681fdfd567bfa5bafc0027eff2aefbb70c913febd7d593c112f6bd9e16c0bd7ee09d54475cf90159fa2948ecd7" +
        "43a4e3d5e3cfacb8626ecb582b696fec27eeccc44213213b157a353ec3221507c6f9dcaac988622bf72d842397f30fbe" +
        "e1dc8323efa45f051f9443e2b659e97d53c6271c4f894746b7cb694ebf790da05590c44535234a54b78e366ee720eb3a" +
        "e61256359c9e7c5369e6a2295fb0c9b5"));

    /// <summary>The configured file published under this name, or null. Only configured files are reachable.</summary>
    public static string? Find(ServerOptions options, string name)
        => options.Urls.FirstOrDefault(u => u.File.Length > 0 && Path.GetFileName(u.File) == name)?.FullPath;
}
