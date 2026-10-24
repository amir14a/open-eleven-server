// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OpenEleven.Server.Configuration;

/// <summary>Compatibility and operator policy shared by CMD_CHECK_STRING and room names.</summary>
public sealed class ProtocolTextPolicy(IOptionsMonitor<ServerOptions> options)
{
    // FUN_007AC550 copies the checked value into a 0x73-byte destination.
    public const int MaxEncodedBytes = 114;

    public bool TryValidate(string? input, out string value)
    {
        value = input?.Trim() ?? "";
        if (value.Length == 0
            || value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > MaxEncodedBytes)
            return false;

        foreach (var configured in options.CurrentValue.Protocol.BlockedTerms)
        {
            var term = configured.Trim();
            if (term.Length == 0)
                continue;

            var pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])";
            if (Regex.IsMatch(
                    value,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)))
                return false;
        }

        return true;
    }
}
