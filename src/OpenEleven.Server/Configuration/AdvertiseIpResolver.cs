// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using System.Net.Sockets;

namespace OpenEleven.Server.Configuration;

/// <summary>
/// Resolves <c>AdvertiseIp: auto</c> once at startup. Whatever this returns is what the
/// client will connect to for every non-gate service, so getting it wrong sends the
/// client somewhere unreachable.
/// </summary>
public static class AdvertiseIpResolver
{
    public static string Resolve(string configured)
    {
        if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            return configured;

        return DetectPrimaryAddress() ?? "127.0.0.1";
    }

    private static string? DetectPrimaryAddress()
    {
        try
        {
            // No traffic is sent; connecting a UDP socket just picks the route the OS
            // would use, which is the address a LAN client can reach us on.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 65530));
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString();
        }
    }
}
