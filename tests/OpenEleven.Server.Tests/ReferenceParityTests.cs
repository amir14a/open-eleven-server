// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text.Json;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

/// <summary>
/// Compares handler output against the exact strings the reference Python server sent.
/// Commands whose output deliberately changed (server list ports, room ids) are covered
/// by <see cref="BehaviourTests"/> instead.
/// </summary>
public class ReferenceParityTests
{
    private static readonly Dictionary<string, string> Reference = LoadReferenceResponses();

    [Fact]
    public async Task Server_version_matches_the_reference_byte_for_byte()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var actual = await harness.RenderFirstAsync(session, "CMD_GET_SVRVERSION");

        Assert.Equal(Reference["CMD_GET_SVRVERSION"], actual);
    }

    [Fact]
    public async Task Eula_matches_the_reference_byte_for_byte()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var actual = await harness.RenderFirstAsync(session, "CMD_GET_EULA");

        Assert.Equal(Reference["CMD_GET_EULA"], actual);
    }

    /// <summary>
    /// KONAMIID deliberately diverges from the reference: it points at this server's own
    /// registration page instead of id.konami.net, which no longer exists. Everything else
    /// in the URL list still has to match byte for byte.
    /// </summary>
    private const string KonamiIdReferenceUrl = "https://id.konami.net/";

    [Fact]
    public async Task Url_list_matches_the_reference_except_for_the_self_hosted_konami_id()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var actual = await harness.RenderFirstAsync(session, "CMD_GET_URLLIST");

        var expected = Reference["CMD_GET_URLLIST"]
            .Replace(KonamiIdReferenceUrl, "http://127.0.0.1/register");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Url_list_carries_the_http_port_when_it_is_not_the_default()
    {
        // The client fetches these URLs immediately and stalls when they 404, so a
        // relocated HTTP port has to reach the payload — including the registration page,
        // which is now served by this server rather than by Konami.
        await using var harness = await ServerHarness.CreateAsync(s =>
            s["Server:Listen:Http"] = "8080");
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_GET_URLLIST");
        var urls = replies[0].GetList("ulist").Select(e => e.GetString("url")).ToArray();

        Assert.Contains("http://127.0.0.1:8080/pcspec.bin", urls);
        Assert.Contains("http://127.0.0.1:8080/gameid_auth", urls);
        Assert.Contains("http://127.0.0.1:8080/register", urls);
        Assert.DoesNotContain(KonamiIdReferenceUrl, urls);
    }

    [Fact]
    public async Task Private_info_matches_the_reference_byte_for_byte()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);

        var actual = await harness.RenderFirstAsync(session, "CMD_GET_PRIVATEINFO");

        Assert.Equal(Reference["CMD_GET_PRIVATEINFO"], actual);
    }

    [Fact]
    public async Task Current_player_result_matches_the_reference_byte_for_byte()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        session.FirstLogin = true;

        var actual = await harness.RenderFirstAsync(session, "CMD_SET_CURRENTPLAYER");

        Assert.Equal(Reference["CMD_SET_CURRENTPLAYER"], actual);
    }

    [Fact]
    public async Task Player_list_matches_the_reference_byte_for_byte()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);

        var actual = await harness.RenderFirstAsync(session, "CMD_GET_PLAYERLIST");

        Assert.Equal(Reference["CMD_GET_PLAYERLIST"], actual);
    }

    [Fact]
    public async Task Player_info_matches_the_reference_byte_for_byte()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);

        var actual = await harness.RenderFirstAsync(session, "CMD_GET_PLAYERINFO");

        Assert.Equal(Reference["CMD_GET_PLAYERINFO"], actual);
    }

    [Fact]
    public async Task Block_list_matches_the_reference_byte_for_byte()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        var actual = await harness.RenderFirstAsync(session, "CMD_GET_BLOCKLIST");

        Assert.Equal(Reference["CMD_GET_BLOCKLIST"], actual);
    }

    [Fact]
    public async Task Unknown_commands_fall_back_to_the_reference_ack()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var actual = await harness.RenderFirstAsync(session, "MSG_SOMETHING_UNKNOWN");

        Assert.Equal(Reference["MSG_SOMETHING_UNKNOWN"], actual);
    }

    private static Dictionary<string, string> LoadReferenceResponses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "goldens.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.GetProperty("responses")
            .EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("msg").GetString()!,
                e => e.GetProperty("text").GetString()!);
    }
}
