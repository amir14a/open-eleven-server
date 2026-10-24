// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;

namespace OpenEleven.Protocol.Kv;

/// <summary>
/// An ordered bag of protocol fields. Field order is preserved because the client's
/// parser is position-sensitive for some commands.
/// </summary>
public sealed class KvMessage
{
    private readonly List<KeyValuePair<string, object?>> _fields = new();

    public IReadOnlyList<KeyValuePair<string, object?>> Fields => _fields;

    public string? MsgName => GetString("msg");

    public int Rqid => GetInt32("rqid", 1);

    // ---- construction -----------------------------------------------------

    public static KvMessage Ok(string msg, int rqid) => new KvMessage()
        .Set("result", "NOERR")
        .Set("msg", msg)
        .Set("rqid", rqid);

    public static KvMessage Err(string msg, int rqid, string reason) => new KvMessage()
        .Set("result", "ERR")
        .Set("msg", msg)
        .Set("rqid", rqid)
        .Set("reason", reason);

    /// <summary>
    /// Emits a client-native failure code. Some PES2010 state machines match the whole
    /// result value (for example ERR_NOPLAYER), not an ERR plus reason pair.
    /// </summary>
    public static KvMessage Fail(string msg, int rqid, string result) => new KvMessage()
        .Set("result", result)
        .Set("msg", msg)
        .Set("rqid", rqid);

    public KvMessage Set(string key, object? value)
    {
        _fields.Add(new KeyValuePair<string, object?>(key, value));
        return this;
    }

    /// <summary>
    /// Emits the element-count field and the list together so the two can never disagree.
    /// A mismatch between them is one of the documented ways to crash the client, so the
    /// only supported way to write a list is through this method.
    /// </summary>
    public KvMessage SetList(string countKey, string listKey, IReadOnlyList<KvMessage> items)
        => Set(countKey, items.Count).Set(listKey, items);

    public KvMessage SetIndexed(string key, IndexedField field) => Set(key, field);

    // ---- inspection -------------------------------------------------------

    public bool Has(string key) => _fields.Any(f => f.Key == key);

    public object? GetValue(string key)
    {
        for (var i = 0; i < _fields.Count; i++)
            if (_fields[i].Key == key)
                return _fields[i].Value;
        return null;
    }

    public string? GetString(string key) => GetValue(key) switch
    {
        string s => s,
        KvRaw raw => raw.Text,
        int or long or uint => Convert.ToString(GetValue(key), CultureInfo.InvariantCulture),
        _ => null,
    };

    public int GetInt32(string key, int fallback = 0)
        => int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    /// <summary>
    /// Records under <paramref name="key"/>, from either the bracketed list
    /// <see cref="SetList"/> writes or an <see cref="IndexedField"/> of records.
    /// </summary>
    public IReadOnlyList<KvMessage> GetList(string key) => GetValue(key) switch
    {
        IReadOnlyList<KvMessage> list => list,
        IndexedField indexed => indexed.Values.OfType<KvMessage>().ToArray(),
        _ => Array.Empty<KvMessage>(),
    };

    public override string ToString()
        => string.Join(",", _fields.Select(f => $"{f.Key}={f.Value}"));
}
