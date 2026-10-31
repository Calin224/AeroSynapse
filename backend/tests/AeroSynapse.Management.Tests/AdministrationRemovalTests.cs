using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class AdministrationRemovalTests
{
    [Fact]
    public async Task Retired_administration_endpoints_are_unavailable_for_every_workspace_role()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ManagementHost.CreateAsync();
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.DoesNotContain(routes, endpoint =>
        {
            var path = endpoint.RoutePattern.RawText?.TrimStart('/') ?? "";
            return path.StartsWith("admin/", StringComparison.Ordinal) || path == "audit"
                || path.Contains("/audit/", StringComparison.Ordinal)
                || path.Contains("adapters", StringComparison.Ordinal)
                || path.EndsWith("data-provenance", StringComparison.Ordinal);
        });
        var id = Guid.NewGuid();
        var endpoints = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/admin/users"),
            (HttpMethod.Put, $"/admin/users/{id}"),
            (HttpMethod.Delete, $"/admin/users/{id}/sessions"),
            (HttpMethod.Get, "/admin/ai-governance"),
            (HttpMethod.Put, "/admin/ai-governance/policy"),
            (HttpMethod.Get, "/admin/ai-governance/events"),
            (HttpMethod.Get, "/admin/ai-governance/policies"),
            (HttpMethod.Get, "/audit"),
            (HttpMethod.Get, $"/recommendations/{id}/audit/export"),
            (HttpMethod.Get, "/adapters/providers"),
            (HttpMethod.Get, $"/adapters/snapshots/{id}"),
            (HttpMethod.Post, $"/adapters/snapshots/{id}/import"),
            (HttpMethod.Get, $"/scenarios/{id}/adapters/snapshots"),
            (HttpMethod.Post, $"/scenarios/{id}/adapters/snapshots"),
            (HttpMethod.Get, $"/runs/{id}/data-provenance"),
        };
        foreach (var role in new[] { "Viewer", "Operator", "Admin", "anonymous" })
        {
            using var client = role == "anonymous" ? host.AnonymousClient() : await host.SignInAsync(role);
            foreach (var (method, path) in endpoints)
            {
                using var request = new HttpRequestMessage(method, path);
                if (method == HttpMethod.Post || method == HttpMethod.Put)
                    request.Content = JsonContent.Create(new { });
                using var response = await client.SendAsync(request, token);
                // The shared authorization fallback also protects unknown URLs.
                var expected = role == "anonymous" ? HttpStatusCode.Unauthorized
                    : role == "Viewer" && method != HttpMethod.Get ? HttpStatusCode.Forbidden
                    : HttpStatusCode.NotFound;
                Assert.True(response.StatusCode == expected,
                    $"{role}: {method} {path} returned {response.StatusCode}");
            }
        }
    }
}
