// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Protocol.Kv;

/// <summary>
/// A value emitted verbatim, without quoting. Used for bare numerics parsed off the
/// wire and for the rare field whose exact spelling matters more than its type.
/// </summary>
public readonly record struct KvRaw(string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// A bracketed list of scalars: <c>key=[v0,v1,...]</c>. Distinct from a list of
/// <see cref="KvMessage"/>, which brace-wraps each element. The client uses this plain
/// form for <c>desiredPosition</c> inside a room member entry.
/// </summary>
public sealed class KvArray
{
    public KvArray(IReadOnlyList<object?> values) => Values = values;

    public KvArray(params string[] values) => Values = values;

    public static KvArray Repeat(object? value, int count)
        => new(Enumerable.Repeat(value, count).ToArray());

    public IReadOnlyList<object?> Values { get; }
}

/// <summary>
/// Expands to <c>key[0]=v0,key[1]=v1,...</c>, the indexed-field form the client uses for
/// <c>desired_position</c> and friends. Keeping it a value type means a handler cannot
/// accidentally emit the indices out of order.
/// </summary>
public sealed class IndexedField
{
    public IndexedField(IReadOnlyList<object?> values) => Values = values;

    public IndexedField(params string[] values) => Values = values;

    public IndexedField(IEnumerable<int> values) => Values = values.Cast<object?>().ToArray();

    public IReadOnlyList<object?> Values { get; }
}
