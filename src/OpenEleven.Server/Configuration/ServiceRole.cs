// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.Configuration;

/// <summary>
/// The logical services the client discovers through CMD_GET_SVRLIST. Each one now
/// gets its own TCP listener so traffic can be separated per stage; the client takes
/// the port from the server list, so any value works except the gate, which it must
/// know up front.
/// </summary>
[Flags]
public enum ServiceRole
{
    None = 0,
    Gate = 1 << 0,
    FdLobby = 1 << 1,
    Lobby = 1 << 2,
    Menu = 1 << 3,
    Account = 1 << 4,
    VdpChat = 1 << 5,

    All = Gate | FdLobby | Lobby | Menu | Account | VdpChat,
}

public static class ServiceRoleExtensions
{
    /// <summary>The <c>svrtype</c> token the client expects in the server list.</summary>
    public static string ToSvrType(this ServiceRole role) => role switch
    {
        ServiceRole.Gate => "GATE",
        ServiceRole.FdLobby => "FDLOBBY",
        ServiceRole.Lobby => "LOBBY",
        ServiceRole.Menu => "MENU",
        ServiceRole.Account => "ACCOUNT",
        ServiceRole.VdpChat => "VDPCHAT",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a single service role."),
    };

    /// <summary>
    /// The <c>svrtype</c> token PES 2011's server-list parser accepts, or null when the role
    /// has no PES 2011 equivalent and must not be advertised.
    /// </summary>
    /// <remarks>
    /// The parser (<c>pes2011.exe FUN_00ffd160</c>) compares each entry's <c>svrtype</c> against
    /// exactly five names, in this order, at 0x013ba23c: <c>MENU</c>, <c>AUTOLOGIN</c>,
    /// <c>ACCOUNT</c>, <c>GATE</c>, <c>VDPCHAT</c>. Anything else raises
    /// <c>ERR_CLIENT_OPTFORMAT</c> for the whole response, so <c>FDLOBBY</c> and <c>LOBBY</c>
    /// (which are PES 2010 service tiers) are omitted rather than sent and rejected.
    /// </remarks>
    public static string? ToPes2011SvrType(this ServiceRole role) => role switch
    {
        ServiceRole.Gate => "GATE",
        ServiceRole.Account => "ACCOUNT",
        ServiceRole.Menu => "MENU",
        ServiceRole.VdpChat => "VDPCHAT",
        _ => null,
    };
}
