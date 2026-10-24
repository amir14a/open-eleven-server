// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Reflection;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Dispatch;

public sealed record CommandEntry(
    string Msg,
    ServiceRole Roles,
    SessionState RequiredState,
    Type HandlerType,
    Func<object, CommandContext, ValueTask<KvMessage[]>> Invoke);

/// <summary>
/// Built once at startup by scanning for <see cref="CommandAttribute"/>, then immutable.
/// Reflection happens during the scan only; dispatch goes through a compiled delegate.
/// </summary>
public sealed class CommandRegistry
{
    private readonly FrozenDictionary<string, CommandEntry> _byMsg;

    private CommandRegistry(IEnumerable<CommandEntry> entries)
    {
        _byMsg = entries.ToFrozenDictionary(e => e.Msg, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<CommandEntry> Entries => _byMsg.Values;

    public int Count => _byMsg.Count;

    public bool TryGet(string msg, out CommandEntry entry) => _byMsg.TryGetValue(msg, out entry!);

    /// <summary>All handler types found, so the host can register them in DI.</summary>
    public static IReadOnlyList<Type> DiscoverHandlerTypes(
        GameProfile activeProfile, params Assembly[] assemblies)
        => Scan(activeProfile, assemblies).Select(x => x.Method.DeclaringType!).Distinct().ToArray();

    /// <summary>
    /// Scans the given assemblies for <see cref="CommandAttribute"/> handlers and keeps
    /// those declared for <paramref name="activeProfile"/>. The profile filter runs before
    /// the duplicate-message check, so per-title variants of the same command coexist.
    /// </summary>
    public static CommandRegistry Build(GameProfile activeProfile, params Assembly[] assemblies)
    {
        var entries = new List<CommandEntry>();
        var seen = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        foreach (var (method, attribute) in Scan(activeProfile, assemblies))
        {
            if (seen.TryGetValue(attribute.Msg, out var previous))
                throw new InvalidOperationException(
                    $"Command '{attribute.Msg}' is handled twice: " +
                    $"{previous.DeclaringType!.Name}.{previous.Name} and " +
                    $"{method.DeclaringType!.Name}.{method.Name}.");

            seen[attribute.Msg] = method;
            entries.Add(new CommandEntry(
                attribute.Msg,
                attribute.Roles,
                attribute.RequiredState,
                method.DeclaringType!,
                CompileInvoker(method)));
        }

        return new CommandRegistry(entries);
    }

    private static IEnumerable<(MethodInfo Method, CommandAttribute Attribute)> Scan(
        GameProfile activeProfile, params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                    continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var attributes = method.GetCustomAttributes<CommandAttribute>().ToArray();
                    if (attributes.Length == 0)
                        continue;

                    Validate(method);
                    foreach (var attribute in attributes)
                    {
                        // A command not declared for the active title is skipped before the
                        // duplicate check, so disjoint per-title variants of the same message
                        // never collide.
                        if ((attribute.Profiles & activeProfile) == 0)
                            continue;

                        yield return (method, attribute);
                    }
                }
            }
        }
    }

    private static void Validate(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var ok = method.ReturnType == typeof(ValueTask<KvMessage[]>)
                 && parameters.Length == 1
                 && parameters[0].ParameterType == typeof(CommandContext);

        if (!ok)
            throw new InvalidOperationException(
                $"{method.DeclaringType!.Name}.{method.Name} is marked [Command] but must be " +
                "'ValueTask<KvMessage[]> Method(CommandContext ctx)'.");
    }

    private static Func<object, CommandContext, ValueTask<KvMessage[]>> CompileInvoker(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var context = Expression.Parameter(typeof(CommandContext), "ctx");

        var call = Expression.Call(
            Expression.Convert(instance, method.DeclaringType!),
            method,
            context);

        return Expression
            .Lambda<Func<object, CommandContext, ValueTask<KvMessage[]>>>(call, instance, context)
            .Compile();
    }
}
