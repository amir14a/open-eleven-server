// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

public class SiteDataTests
{
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
}
