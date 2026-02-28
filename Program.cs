using ldap_api.Configuration;
using ldap_api.Endpoints;
using ldap_api.Services;
using Microsoft.Extensions.Hosting.WindowsServices;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : default
};

var builder = WebApplication.CreateBuilder(options);

builder.Host.UseWindowsService();

// Serilog — configured programmatically; only the log file path is read from appsettings.json.
// (Avoid ReadFrom.Configuration: it uses reflection to discover sink assemblies at startup,
//  which can hang under net10.0-windows.)
builder.Host.UseSerilog((context, config) =>
{
    var logPath = context.Configuration["LogSettings:FilePath"] ?? "logs\\ldap-api-.log";

    config
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            logPath,
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}");
});

// Configuration
builder.Services.Configure<AdSettings>(builder.Configuration.GetSection("AdSettings"));

// Services
builder.Services.AddScoped<IAdService, AdService>();
builder.Services.AddScoped<IExchangeService, ExchangeService>();

// OpenAPI
builder.Services.AddOpenApi();

var app = builder.Build();

// Interactive API explorer at /scalar/v1
app.MapOpenApi();
app.MapScalarApiReference();

// Convenience redirect: /swagger → /scalar/v1
app.MapGet("/swagger", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

// Endpoints
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithTags("Health")
    .WithSummary("Health check");

app.MapUserEndpoints();
app.MapExchangeEndpoints();

app.Run();
