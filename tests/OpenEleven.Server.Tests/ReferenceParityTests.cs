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
    public async Task Url_list_matches_the_reference_except_for_the_self_hosted_konami_id_and_rank()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var actual = await harness.RenderFirstAsync(session, "CMD_GET_URLLIST");

        // pes2010.exe (FUN_0072c6e0) reads ulist[0] as type RANK whatever it says, so the
        // reference's PC_SPEC first made the ranking screen post to /pcspec.bin.
        const string rank = "{type=\"RANK\",url=\"http://127.0.0.1/ranking\",version=1,file_size=0,md5=0}";
        const string rankWeek = "{type=\"RANK_WEEK\",url=\"http://127.0.0.1/ranking\",version=1,file_size=0,md5=0}";
        var expected = Reference["CMD_GET_URLLIST"]
            .Replace(KonamiIdReferenceUrl, "http://127.0.0.1/register")
            .Replace("url_num=4,ulist=[", $"url_num=6,ulist=[{rank},")
            .Replace("md5=0}]", "md5=0}," + rankWeek + "]");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Url_list_uses_https_the_host_name_and_its_port_when_https_is_on()
    {
        // The client turns TLS on for every port but 80, so each title can take its own port.
        await using var harness = await ServerHarness.CreateAsync(s =>
        {
            s["Server:Https:Enabled"] = "true";
            s["Server:AdvertiseIp"] = "oe.test";
            s["Server:Listen:Https"] = "8444";
        });
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_GET_URLLIST");
        var urls = replies[0].GetList("ulist").Select(e => e.GetString("url")).ToArray();

        Assert.Contains("https://oe.test:8444/pcspec.bin", urls);
        Assert.Contains("https://oe.test:8444/gameid_auth", urls);
        Assert.Contains("https://oe.test:8444/register", urls);
        Assert.DoesNotContain(KonamiIdReferenceUrl, urls);
    }

    [Theory]
    [InlineData("192.168.100.7", 443)] // an IP cannot match a public certificate
    [InlineData("", 443)]
    [InlineData("auto", 443)]         // resolves to an IP
    [InlineData("oe.test", 80)]        // port 80 means plain HTTP to the client
    public void With_https_the_address_must_be_a_name_and_the_port_not_80(string host, int port)
    {
        var options = new ServerOptions { AdvertiseIp = host, Listen = { Https = port } };
        // Plain HTTP (local development) takes an IP, but only on port 80.
        Assert.True(new ServerOptionsValidator().Validate(null, new ServerOptions
        {
            AdvertiseIp = "192.168.100.7", Https = { Enabled = false },
        }).Succeeded);
        Assert.True(new ServerOptionsValidator().Validate(null, new ServerOptions
        {
            AdvertiseIp = "192.168.100.7", Https = { Enabled = false }, Listen = { Http = 8080 },
        }).Failed);

        var result = new ServerOptionsValidator().Validate(null, options);

        Assert.Contains(result.Failures ?? [], f => f.Contains("AdvertiseIp") || f.Contains("Listen.Https"));
    }

    [Fact]
    public void Pem_certificate_and_key_load_with_a_usable_private_key()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=oe.test", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var dir = Directory.CreateTempSubdirectory("oe-pem-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "fullchain.pem"), issued.ExportCertificatePem());
            File.WriteAllText(Path.Combine(dir, "privkey.pem"), key.ExportPkcs8PrivateKeyPem());

            using var loaded = OpenElevenServerHost.LoadCertificate(new HttpsOptions
            {
                CertificatePath = Path.Combine(dir, "fullchain.pem"),
                KeyPath = Path.Combine(dir, "privkey.pem"),
            });

            Assert.True(loaded.HasPrivateKey);
            Assert.Equal("CN=oe.test", loaded.Subject);
            Assert.Throws<InvalidOperationException>(() => OpenElevenServerHost.LoadCertificate(new HttpsOptions
            {
                CertificatePath = Path.Combine(dir, "missing.pem"),
                KeyPath = Path.Combine(dir, "privkey.pem"),
            }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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
    public async Task Current_player_result_matches_the_reference_except_for_the_team_list()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        session.FirstLogin = true;

        var actual = await harness.RenderFirstAsync(session, "CMD_SET_CURRENTPLAYER");

        // The reference sent no teams, which leaves PES 2010 without a default team: the match
        // is abandoned right after Start Match (see TitleProfiles.UseableTeams).
        var teams = TitleProfiles.UseableTeams(GameProfile.Pes2010Pc);
        var expected = Reference["CMD_SET_CURRENTPLAYER"].Replace(
            "useable_team_size=0",
            $"useable_team_size={teams.Length},useable_team_id=[{string.Join(',', teams)}]");

        Assert.Equal(expected, actual);
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
