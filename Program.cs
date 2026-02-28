using ldap_api.Configuration;
using ldap_api.Endpoints;
using ldap_api.Services;
using Microsoft.Extensions.Hosting.WindowsServices;
using Scalar.AspNetCore;
using Serilog;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : default
};

var builder = WebApplication.CreateBuilder(options);

builder.Host.UseWindowsService();

// Serilog — reads the "Serilog" section from appsettings.json
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

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
