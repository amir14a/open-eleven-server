// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Http;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

public class HttpTraceMiddlewareTests
{
    private static Endpoint EndpointWith(params object[] metadata)
        => new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test");

    [Fact]
    public void Redacts_a_body_when_the_endpoint_is_marked_sensitive()
        => Assert.True(HttpTraceMiddleware.ShouldRedactBody(
            EndpointWith(new SensitiveBodyAttribute())));

    [Fact]
    public void Traces_a_body_when_the_endpoint_is_not_marked()
        => Assert.False(HttpTraceMiddleware.ShouldRedactBody(EndpointWith()));

    [Fact]
    public void Traces_a_body_when_no_endpoint_matched()
        => Assert.False(HttpTraceMiddleware.ShouldRedactBody(null));

    [Fact]
    public void Finds_the_marker_alongside_other_metadata()
        => Assert.True(HttpTraceMiddleware.ShouldRedactBody(
            EndpointWith("unrelated", 42, new SensitiveBodyAttribute())));
}
