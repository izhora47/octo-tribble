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

// Serilog — use AddSerilog (IServiceCollection extension, Serilog.AspNetCore 8+).
// Avoids hooking IHostBuilder directly, which can deadlock on net10.0-windows
// when combined with UseWindowsService().
builder.Services.AddSerilog((_, config) =>
{
    var logPath = builder.Configuration["LogSettings:FilePath"] ?? "logs\\ldap-api-.log";

    // If the path is relative, anchor it to the content root so the log ends up
    // next to the executable (important when running as a Windows Service).
    if (!Path.IsPathRooted(logPath))
        logPath = Path.Combine(builder.Environment.ContentRootPath, logPath);

    config
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
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
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("SmtpSettings"));

// Services
builder.Services.AddScoped<IAdService, AdService>();
builder.Services.AddScoped<IExchangeService, ExchangeService>();
builder.Services.AddScoped<IEmailService, EmailService>();

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
