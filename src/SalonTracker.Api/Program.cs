using SalonTracker.Api;
using SalonTracker.Api.Cli;

// "dotnet SalonTracker.Api.dll seed" and friends run a tool instead of the web server
var isCommand = Commands.IsCommand(args);

var builder = WebApplication.CreateBuilder(isCommand ? [] : args);
builder.AddSalonTracker();

var app = builder.Build();
await app.MigrateDatabaseAsync();

if (isCommand) return await Commands.RunAsync(app.Services, args);

app.UseSalonTracker();
await app.RunAsync();
return 0;

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
