// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using System.Text;

namespace OpenEleven.Protocol.Kv;

/// <summary>
/// Renders a <see cref="KvMessage"/> as an indented, key-aligned block for logs.
/// </summary>
/// <remarks>
/// A room list on one line is roughly 1200 characters of nested brackets, which is
/// unreadable in a console and hides exactly the structural mistakes this protocol
/// punishes — a bracketed list where the client wants indexed keys, a record nested one
/// level too deep. This formats from the parsed object model rather than the wire text,
/// so what it shows is what the writer will emit.
/// <para>
/// It is a viewing aid, not a serialiser: <see cref="KvWriter"/> remains the only thing
/// that produces bytes for the client.
/// </para>
/// </remarks>
public static class KvPrettyPrinter
{
    private const string Indent = "  ";

    /// <param name="palette">
    /// Colours for a terminal. Omit, or pass <see cref="AnsiPalette.None"/>, for plain
    /// text — the layout is identical either way.
    /// </param>
    public static string Format(KvMessage message, AnsiPalette? palette = null)
    {
        var sb = new StringBuilder();
        WriteFields(sb, message, depth: 1, palette ?? AnsiPalette.None);
        return sb.ToString().TrimEnd('\n');
    }

    private static void WriteFields(StringBuilder sb, KvMessage message, int depth, AnsiPalette p)
    {
        var fields = message.Fields.ToArray();
        if (fields.Length == 0)
            return;

        // Align the '=' within this block only. Aligning across nesting levels would
        // push deep values off the right of a terminal.
        var width = fields.Max(f => f.Key.Length);
        var pad = string.Concat(Enumerable.Repeat(Indent, depth));

        foreach (var (key, value) in fields)
        {
            // Pad before colouring: escape codes have no width but would be counted.
            sb.Append(pad).Append(p.Paint(p.Key, key.PadRight(width))).Append(" = ");
            WriteValue(sb, value, depth, p);
            sb.Append('\n');
        }
    }

    private static void WriteValue(StringBuilder sb, object? value, int depth, AnsiPalette p)
    {
        var pad = string.Concat(Enumerable.Repeat(Indent, depth));

        switch (value)
        {
            case KvMessage record:
                sb.Append(p.Paint(p.Structure, "{")).Append('\n');
                WriteFields(sb, record, depth + 1, p);
                sb.Append(pad).Append(p.Paint(p.Structure, "}"));
                break;

            case IReadOnlyList<KvMessage> list:
                WriteRecordList(sb, list, depth, pad, p, i => $"[{i}]");
                break;

            // Indexed scalars (teamLog[0]=108, desired_position[3]="NO") stay on one
            // line: expanded they are a dozen near-identical rows that bury the rest.
            case IndexedField indexed when indexed.Values.All(v => v is not KvMessage):
                WriteScalarList(sb, indexed.Values, p);
                if (indexed.Values.Count > 0)
                    sb.Append(p.Paint(p.Dim, $"   ← written as key[0..{indexed.Values.Count - 1}]"));
                break;

            case IndexedField indexedRecords:
                WriteRecordList(
                    sb,
                    indexedRecords.Values.OfType<KvMessage>().ToArray(),
                    depth, pad, p, i => $"[{i}] (indexed key)");
                break;

            case KvArray array:
                WriteScalarList(sb, array.Values, p);
                break;

            default:
                WriteScalar(sb, value, p);
                break;
        }
    }

    private static void WriteScalarList(StringBuilder sb, IReadOnlyList<object?> values, AnsiPalette p)
    {
        sb.Append(p.Paint(p.Structure, "["));
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(p.Paint(p.Structure, ", "));
            WriteScalar(sb, values[i], p);
        }
        sb.Append(p.Paint(p.Structure, "]"));
    }

    private static void WriteRecordList(
        StringBuilder sb,
        IReadOnlyList<KvMessage> list,
        int depth,
        string pad,
        AnsiPalette p,
        Func<int, string> label)
    {
        if (list.Count == 0)
        {
            sb.Append(p.Paint(p.Structure, "[]"));
            return;
        }

        sb.Append(p.Paint(p.Structure, "[")).Append('\n');
        for (var i = 0; i < list.Count; i++)
        {
            sb.Append(pad).Append(Indent)
              .Append(p.Paint(p.Structure, label(i) + " {")).Append('\n');
            WriteFields(sb, list[i], depth + 2, p);
            sb.Append(pad).Append(Indent).Append(p.Paint(p.Structure, "}")).Append('\n');
        }
        sb.Append(pad).Append(p.Paint(p.Structure, "]"));
    }

    private static void WriteScalar(StringBuilder sb, object? value, AnsiPalette p)
    {
        switch (value)
        {
            case null:
                sb.Append(p.Paint(p.Text, "\"\""));
                break;
            case string s:
                sb.Append(p.Paint(p.Text, $"\"{s}\""));
                break;
            case KvRaw raw:
                sb.Append(p.Paint(p.Number, raw.Text));
                break;
            case bool b:
                sb.Append(p.Paint(p.Flag, b ? "\"YES\"" : "\"NO\""));
                break;
            default:
                sb.Append(p.Paint(
                    p.Number, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
                break;
        }
    }
}
