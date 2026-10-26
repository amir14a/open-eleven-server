// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;

namespace OpenEleven.Server.Tests;

/// <summary>
/// Profile filtering in the command registry, GameProfile validation, and the DI
/// plumbing that carries the configured title into the service provider.
/// </summary>
public class GameProfileTests
{
    // The conflict pair only overlaps on Pes2012Pc, so every other Build call below
    // filters one side out and stays valid.
    private sealed class SharedHandlers
    {
        [Command("CMD_SHARED")]
        public ValueTask<KvMessage[]> Handle(CommandContext ctx) => Reply.None();
    }

    private sealed class Variant2010Handlers
    {
        [Command("CMD_VARIANT", Profiles = GameProfile.Pes2010Pc)]
        public ValueTask<KvMessage[]> Handle(CommandContext ctx) => Reply.None();
    }

    private sealed class Variant2011PlusHandlers
    {
        [Command("CMD_VARIANT",
            Profiles = GameProfile.Pes2011Pc | GameProfile.Pes2012Pc | GameProfile.Pes2013Pc)]
        public ValueTask<KvMessage[]> Handle(CommandContext ctx) => Reply.None();
    }

    private sealed class Only2013Handlers
    {
        [Command("CMD_2013_ONLY", Profiles = GameProfile.Pes2013Pc)]
        public ValueTask<KvMessage[]> Handle(CommandContext ctx) => Reply.None();
    }

    private sealed class Conflict2012Handlers
    {
        [Command("CMD_CONFLICT", Profiles = GameProfile.Pes2012Pc)]
        public ValueTask<KvMessage[]> A(CommandContext ctx) => Reply.None();

        [Command("CMD_CONFLICT")]
        public ValueTask<KvMessage[]> B(CommandContext ctx) => Reply.None();
    }

    private static CommandRegistry Build(GameProfile profile)
        => CommandRegistry.Build(profile, typeof(GameProfileTests).Assembly);

    [Theory]
    [InlineData(GameProfile.Pes2010Pc)]
    [InlineData(GameProfile.Pes2011Pc)]
    [InlineData(GameProfile.Pes2013Pc)]
    public void Commands_default_to_every_title(GameProfile profile)
    {
        var registry = Build(profile);

        Assert.True(registry.TryGet("CMD_SHARED", out _));
    }

    [Fact]
    public void Profile_gated_command_registers_only_for_its_title()
    {
        Assert.False(Build(GameProfile.Pes2010Pc).TryGet("CMD_2013_ONLY", out _));
        Assert.False(Build(GameProfile.Pes2011Pc).TryGet("CMD_2013_ONLY", out _));
        Assert.True(Build(GameProfile.Pes2013Pc).TryGet("CMD_2013_ONLY", out _));
    }

    [Fact]
    public void Disjoint_variants_of_the_same_command_do_not_collide()
    {
        Assert.Equal(
            typeof(Variant2010Handlers),
            Build(GameProfile.Pes2010Pc).Entries.Single(e => e.Msg == "CMD_VARIANT").HandlerType);

        Assert.Equal(
            typeof(Variant2011PlusHandlers),
            Build(GameProfile.Pes2011Pc).Entries.Single(e => e.Msg == "CMD_VARIANT").HandlerType);
    }

    // pes2010.exe sends the same in-match commands as pes2011.exe (FUN_007c1f80).
    [Theory]
    [InlineData(GameProfile.Pes2010Pc, true)]
    [InlineData(GameProfile.Pes2011Pc, true)]
    [InlineData(GameProfile.Pes2013Pc, false)]
    public void Match_commands_are_served_to_pes2010_and_pes2011(GameProfile profile, bool served)
    {
        var registry = CommandRegistry.Build(profile, typeof(Handlers.Pes2011Handlers).Assembly);

        foreach (var msg in new[] { "CMD_CHANGE_GAMEPHASE", "CMD_ADD_SCORE", "CMD_GET_GAMEADDPOINT", "CMD_SET_FORFEITEDGAMEEND" })
            Assert.Equal(served, registry.TryGet(msg, out _));
    }

    [Fact]
    public void Overlapping_variants_on_the_active_title_still_throw()
        => Assert.Throws<InvalidOperationException>(() => Build(GameProfile.Pes2012Pc));

    public class ValidatorTests
    {
        private readonly ServerOptionsValidator _validator = new();

        [Theory]
        [InlineData(GameProfile.Pes2010Pc)]
        [InlineData(GameProfile.Pes2011Pc)]
        [InlineData(GameProfile.Pes2012Pc)]
        [InlineData(GameProfile.Pes2013Pc)]
        public void Accepts_each_single_title(GameProfile profile)
            => Assert.False(_validator
                .Validate(null, new ServerOptions { GameProfile = profile, AdvertiseIp = "oe.test" }).Failed);

        [Fact]
        public void Rejects_all()
            => Assert.True(_validator
                .Validate(null, new ServerOptions { GameProfile = GameProfile.All }).Failed);

        [Fact]
        public void Rejects_flag_combinations()
            => Assert.True(_validator.Validate(null,
                new ServerOptions { GameProfile = GameProfile.Pes2010Pc | GameProfile.Pes2011Pc }).Failed);

        [Fact]
        public void Rejects_undefined_bits()
            => Assert.True(_validator
                .Validate(null, new ServerOptions { GameProfile = (GameProfile)16 }).Failed);
    }

    public class HarnessIntegrationTests
    {
        [Fact]
        public async Task The_configured_profile_flows_into_the_service_provider()
        {
            await using var harness = await ServerHarness.CreateAsync(
                configure: settings => settings["Server:GameProfile"] = "Pes2013Pc");

            Assert.Equal(GameProfile.Pes2013Pc, harness.Services.GetRequiredService<GameProfile>());
        }

        [Fact]
        public async Task The_default_profile_is_pes2010()
        {
            await using var harness = await ServerHarness.CreateAsync();

            Assert.Equal(GameProfile.Pes2010Pc, harness.Services.GetRequiredService<GameProfile>());
        }
    }
}
