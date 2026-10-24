// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.Tests;

/// <summary>
/// Guards the two properties of the markup that cannot be expressed in C#: it must reach
/// nothing off this machine, and it must need no JavaScript. The view and stylesheet are
/// copied into the test output as plain files (a .cshtml is inert in a non-Web SDK project).
/// </summary>
public class RegistrationMarkupTests
{
    private static readonly string View = ReadAsset("Register.cshtml");
    private static readonly string Stylesheet = ReadAsset("register.css");

    private static string ReadAsset(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));

    [Theory]
    [InlineData("GameId")]
    [InlineData("Password")]
    [InlineData("Confirm")]
    [InlineData("RegCode")]
    public void Binds_every_field_registration_requires(string property)
        => Assert.Contains($"asp-for=\"{property}\"", View);

    [Fact]
    public void Posts_the_form_natively()
        => Assert.Contains("method=\"post\"", View);

    [Fact]
    public void Needs_no_javascript()
    {
        // The whole point of hashing server-side was to remove the hand-rolled MD5.
        Assert.DoesNotContain("<script", View, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch(", View);
    }

    [Fact]
    public void Reaches_nothing_off_this_machine()
    {
        // The game machine has no internet route through this server, so an external
        // reference would leave the page unstyled. Same-origin /css/register.css is fine.
        foreach (var asset in new[] { View, Stylesheet })
        {
            Assert.DoesNotContain("http://", asset);
            Assert.DoesNotContain("https://", asset);
        }
    }

    [Fact]
    public void Reads_its_field_limits_from_the_endpoint_constants()
    {
        // Written as @RegistrationEndpoint.Max... rather than literals, so the page and the
        // validator cannot drift.
        Assert.Contains("@RegistrationEndpoint.MaxGameIdLength", View);
        Assert.Contains("@RegistrationEndpoint.MaxPasswordLength", View);
        Assert.Contains("@RegistrationEndpoint.MaxRegCodeLength", View);
    }

    [Fact]
    public void Pins_its_route_explicitly()
        => Assert.StartsWith("@page \"/register\"", View);
}
