using AeroSynapse.Application;
using AeroSynapse.Application.Health;
using AeroSynapse.Infrastructure;
using MediatR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

app.MapGet("/health", async (ISender sender, CancellationToken cancellationToken) =>
    Results.Ok(await sender.Send(new GetHealthQuery(), cancellationToken)));

app.Run();

public partial class Program;
