// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

public class SiteDataTests
{
    [Theory]
    [InlineData(1, 1, new[] { 1 })]
    [InlineData(1, 4, new[] { 1, 2, 3, 4 })]
    [InlineData(6, 20, new[] { 1, 0, 4, 5, 6, 7, 8, 0, 20 })]
    [InlineData(20, 20, new[] { 1, 0, 18, 19, 20 })]
    [InlineData(3, 20, new[] { 1, 2, 3, 4, 5, 0, 20 })]
    public void The_pager_links_the_ends_and_the_neighbourhood(int page, int pages, int[] expected)
        => Assert.Equal(expected, new Pager(page, pages, _ => "").Numbers());

    [Fact]
    public void No_query_parameter_uses_a_route_value_razor_pages_reserves()
    {
        // ?page= and ?handler= are taken by routing ("page" holds the page's own path), so a
        // property bound to either name silently keeps its default.
        var bound = typeof(Pager).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Microsoft.AspNetCore.Mvc.RazorPages.PageModel)))
            .SelectMany(t => t.GetProperties())
            .Select(p => p.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.BindPropertyAttribute), true)
                .Cast<Microsoft.AspNetCore.Mvc.BindPropertyAttribute>().FirstOrDefault()?.Name ?? p.Name);
        Assert.DoesNotContain(bound, name => name.Equals("page", StringComparison.OrdinalIgnoreCase) || name.Equals("handler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_the_offered_page_sizes_are_accepted()
        => Assert.Equal((25, 100, 50, 50), (SiteData.Size(25), SiteData.Size(100), SiteData.Size(0), SiteData.Size(100000)));
}
