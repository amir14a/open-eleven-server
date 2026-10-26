// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

public class AdminPortalTests
{
    [Fact]
    public async Task Kicking_an_account_closes_all_its_connections_and_only_those()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var (lobby, menu) = (harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated), harness.NewSession(ServiceRole.Menu, SessionState.Authenticated));
        var other = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);
        other.AccountId = lobby.AccountId + 1;

        Assert.Equal(2, harness.Hub.Disconnect(lobby.AccountId));

        Assert.True(lobby.Queue.Reader.Completion.IsCompleted && menu.Queue.Reader.Completion.IsCompleted);
        Assert.False(other.Queue.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void Only_the_configured_user_and_password_sign_in()
    {
        var admin = new AdminOptions { User = "admin", Password = "a-long-secret-1" };

        Assert.True(AdminAuth.Matches(admin, "admin", "a-long-secret-1"));
        Assert.False(AdminAuth.Matches(admin, "admin", "a-long-secret-2"));
        Assert.False(AdminAuth.Matches(admin, "root", "a-long-secret-1"));
        Assert.False(AdminAuth.Matches(new AdminOptions { Password = "" }, "admin", ""));   // portal off
    }

    [Fact]
    public void A_short_admin_password_stops_the_server()
    {
        var options = new ServerOptions { AdvertiseIp = "oe.test", Admin = { Password = "short" } };
        Assert.Contains(new ServerOptionsValidator().Validate(null, options).Failures ?? [], f => f.StartsWith("Admin"));
    }
}
