// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text;
using OpenEleven.Protocol.Framing;

namespace OpenEleven.Protocol.Kv;

/// <summary>
/// Recursive-descent parser for the client's key=value grammar. Replaces the
/// per-field regular expressions of the reference implementation, so nested lists
/// and escaped quotes survive the trip.
/// </summary>
public sealed class KvReader
{
    public KvMessage Parse(string payload)
    {
        var text = payload.TrimEnd('\0');
        var position = 0;
        var message = ParseFields(text, ref position, terminator: '\0');

        if (position < text.Length)
            throw new ProtocolException(
                $"Trailing junk at offset {position} in payload: '{text[position..]}'");

        return message;
    }

    /// <summary>Never throws; returns null when the payload is not parseable.</summary>
    public KvMessage? TryParse(string payload)
    {
        try
        {
            return Parse(payload);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    private static KvMessage ParseFields(string s, ref int i, char terminator)
    {
        var message = new KvMessage();

        SkipWhitespace(s, ref i);
        if (i >= s.Length || s[i] == terminator)
            return message;

        while (true)
        {
            SkipWhitespace(s, ref i);

            var key = ParseKey(s, ref i);
            Expect(s, ref i, '=');
            var value = ParseValue(s, ref i);
            message.Set(key, value);

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ',')
            {
                i++;
                continue;
            }

            return message;
        }
    }

    private static string ParseKey(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && s[i] != '=' && s[i] != ',' && s[i] != '}' && s[i] != ']')
            i++;

        if (i == start)
            throw new ProtocolException($"Empty field name at offset {start}.");

        return s[start..i].Trim();
    }

    private static object ParseValue(string s, ref int i)
    {
        if (i >= s.Length)
            return new KvRaw(string.Empty);

        return s[i] switch
        {
            '"' => ParseQuoted(s, ref i),
            '[' => ParseList(s, ref i),
            '{' => ParseRecord(s, ref i),
            _ => ParseBare(s, ref i),
        };
    }

    /// <summary>
    /// A brace-wrapped record used directly as a value: <c>profile={date=0,country=50}</c>.
    /// The client sends this for CMD_SET_PLAYERPROFILE, without the enclosing brackets
    /// that record lists use.
    /// </summary>
    private static KvMessage ParseRecord(string s, ref int i)
    {
        Expect(s, ref i, '{');
        var record = ParseFields(s, ref i, terminator: '}');
        Expect(s, ref i, '}');
        return record;
    }

    private static string ParseQuoted(string s, ref int i)
    {
        i++;                                    // opening quote
        var sb = new StringBuilder();

        while (i < s.Length)
        {
            var c = s[i++];
            if (c == '\\' && i < s.Length)
            {
                sb.Append(s[i++]);
                continue;
            }

            if (c == '"')
                return sb.ToString();

            sb.Append(c);
        }

        throw new ProtocolException("Unterminated quoted string.");
    }

    private static object ParseList(string s, ref int i)
    {
        i++;                                    // '['
        var items = new List<KvMessage>();

        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == ']')
        {
            i++;
            return items;
        }

        // Two list shapes share the bracket syntax: brace-wrapped records and a plain
        // comma-separated scalar list (desiredPosition uses the latter).
        if (i < s.Length && s[i] != '{')
            return ParseScalarList(s, ref i);

        while (true)
        {
            SkipWhitespace(s, ref i);
            Expect(s, ref i, '{');
            items.Add(ParseFields(s, ref i, terminator: '}'));
            Expect(s, ref i, '}');

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ',')
            {
                i++;
                continue;
            }

            Expect(s, ref i, ']');
            return items;
        }
    }

    private static KvArray ParseScalarList(string s, ref int i)
    {
        var values = new List<object?>();

        while (true)
        {
            SkipWhitespace(s, ref i);
            values.Add(i < s.Length && s[i] == '"' ? ParseQuoted(s, ref i) : ParseBare(s, ref i));

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ',')
            {
                i++;
                continue;
            }

            Expect(s, ref i, ']');
            return new KvArray(values);
        }
    }

    private static KvRaw ParseBare(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && s[i] != ',' && s[i] != ']' && s[i] != '}' && s[i] != '\0')
            i++;

        return new KvRaw(s[start..i].Trim());
    }

    private static void Expect(string s, ref int i, char expected)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length || s[i] != expected)
        {
            var found = i >= s.Length ? "end of payload" : $"'{s[i]}'";
            throw new ProtocolException($"Expected '{expected}' at offset {i} but found {found}.");
        }

        i++;
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\r' || s[i] == '\n' || s[i] == '\t'))
            i++;
    }
}
