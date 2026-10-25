// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Transport;

namespace OpenEleven.Server.Tests;

public class PacketLogFormatterTests
{
    private static readonly DebugOptions Debug = new() { HexDump = true, Colors = ConsoleColorMode.Never };

    private static string Log(string payload) => PacketLogFormatter.Format(new KvReader().Parse(payload), payload, Debug);

    [Theory]
    [InlineData("msg=\"CMD_AUTH_GAMEID\",gameid=\"player\",gameid_password=\"hunter2secret\"", "hunter2secret")]
    [InlineData("msg=\"CMD_LOGIN\",loginid=\"7\",loginid_password=\"hunter2secret\"", "hunter2secret")]
    [InlineData("msg=\"MSG_REQAUTH\",uname=\"0123456789abcdef0123456789abcdef\"", "0123456789abcdef")]
    public void Credentials_never_reach_the_log(string payload, string secret)
        => Assert.DoesNotContain(secret, Log(payload));

    [Fact]
    public void A_plain_game_id_in_uname_stays_readable()
        => Assert.Contains("player", Log("msg=\"MSG_REQAUTH\",uname=\"player\""));
}
