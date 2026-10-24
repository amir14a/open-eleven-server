// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using System.Text;
using OpenEleven.Protocol.Framing;

namespace OpenEleven.Protocol.Kv;

/// <summary>
/// Serialises a <see cref="KvMessage"/> into the client's key=value grammar.
/// Every quoting, escaping and terminator rule lives here and nowhere else, so a
/// fix found during reverse engineering lands on every command at once.
/// </summary>
public sealed class KvWriter
{
    /// <summary>Top-level payload, NUL-terminated as the client expects.</summary>
    public string Write(KvMessage message)
    {
        var sb = new StringBuilder();
        WriteFields(sb, message);
        sb.Append('\0');
        return sb.ToString();
    }

    private static void WriteFields(StringBuilder sb, KvMessage message)
    {
        var first = true;
        foreach (var (key, value) in message.Fields)
        {
            // An empty indexed group emits no keys at all, so writing a separator for it
            // would leave a dangling comma (count=0,) and a malformed payload.
            if (value is IndexedField { Values.Count: 0 })
                continue;

            if (!first) sb.Append(',');
            first = false;
            AppendField(sb, key, value);
        }
    }

    private static void AppendField(StringBuilder sb, string key, object? value)
    {
        switch (value)
        {
            case null:
            case string:
            case KvRaw:
            case bool:
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                sb.Append(key).Append('=');
                AppendScalar(sb, value);
                break;

            case KvArray array:
                sb.Append(key).Append("=[");
                for (var i = 0; i < array.Values.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendScalar(sb, array.Values[i]);
                }
                sb.Append(']');
                break;

            case IndexedField indexed:
                for (var i = 0; i < indexed.Values.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendField(sb, $"{key}[{i}]", indexed.Values[i]);
                }
                break;

            case KvMessage record:
                sb.Append(key).Append("={");
                WriteFields(sb, record);
                sb.Append('}');
                break;

            case IReadOnlyList<KvMessage> list:
                sb.Append(key).Append("=[");
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    WriteFields(sb, list[i]);
                    sb.Append('}');
                }
                sb.Append(']');
                break;

            default:
                throw new ProtocolException(
                    $"Field '{key}' has unsupported value type {value.GetType().Name}.");
        }
    }

    private static void AppendScalar(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("\"\"");
                break;

            case string s:
                sb.Append('"').Append(Escape(s)).Append('"');
                break;

            case KvRaw raw:
                sb.Append(raw.Text);
                break;

            case bool b:
                sb.Append('"').Append(b ? "YES" : "NO").Append('"');
                break;

            case sbyte or byte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;

            default:
                throw new ProtocolException(
                    $"Unsupported scalar value type {value.GetType().Name}.");
        }
    }

    internal static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
