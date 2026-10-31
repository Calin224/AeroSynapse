namespace AeroSynapse.API.Identity;

// Only explicit synthetic demo routes and a health probe face browsers.
// Simulation/optimization APIs stay service-to-service; never relay cookies to Python.
internal static class IntelligenceBrowserEndpoints
{
    public static void MapIntelligenceBrowserEndpoints(this WebApplication app)
    {
        app.MapGet("/intelligence/health", (HttpContext context, IHttpClientFactory clients) =>
            Relay(context, clients, "health", HttpMethod.Get)).AllowAnonymous();
        app.MapPost("/intelligence/baggage/v1/run", (HttpContext context, IHttpClientFactory clients) =>
            Relay(context, clients, "baggage/v1/run", HttpMethod.Post)).RequireAuthorization(WorkspaceRoles.Write)
            .AddEndpointFilter<AeroSynapse.API.Audit.AuditCalculationFilter>();
        foreach (var action in new[] { "run", "optimize" })
            app.MapPost("/intelligence/airside/v1/" + action, (HttpContext context, IHttpClientFactory clients) =>
                Relay(context, clients, "airside/v1/" + action, HttpMethod.Post)).RequireAuthorization(WorkspaceRoles.Write)
                .AddEndpointFilter<AeroSynapse.API.Audit.AuditCalculationFilter>();
        foreach (var action in new[] { "compare", "optimize" })
            app.MapPost("/intelligence/energy/v1/" + action, (HttpContext context, IHttpClientFactory clients) =>
                Relay(context, clients, "energy/v1/" + action, HttpMethod.Post)).RequireAuthorization(WorkspaceRoles.Write)
                .AddEndpointFilter<AeroSynapse.API.Audit.AuditCalculationFilter>();
    }

    private static async Task<IResult> Relay(HttpContext context, IHttpClientFactory clients, string path, HttpMethod method)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
        {
            if (!context.Request.HasJsonContentType()) return WorkspaceIdentity.Problem(415, "json_required", "A JSON command is required.");
            // Read no more than the bounded command envelope, even for chunked requests.
            var bytes = new byte[65537];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), context.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            if (count > 65536) return WorkspaceIdentity.Problem(413, "command_too_large", "The command exceeds 64 KiB.");
            request.Content = new ByteArrayContent(bytes, 0, count);
            request.Content.Headers.ContentType = new("application/json");
        }
        if (context.Items["CorrelationId"] is string correlation) request.Headers.Add("X-Correlation-ID", correlation);
        try
        {
            using var response = await clients.CreateClient("BrowserIntelligence").SendAsync(request, context.RequestAborted);
            if ((int)response.StatusCode >= 500) return WorkspaceIdentity.Problem(502, "intelligence_unavailable", "The synthetic demo service is unavailable.");
            var json = await response.Content.ReadAsStringAsync(context.RequestAborted);
            return Results.Content(json, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            return WorkspaceIdentity.Problem(502, "intelligence_unavailable", "The synthetic demo service is unavailable.");
        }
    }
}
