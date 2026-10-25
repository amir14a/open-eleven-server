// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text;
using OpenEleven.Protocol;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Framing;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Transport;

/// <summary>
/// Builds the multi-line block logged for one packet. Shared by both directions so an
/// inbound and an outbound packet are directly comparable in the console.
/// </summary>
internal static class PacketLogFormatter
{
    /// <summary>
    /// Whether this process is writing to something that understands escape sequences.
    /// Evaluated once: none of these can change while the server runs.
    /// </summary>
    private static readonly bool TerminalSupportsColor =
        !Console.IsOutputRedirected
        && !System.Diagnostics.Debugger.IsAttached
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    internal static AnsiPalette PaletteFor(DebugOptions debug) => debug.Colors switch
    {
        ConsoleColorMode.Always => AnsiPalette.Default,
        ConsoleColorMode.Never => AnsiPalette.None,
        _ => TerminalSupportsColor ? AnsiPalette.Default : AnsiPalette.None,
    };

    /// <summary>
    /// Fields that would let whoever reads the log sign in as the player: passwords, login
    /// tickets and serials. PES 2010's uname is the MD5 credential itself; PES 2011 puts the
    /// plain game id there, which stays readable.
    /// </summary>
    private static bool IsSecret(string key, object? value) => key switch
    {
        "gameid_password" or "loginid_password" or "regcode" or "tmpRegcode" => true,
        "uname" => AuthProof.TryNormalizeMd5(value?.ToString(), out _),
        _ => false,
    };

    /// <summary>The message with secret values masked, or the message itself when it has none.</summary>
    internal static KvMessage Redact(KvMessage message)
    {
        if (!message.Fields.Any(f => IsSecret(f.Key, f.Value)))
            return message;

        var masked = new KvMessage();
        foreach (var (key, value) in message.Fields)
            masked.Set(key, IsSecret(key, value) ? "***" : value);
        return masked;
    }

    /// <summary>A parsed key=value packet: pretty payload, then the bytes.</summary>
    public static string Format(KvMessage message, string payloadText, DebugOptions debug)
    {
        var redacted = Redact(message);
        if (!ReferenceEquals(redacted, message))
            (message, payloadText) = (redacted, new KvWriter().Write(redacted).TrimEnd('\0'));

        var palette = PaletteFor(debug);
        var sb = new StringBuilder();

        if (debug.PrettyPrintPayloads)
            sb.Append(KvPrettyPrinter.Format(message, palette));
        else
            sb.Append("  ").Append(payloadText);

        AppendHex(sb, Encoding.ASCII.GetBytes(payloadText), debug, "payload", palette);
        return sb.ToString();
    }

    /// <summary>
    /// A packet with no readable payload. These were previously logged at Debug, so at
    /// the default Information level they did not appear at all.
    /// </summary>
    public static string FormatBinary(ReadOnlySpan<byte> data, DebugOptions debug)
    {
        var sb = new StringBuilder();
        AppendHex(sb, data, debug, "body (still Blowfish-encrypted)", PaletteFor(debug));
        return sb.Length == 0 ? "  (no data)" : sb.ToString();
    }

    private static void AppendHex(
        StringBuilder sb,
        ReadOnlySpan<byte> data,
        DebugOptions debug,
        string label,
        AnsiPalette palette)
    {
        if (!debug.HexDump || data.IsEmpty)
            return;

        var max = debug.HexDumpMaxBytes <= 0 ? int.MaxValue : debug.HexDumpMaxBytes;

        if (sb.Length > 0)
            sb.Append('\n');

        sb.Append("  ")
          .Append(palette.Paint(palette.Dim, $"{label} ({data.Length} bytes):"))
          .Append('\n');

        // Indent the dump so it reads as part of this packet's block rather than as
        // separate log lines.
        foreach (var line in HexDump.Format(data, maxBytes: max, palette: palette).Split('\n'))
            sb.Append("  ").Append(line).Append('\n');

        sb.Length--; // trailing newline
    }
}
