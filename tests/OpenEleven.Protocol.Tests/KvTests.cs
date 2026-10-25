// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using Xunit;

namespace OpenEleven.Protocol.Tests;

public class KvWriterTests
{
    private readonly KvWriter _writer = new();

    [Fact]
    public void Writes_strings_quoted_and_numbers_bare()
    {
        var message = KvMessage.Ok("CMD_GET_SVRTIME", 4).Set("date", 1234567890L);

        Assert.Equal(
            "result=\"NOERR\",msg=\"CMD_GET_SVRTIME\",rqid=4,date=1234567890\0",
            _writer.Write(message));
    }

    [Fact]
    public void Writes_native_result_code_without_a_reason_field()
    {
        var message = KvMessage.Fail("CMD_DEL_PLAYER", 7, "ERR_NOPLAYER");

        Assert.Equal(
            "result=\"ERR_NOPLAYER\",msg=\"CMD_DEL_PLAYER\",rqid=7\0",
            _writer.Write(message));
    }

    [Fact]
    public void Writes_bool_as_quoted_yes_no()
    {
        var message = new KvMessage().Set("first_login", true).Set("newmail", false);

        Assert.Equal("first_login=\"YES\",newmail=\"NO\"\0", _writer.Write(message));
    }

    [Fact]
    public void Escapes_backslash_and_quote()
    {
        var message = new KvMessage().Set("name", "a\"b\\c");

        Assert.Equal("name=\"a\\\"b\\\\c\"\0", _writer.Write(message));
    }

    [Fact]
    public void SetList_keeps_count_and_list_in_agreement()
    {
        var entries = new[]
        {
            new KvMessage().Set("svrtype", "GATE").Set("svrport", 28010),
            new KvMessage().Set("svrtype", "LOBBY").Set("svrport", 28012),
        };

        var text = _writer.Write(new KvMessage().SetList("server_num", "svrlist", entries));

        Assert.Equal(
            "server_num=2,svrlist=[{svrtype=\"GATE\",svrport=28010}," +
            "{svrtype=\"LOBBY\",svrport=28012}]\0",
            text);
    }

    [Fact]
    public void Indexed_field_expands_to_bracketed_keys()
    {
        var message = new KvMessage()
            .Set("desired_position", new IndexedField("YES", "NO", "YES"));

        Assert.Equal(
            "desired_position[0]=\"YES\",desired_position[1]=\"NO\",desired_position[2]=\"YES\"\0",
            _writer.Write(message));
    }

    [Fact]
    public void Scalar_array_stays_in_one_bracket_group()
    {
        var message = new KvMessage().Set("desiredPosition", KvArray.Repeat("", 4));

        Assert.Equal("desiredPosition=[\"\",\"\",\"\",\"\"]\0", _writer.Write(message));
    }

    [Fact]
    public void Empty_list_still_emits_zero_count()
    {
        var text = _writer.Write(new KvMessage().SetList("count", "list", Array.Empty<KvMessage>()));

        Assert.Equal("count=0,list=[]\0", text);
    }
}

public class KvReaderTests
{
    private readonly KvReader _reader = new();

    [Fact]
    public void Reads_quoted_and_bare_values()
    {
        var message = _reader.Parse("result=\"NOERR\",msg=\"MSG_REQAUTH\",rqid=7\0");

        Assert.Equal("NOERR", message.GetString("result"));
        Assert.Equal("MSG_REQAUTH", message.MsgName);
        Assert.Equal(7, message.Rqid);
    }

    [Fact]
    public void Reads_escaped_quote_inside_a_string()
    {
        var message = _reader.Parse("name=\"a\\\"b\"");

        Assert.Equal("a\"b", message.GetString("name"));
    }

    [Fact]
    public void Reads_record_lists()
    {
        var message = _reader.Parse(
            "count=2,list=[{pid=1,name=\"one\"},{pid=2,name=\"two\"}]");

        var list = message.GetList("list");
        Assert.Equal(2, message.GetInt32("count"));
        Assert.Equal(2, list.Count);
        Assert.Equal("two", list[1].GetString("name"));
    }

    [Fact]
    public void Reads_scalar_lists()
    {
        var message = _reader.Parse("desiredPosition=[\"\",\"\",\"CF\"]");

        var array = Assert.IsType<KvArray>(message.GetValue("desiredPosition"));
        Assert.Equal(3, array.Values.Count);
        Assert.Equal("CF", array.Values[2]);
    }

    [Fact]
    public void Round_trips_through_the_writer()
    {
        const string original =
            "result=\"NOERR\",msg=\"CMD_GET_SVRLIST\",rqid=3,server_num=1," +
            "svrlist=[{svrtype=\"GATE\",svrname=\"Gate\",svrport=28010," +
            "svraddr=\"192.168.1.10\",max_player_num=1000,player_num=0,svrgid=1}]";

        var reparsed = new KvWriter().Write(_reader.Parse(original));

        Assert.Equal(original + "\0", reparsed);
    }

    [Fact]
    public void Reads_a_brace_wrapped_record_used_as_a_value()
    {
        var message = _reader.Parse("rqid=15,profile={date=0,country=50},svrtype=\"MENU\"");

        var profile = Assert.IsType<KvMessage>(message.GetValue("profile"));
        Assert.Equal(15, message.Rqid);
        Assert.Equal(50, profile.GetInt32("country"));
        Assert.Equal("MENU", message.GetString("svrtype"));
    }

    [Fact]
    public void Reads_the_captured_set_playerprofile_payload()
    {
        // Verbatim from a live client; the nested record used to stop the parser at the
        // first comma inside the braces, so the command was never answered.
        const string captured =
            "rqid=15,profile={date=0,birthmonth=9,birthday=12,country=50,area=0," +
            "favorite_team=5,favorite_player=4618,intro=\"PLAYERINFO FIELD TESTadsdasda\"}," +
            "svrtype=\"MENU\",client=\"NETCLIENT\",msg=\"CMD_SET_PLAYERPROFILE\"";

        var message = _reader.Parse(captured);
        var profile = Assert.IsType<KvMessage>(message.GetValue("profile"));

        Assert.Equal("CMD_SET_PLAYERPROFILE", message.MsgName);
        Assert.Equal(9, profile.GetInt32("birthmonth"));
        Assert.Equal(4618, profile.GetInt32("favorite_player"));
        Assert.Equal("PLAYERINFO FIELD TESTadsdasda", profile.GetString("intro"));

        // The writer must be able to put the same shape back on the wire.
        Assert.Equal(captured + "\0", new KvWriter().Write(message));
    }

    [Fact]
    public void TryParse_returns_null_on_garbage()
    {
        Assert.Null(_reader.TryParse("this is not a payload"));
    }

    [Fact]
    public void Deep_nesting_is_refused_instead_of_overflowing_the_stack()
    {
        Assert.Null(_reader.TryParse(string.Concat(Enumerable.Repeat("a={", 20_000))));
        Assert.Null(_reader.TryParse(string.Concat(Enumerable.Repeat("a=[{", 20_000))));
    }
}
