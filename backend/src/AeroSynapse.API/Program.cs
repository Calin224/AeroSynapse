using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AeroSynapse.API.Endpoints;
using AeroSynapse.Application;
using AeroSynapse.Application.Health;
using AeroSynapse.Domain.Airports;
using AeroSynapse.Infrastructure;
using AeroSynapse.Infrastructure.Airports;
using MediatR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("AirportMaps")
    ?? "Data Source=airport-maps.db");
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    // Let the domain validator report nonfinite map coordinates with field-level issues.
    var resolver = options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
    options.SerializerOptions.TypeInfoResolver = resolver.WithAddedModifier(type =>
    {
        if (type.Type.Namespace != typeof(AirportMap).Namespace) return;
        foreach (var property in type.Properties.Where(property => property.PropertyType == typeof(double)))
            property.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    });
});

var app = builder.Build();

// Keep map data local to development until workspace authentication is integrated.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AirportMapDbContext>().Database.EnsureCreatedAsync();
    app.MapAirportEndpoints();
}

app.MapGet("/health", async (ISender sender, CancellationToken cancellationToken) =>
    Results.Ok(await sender.Send(new GetHealthQuery(), cancellationToken)));

app.Run();

public partial class Program;
