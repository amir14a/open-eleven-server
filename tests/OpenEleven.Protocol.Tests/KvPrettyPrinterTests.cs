// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol;
using OpenEleven.Protocol.Kv;
using Xunit;

namespace OpenEleven.Protocol.Tests;

public class KvPrettyPrinterTests
{
    /// <summary>A room list entry, the payload the printer exists for.</summary>
    private static KvMessage RoomList() =>
        KvMessage.Ok("MSG_ROOMLIST", 9)
            .SetList("count", "roomList", [
                new KvMessage()
                    .Set("match_type", "OC_FREE")
                    .Set("room_id", 1)
                    .Set("status", "WAITING")
                    .Set("name", "Room marqisspes5")
                    .Set("gameenv", new KvMessage()
                        .Set("cpuLevel", "NORMAL")
                        .Set("gametime", "10MINUTES")
                        .Set("ball_type", 7))
                    .Set("is_passwd", false)
                    .Set("max_players", 4)
                    .SetList("gamer_num", "gamer", [
                        new KvMessage().Set("pid", 2).Set("is_room_owner", true),
                        new KvMessage().Set("pid", 3).Set("is_room_owner", false),
                    ])
                    .Set("desiredPosition", KvArray.Repeat("NO", 4)),
            ]);

    [Fact]
    public void Nested_records_and_lists_are_indented_by_depth()
    {
        var text = KvPrettyPrinter.Format(RoomList());

        Assert.Contains("  roomList", text);
        Assert.Contains("    [0] {", text);
        Assert.Contains("      gameenv", text);
        Assert.Contains("        cpuLevel", text);
    }

    [Fact]
    public void Keys_are_aligned_within_a_block_but_not_across_levels()
    {
        var text = KvPrettyPrinter.Format(
            new KvMessage().Set("a", 1).Set("longer_key", 2));

        Assert.Contains("a          = 1", text);
        Assert.Contains("longer_key = 2", text);
    }

    [Fact]
    public void Scalars_keep_the_quoting_the_writer_would_emit()
    {
        var text = KvPrettyPrinter.Format(new KvMessage()
            .Set("name", "player2")
            .Set("enabled", true)
            .Set("disabled", false)
            .Set("count", 42));

        Assert.Contains("\"player2\"", text);
        Assert.Contains("\"YES\"", text);
        Assert.Contains("\"NO\"", text);
        Assert.Contains("= 42", text);
    }

    [Fact]
    public void Scalar_arrays_stay_on_one_line()
    {
        var text = KvPrettyPrinter.Format(
            new KvMessage().Set("desiredPosition", KvArray.Repeat("NO", 3)));

        Assert.Contains("[\"NO\", \"NO\", \"NO\"]", text);
    }

    [Fact]
    public void Indexed_scalars_are_summarised_rather_than_one_row_each()
    {
        var text = KvPrettyPrinter.Format(
            new KvMessage().Set("teamLog", new IndexedField([108, 0, 0])));

        Assert.Contains("[108, 0, 0]", text);
        Assert.Contains("key[0..2]", text);
        Assert.Single(text.Split('\n'));
    }

    [Fact]
    public void An_empty_list_does_not_open_a_block()
    {
        var text = KvPrettyPrinter.Format(
            new KvMessage().SetList("count", "roomList", []));

        Assert.Contains("roomList = []", text);
    }

    [Fact]
    public void The_none_palette_is_identical_to_passing_nothing()
    {
        Assert.Equal(
            KvPrettyPrinter.Format(RoomList()),
            KvPrettyPrinter.Format(RoomList(), AnsiPalette.None));
    }

    [Fact]
    public void Colour_adds_escape_codes_without_changing_the_visible_text()
    {
        var plain = KvPrettyPrinter.Format(RoomList());
        var coloured = KvPrettyPrinter.Format(RoomList(), AnsiPalette.Default);

        Assert.Contains('\e', coloured);
        Assert.DoesNotContain('\e', plain);

        // Strip the codes back out: what is left must be the uncoloured rendering.
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            coloured, @"\e\[[0-9;]*m", "");
        Assert.Equal(plain, stripped);
    }

    /// <summary>
    /// Escape codes have no display width. Padding a key after colouring it would count
    /// them, and every aligned column would drift by the length of a colour code.
    /// </summary>
    [Fact]
    public void Alignment_survives_colouring()
    {
        var coloured = KvPrettyPrinter.Format(
            new KvMessage().Set("a", 1).Set("longer_key", 2), AnsiPalette.Default);

        var stripped = System.Text.RegularExpressions.Regex.Replace(
            coloured, @"\e\[[0-9;]*m", "");

        Assert.Contains("a          = 1", stripped);
        Assert.Contains("longer_key = 2", stripped);
    }

    /// <summary>
    /// The printer must never be mistaken for the serialiser: it exists to be read, and
    /// its output is not valid wire format.
    /// </summary>
    [Fact]
    public void Output_is_not_wire_format()
    {
        var pretty = KvPrettyPrinter.Format(RoomList());
        var wire = new KvWriter().Write(RoomList());

        Assert.NotEqual(wire, pretty);
        Assert.Contains('\n', pretty);
        Assert.DoesNotContain('\n', wire);
    }
}
