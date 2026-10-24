// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Protocol;

/// <summary>
/// ANSI colour codes for log rendering, or empty strings when colour is off.
/// </summary>
/// <remarks>
/// Passing a palette rather than checking a flag at each call site means the formatters
/// have one code path: <see cref="None"/> substitutes empty strings and the output is
/// byte-identical to the uncoloured version.
/// <para>
/// Only worth enabling on a real terminal. Visual Studio's Output window is fed by the
/// Debug logger provider, which does not interpret escape sequences and will show them
/// literally as <c>←[36m</c>.
/// </para>
/// </remarks>
public sealed record AnsiPalette(
    string Key,
    string Text,
    string Number,
    string Flag,
    string Structure,
    string Dim,
    string Reset)
{
    /// <summary>No colour: every code is empty, so formatting is unchanged.</summary>
    public static readonly AnsiPalette None = new("", "", "", "", "", "", "");

    public static readonly AnsiPalette Default = new(
        Key: "\e[36m",        // cyan — field names
        Text: "\e[32m",       // green — quoted strings
        Number: "\e[33m",     // yellow — bare numerics
        Flag: "\e[35m",       // magenta — YES/NO, which read as values but act as enums
        Structure: "\e[90m",  // grey — braces, brackets, indices
        Dim: "\e[90m",        // grey — offsets and annotations
        Reset: "\e[0m");

    public bool IsEnabled => Reset.Length > 0;

    /// <summary>Wraps <paramref name="value"/> in <paramref name="code"/>, if enabled.</summary>
    public string Paint(string code, string value)
        => code.Length == 0 ? value : code + value + Reset;
}
