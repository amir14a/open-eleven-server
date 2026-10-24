// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Data;

/// <summary>Validation shared by profile creation and schema backfills.</summary>
public static class PlayerNamePolicy
{
    /// <summary>Maximum profile-name length accepted by the PES2010 client input field.</summary>
    public const int MaxLength = 15;

    public static bool TryValidate(string? supplied, out string name, out string normalized)
    {
        name = supplied?.Trim() ?? "";
        normalized = "";

        if (name.Length is < 1 or > MaxLength || name.Any(char.IsControl))
            return false;

        normalized = Normalize(name);
        return true;
    }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
