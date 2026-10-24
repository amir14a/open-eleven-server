// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Framing;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Transport;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

/// <summary>
/// PES 2011 (PC) discovery and login.
/// </summary>
/// <remarks>
/// Field names and the frame layout asserted here come from <c>pes2011.exe</c> (image base
/// 0x00400000): the server-list response parser <c>FUN_00ffd160</c>, the self-registration
/// builder <c>FUN_00512320</c>, the login state machine <c>FUN_0061c360</c> and the frame
/// reader <c>FUN_01006660</c>. See <c>docs/reverse-engineering/pes2011-session-protocol.md</c>.
/// </remarks>
public class Pes2011Tests
{
    private const string TestGameId = "pes11-tester";
    private const string TestPassword = "s3cret";

    private static Task<ServerHarness> CreateHarnessAsync(Action<Dictionary<string, string?>>? configure = null)
        => ServerHarness.CreateAsync(settings =>
        {
            settings["Server:GameProfile"] = "Pes2011Pc";
            configure?.Invoke(settings);
        });

    /// <summary>Adds an account with the same digest the web registration form would store.</summary>
    private static async Task SeedAccountAsync(ServerHarness harness, string gameId, string password)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        db.Accounts.Add(new Account
        {
            GameId = gameId,
            PasswordHash = AuthProof.HashPassword(password),
            RegCode = "SLES-12345",
        });
        await db.SaveChangesAsync();
    }

    // ---- discovery -------------------------------------------------------

    [Fact]
    public async Task Server_list_uses_the_pes2011_field_names()
    {
        await using var harness = await CreateHarnessAsync();

        var text = await harness.RenderFirstAsync(
            harness.NewSession(ServiceRole.Gate), "CMD_GET_SVRLIST");

        // Exactly the four fields FUN_00ffd160 reads per entry, and the server_list key its
        // path formatter builds (0x013ba1a4). FDLOBBY/LOBBY are absent because the parser
        // raises ERR_CLIENT_OPTFORMAT on any svrtype outside its five-name table.
        Assert.Equal(
            "result=\"NOERR\",msg=\"CMD_GET_SVRLIST\",rqid=4,server_num=4,server_list=[" +
            "{svrtype=\"GATE\",svraddr=\"127.0.0.1\",svrport=28010,userCount=0}," +
            "{svrtype=\"MENU\",svraddr=\"127.0.0.1\",svrport=28013,userCount=0}," +
            "{svrtype=\"ACCOUNT\",svraddr=\"127.0.0.1\",svrport=28014,userCount=0}," +
            "{svrtype=\"VDPCHAT\",svraddr=\"127.0.0.1\",svrport=28015,userCount=0}]," +
            // The same reply is re-parsed by FUN_005104f0 (gate state 0x11) in the PES 2010 shape.
            "svrlist=[" +
            "{svrtype=\"GATE\",svrport=28010,svraddr=\"127.0.0.1\",max_player_num=1000,player_num=0,svrgid=1}," +
            "{svrtype=\"MENU\",svrport=28013,svraddr=\"127.0.0.1\",max_player_num=1000,player_num=0,svrgid=4}," +
            "{svrtype=\"ACCOUNT\",svrport=28014,svraddr=\"127.0.0.1\",max_player_num=1000,player_num=0,svrgid=5}," +
            "{svrtype=\"VDPCHAT\",svrport=28015,svraddr=\"127.0.0.1\",max_player_num=1000,player_num=0,svrgid=6}]" + '\0',
            text);
    }

    [Fact]
    public async Task Server_list_advertises_only_svrtypes_the_client_parses()
    {
        await using var harness = await CreateHarnessAsync();

        var replies = await harness.DispatchAsync(harness.NewSession(ServiceRole.Gate), "CMD_GET_SVRLIST");

        var accepted = new[] { "MENU", "AUTOLOGIN", "ACCOUNT", "GATE", "VDPCHAT" };
        var entries = replies[0].GetList("server_list");

        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.Contains(entry.GetString("svrtype")!, accepted);

            // Each of the four is required; a missing one is what raises ERR_CLIENT_OPTFORMAT.
            foreach (var field in new[] { "svraddr", "svrport", "userCount" })
                Assert.True(entry.Has(field), $"entry is missing {field}");
        }
    }

    [Fact]
    public async Task Server_list_reports_the_pes2011_failure_shape_when_nothing_is_advertised()
    {
        await using var harness = await CreateHarnessAsync(settings =>
        {
            for (var i = 0; i < 6; i++)
                settings[$"Server:Services:{i}:Advertise"] = "false";
        });

        var text = await harness.RenderFirstAsync(harness.NewSession(ServiceRole.Gate), "CMD_GET_SVRLIST");

        // 0x013ba3a0 holds result="%s",server_num=0,server_list=[] and SvrTypeErr is the only
        // gate-layer error literal in the binary.
        Assert.Contains("result=\"SvrTypeErr\"", text);
        Assert.Contains("server_num=0", text);
        Assert.Contains("server_list=[]", text);
    }

    [Fact]
    public async Task Url_list_puts_rank_in_slot_zero_and_serves_configured_downloads()
    {
        var dlc = Path.Combine(Path.GetTempPath(), $"dlc-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(dlc, "abc");
        try
        {
            await using var harness = await CreateHarnessAsync(s =>
            {
                s["Server:Urls:0:Type"] = "DOWNLOAD";
                s["Server:Urls:0:File"] = dlc;
            });

            var list = Assert.Single(await harness.DispatchAsync(harness.NewSession(ServiceRole.Gate), "CMD_GET_URLLIST",
                    withFields: r => r.Set("lang", "SP")))
                .GetList("ulist");

            // FUN_00513de0 never stores slot 0's type and the table starts zeroed: slot 0 is RANK.
            Assert.Equal("RANK", list[0].GetString("type"));
            Assert.Contains(list, e => e.GetString("type") == "PC_SPEC");
            // DOWNLOAD names the manifest; the manifest names the package and signs it.
            var download = Assert.Single(list, e => e.GetString("type") == "DOWNLOAD");
            // The client's language rides in the urls of the texts the server writes.
            Assert.EndsWith("/dlc/manifest/SP", download.GetString("url"));
            Assert.EndsWith("/eula_pes.txt?lang=SP", list.Single(e => e.GetString("type") == "EULA_PES").GetString("url"));
            Assert.Equal("Partido rápido", ServerText.Get("room.quickmatch", "MX"));
            Assert.Equal("Быстрый матч", ServerText.Get("room.quickmatch", "RU"));
            Assert.Equal("Quick match", ServerText.Get("room.quickmatch", "ANY"));

            var manifest = DownloadFiles.Manifest(
                new UrlOptions { Type = "DOWNLOAD", File = dlc, Description = "d" }, "http://x/files/p").Split('\n');
            Assert.Equal(["1", "3"], manifest[..2]);
            Assert.Matches("^[0-9a-f]{64}$", manifest[2]);
            Assert.Equal(["http://x/files/p", "d"], manifest[3..5]);
        }
        finally
        {
            File.Delete(dlc);
        }
    }

    [Fact]
    public async Task Pes2010_discovery_keeps_its_own_payload()
    {
        // Regression guard: the shared handler must still answer for the other titles, with
        // the svrlist/svrgid keys their clients parse.
        await using var harness = await ServerHarness.CreateAsync();

        var text = await harness.RenderFirstAsync(harness.NewSession(ServiceRole.Gate), "CMD_GET_SVRLIST");

        Assert.Contains("svrlist=[", text);
        Assert.Contains("svrgid=1", text);
        Assert.DoesNotContain("server_list=", text);
    }

    // ---- handshake -------------------------------------------------------

    [Fact]
    public async Task NetClient_is_acknowledged()
    {
        await using var harness = await CreateHarnessAsync();

        var replies = await harness.DispatchAsync(harness.NewSession(), "NETCLIENT", rqid: 9);

        Assert.Single(replies);
        Assert.Equal("NOERR", replies[0].GetString("result"));
        Assert.Equal("NETCLIENT", replies[0].GetString("msg"));
        Assert.Equal(9, replies[0].Rqid);
    }

    [Fact]
    public async Task Gate_self_registration_is_acknowledged()
    {
        await using var harness = await CreateHarnessAsync();

        var replies = await harness.DispatchAsync(harness.NewSession(ServiceRole.Gate), "CMD_REG_SVRLIST",
            withFields: m => m.Set("server_num", 1));

        Assert.Equal("NOERR", replies[0].GetString("result"));
    }

    [Fact]
    public async Task Get_http_serves_the_eula_text_for_the_eula_url()
    {
        await using var harness = await CreateHarnessAsync(s => s["Server:Protocol:EulaText"] = "Play fair.");

        // The terms screen (FUN_00604880) sends the EULA_PES url as uri and shows read_buffer.
        var text = await harness.RenderFirstAsync(harness.NewSession(ServiceRole.Gate), "CMD_GET_HTTP",
            withFields: m => m.Set("uri", "http://127.0.0.1/eula_pes.txt"));

        Assert.Contains("result=\"NOERR\"", text);
        Assert.Contains("read_buffer=\"Play fair.\"", text);
    }

    // ---- login -----------------------------------------------------------

    [Fact]
    public async Task Game_id_auth_mints_a_ticket_and_login_accepts_it()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedAccountAsync(harness, TestGameId, TestPassword);

        var session = harness.NewSession(ServiceRole.Account);

        var auth = await harness.DispatchAsync(session, "CMD_AUTH_GAMEID", rqid: 11, withFields: m => m
            .Set("gameid", TestGameId)
            .Set("gameid_password", TestPassword)
            .Set("password_type", "PASS_PLAIN"));

        Assert.Equal("NOERR", auth[0].GetString("result"));
        var loginId = auth[0].GetString("loginid");
        var loginPassword = auth[0].GetString("loginid_password");
        Assert.False(string.IsNullOrEmpty(loginId));
        Assert.False(string.IsNullOrEmpty(loginPassword));
        Assert.NotEqual(loginId, loginPassword);

        // The ticket alone must not authenticate the session.
        Assert.Equal(SessionState.Connected, session.State);

        var login = await harness.DispatchAsync(session, "CMD_LOGIN", rqid: 12, withFields: m => m
            .Set("svrtype", "ACCOUNT")
            .Set("style", "LOGINID")
            .Set("loginid", loginId!)
            .Set("loginid_password", loginPassword!)
            .Set("use_regcode", "NO"));

        Assert.Equal("NOERR", login[0].GetString("result"));
        Assert.Equal(SessionState.Authenticated, session.State);
        Assert.Equal(TestGameId, session.GameId);
        Assert.True(session.AccountId > 0);

        // account_first_login is the only response field the binary reads back (0x0130c9b8).
        Assert.IsType<bool>(login[0].GetValue("account_first_login"));
    }

    [Fact]
    public async Task A_cached_ticket_can_be_replayed_on_a_later_connection()
    {
        // FUN_0061c360 state 1 replays the cached loginid/loginid_password instead of calling
        // CMD_AUTH_GAMEID again, so the ticket cannot be single-use.
        await using var harness = await CreateHarnessAsync();
        await SeedAccountAsync(harness, TestGameId, TestPassword);

        var minting = harness.NewSession(ServiceRole.Account);
        var auth = await harness.DispatchAsync(minting, "CMD_AUTH_GAMEID", withFields: m => m
            .Set("gameid", TestGameId)
            .Set("gameid_password", TestPassword)
            .Set("password_type", "PASS_PLAIN"));

        var loginId = auth[0].GetString("loginid")!;
        var loginPassword = auth[0].GetString("loginid_password")!;

        foreach (var connection in new[] { harness.NewSession(ServiceRole.Account), harness.NewSession(ServiceRole.Account) })
        {
            var login = await harness.DispatchAsync(connection, "CMD_LOGIN", withFields: m => m
                .Set("style", "LOGINID")
                .Set("loginid", loginId)
                .Set("loginid_password", loginPassword));

            Assert.Equal("NOERR", login[0].GetString("result"));
        }
    }

    [Fact]
    public async Task Login_rejects_a_ticket_the_server_never_minted()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedAccountAsync(harness, TestGameId, TestPassword);

        var session = harness.NewSession(ServiceRole.Account);
        var login = await harness.DispatchAsync(session, "CMD_LOGIN", withFields: m => m
            .Set("style", "LOGINID")
            .Set("loginid", "deadbeef")
            .Set("loginid_password", "deadbeef"));

        Assert.Equal("ERR_LOGIN_FAILED", login[0].GetString("result"));
        Assert.Equal(SessionState.Connected, session.State);
    }

    [Fact]
    public async Task Login_rejects_a_valid_login_id_with_the_wrong_secret()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedAccountAsync(harness, TestGameId, TestPassword);

        var minting = harness.NewSession(ServiceRole.Account);
        var auth = await harness.DispatchAsync(minting, "CMD_AUTH_GAMEID", withFields: m => m
            .Set("gameid", TestGameId)
            .Set("gameid_password", TestPassword)
            .Set("password_type", "PASS_PLAIN"));

        var login = await harness.DispatchAsync(harness.NewSession(ServiceRole.Account), "CMD_LOGIN",
            withFields: m => m
                .Set("style", "LOGINID")
                .Set("loginid", auth[0].GetString("loginid")!)
                .Set("loginid_password", "not-the-minted-secret"));

        Assert.Equal("ERR_LOGIN_FAILED", login[0].GetString("result"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Game_id_auth_rejects_missing_fields(string? gameId)
    {
        await using var harness = await CreateHarnessAsync();

        var auth = await harness.DispatchAsync(harness.NewSession(ServiceRole.Account), "CMD_AUTH_GAMEID",
            withFields: m => m
                .Set("gameid", gameId)
                .Set("gameid_password", TestPassword)
                .Set("password_type", "PASS_PLAIN"));

        Assert.Equal("ERR_AUTHGID_AUTH", auth[0].GetString("result"));
    }

    [Fact]
    public async Task Game_id_auth_rejects_a_wrong_password_without_minting_a_ticket()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedAccountAsync(harness, TestGameId, TestPassword);

        var auth = await harness.DispatchAsync(harness.NewSession(ServiceRole.Account), "CMD_AUTH_GAMEID",
            withFields: m => m
                .Set("gameid", TestGameId)
                .Set("gameid_password", "wrong")
                .Set("password_type", "PASS_PLAIN"));

        Assert.Equal("ERR_AUTHGID_AUTH", auth[0].GetString("result"));
        Assert.False(auth[0].Has("loginid"));
    }

    [Fact]
    public async Task Game_id_auth_refuses_aes_wrapped_passwords_rather_than_guessing()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedAccountAsync(harness, TestGameId, TestPassword);

        var auth = await harness.DispatchAsync(harness.NewSession(ServiceRole.Account), "CMD_AUTH_GAMEID",
            withFields: m => m
                .Set("gameid", TestGameId)
                .Set("gameid_password", TestPassword)
                .Set("password_type", "PASS_AES"));

        Assert.Equal("ERR_AUTHGID_AUTH", auth[0].GetString("result"));
    }

    [Fact]
    public async Task Login_checks_the_registration_code_when_the_client_asks_for_it()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedAccountAsync(harness, TestGameId, TestPassword);

        var session = harness.NewSession(ServiceRole.Account);
        var auth = await harness.DispatchAsync(session, "CMD_AUTH_GAMEID", withFields: m => m
            .Set("gameid", TestGameId)
            .Set("gameid_password", TestPassword)
            .Set("password_type", "PASS_PLAIN"));

        var rejected = await harness.DispatchAsync(session, "CMD_LOGIN", withFields: m => m
            .Set("style", "LOGINID")
            .Set("loginid", auth[0].GetString("loginid")!)
            .Set("loginid_password", auth[0].GetString("loginid_password")!)
            .Set("use_regcode", "YES")
            .Set("regcode", "SLES-00000"));

        Assert.Equal("ERR_REGCODE_FAILED", rejected[0].GetString("result"));

        var accepted = await harness.DispatchAsync(session, "CMD_LOGIN", withFields: m => m
            .Set("style", "LOGINID")
            .Set("loginid", auth[0].GetString("loginid")!)
            .Set("loginid_password", auth[0].GetString("loginid_password")!)
            .Set("use_regcode", "YES")
            .Set("regcode", "sles-12345"));

        Assert.Equal("NOERR", accepted[0].GetString("result"));
        Assert.Equal(SessionState.Authenticated, session.State);
    }

    [Fact]
    public async Task Login_rejects_an_unknown_auth_style()
    {
        await using var harness = await CreateHarnessAsync();

        var login = await harness.DispatchAsync(harness.NewSession(ServiceRole.Account), "CMD_LOGIN",
            withFields: m => m.Set("style", "NPTICKET").Set("loginid", "x").Set("loginid_password", "y"));

        Assert.Equal("ERR_LOGIN_FAILED", login[0].GetString("result"));
    }

    // ---- transport -------------------------------------------------------

    [Fact]
    public async Task The_server_list_frame_fits_the_clients_frame_reader()
    {
        await using var harness = await CreateHarnessAsync();
        var codecs = harness.Services.GetRequiredService<ProtocolCodecs>();

        var text = codecs.Writer.Write(
            (await harness.DispatchAsync(harness.NewSession(ServiceRole.Gate), "CMD_GET_SVRLIST"))[0]);

        var body = codecs.Blowfish.Encrypt(InnerBody.Wrap(text));

        // FUN_01006660 rejects any frame whose 16-bit length field exceeds 0x400 and never
        // reads the body, which would look like an unexplained disconnect.
        Assert.True(body.Length <= 1024, $"frame body is {body.Length} bytes");

        var wire = new System.Buffers.ReadOnlySequence<byte>(
            codecs.Packets.Write(PacketFrame.TextCommand, 1, body, codecs.Xor));
        Assert.True(codecs.Packets.TryRead(ref wire, codecs.Xor, out var frame));
        Assert.Equal(PacketFrame.TextCommand, frame.Id);
        Assert.Equal(body.Length, frame.Data.Length);

        // InnerBody records the unpadded length, so the NUL terminator the writer appends is
        // part of the payload and Unwrap trims it.
        Assert.Equal(text.TrimEnd('\0'), InnerBody.Unwrap(codecs.Blowfish.Decrypt(frame.Data)));
    }

    [Fact]
    public async Task The_pes2011_profile_boots_the_shared_stack()
    {
        // Cheap guard that configuring the 2011 title still resolves the shared services the
        // login flow depends on.
        await using var harness = await CreateHarnessAsync();

        Assert.Equal(GameProfile.Pes2011Pc, harness.Services.GetRequiredService<GameProfile>());
        Assert.NotNull(harness.Services.GetRequiredService<GameIdTicketStore>());
    }
}
