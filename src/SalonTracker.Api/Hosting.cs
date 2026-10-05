using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using SalonTracker.Api.Auth;
using SalonTracker.Api.Backup;
using SalonTracker.Api.Cli;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Data;
using SalonTracker.Api.Features;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api;

/// <summary>The composition root: services, middleware order and endpoints.</summary>
public static class Hosting
{
    public static WebApplicationBuilder AddSalonTracker(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration["PORT"] ?? "3001"}");
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });

        // read when first needed, so tests can override the configuration
        services.AddSingleton(sp => AppSettings.From(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<AppDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<AppSettings>().ConnectionString).UseSnakeCaseNamingConvention());

        services.AddSalonAuth();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(AuthEndpoints.LoginRateLimit, context => PerClient(context, 20));
            options.AddPolicy(AuthEndpoints.PasswordRateLimit, context => PerClient(context, 10));
            options.OnRejected = (context, _) =>
                new ValueTask(ApiErrors.Write(context.HttpContext.Response, StatusCodes.Status429TooManyRequests, "rate_limited"));
        });

        // Exactly one proxy in front of the app (tailscale serve, or Caddy in public mode).
        // Trusting the whole X-Forwarded-For chain would let a client forge its own address
        // and get a fresh rate-limit bucket on every login attempt.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));
        // malformed bodies reach the exception handler instead of an empty 400
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();
        services.AddValidatorsFromAssemblyContaining<LoginValidator>(includeInternalTypes: true);
        services.AddOpenApi();

        services.AddSingleton<ErrorLog>();
        services.AddSingleton<Mailer>();
        services.AddSingleton<BackupService>();
        services.AddScoped<JsonBackup>();
        services.AddScoped<ExcelExport>();
        services.AddScoped<Seeder>();
        services.AddHostedService<NightlyBackupScheduler>();

        return builder;
    }

    private static RateLimitPartition<string> PerClient(HttpContext context, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });

    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public static WebApplication UseSalonTracker(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AppSettings>();

        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseSecurityHeaders(app.Environment.IsProduction());

        // the built web app; in development Vite serves it and proxies /api here
        var webRoot = settings.WebDist is { } dist && Directory.Exists(dist) ? new PhysicalFileProvider(dist) : null;
        if (webRoot is not null)
        {
            // before routing: the SPA fallback below would otherwise claim /assets/*.js too
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = webRoot,
                ContentTypeProvider = ContentTypes(),
                OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                    context.Context.Request.Path.StartsWithSegments("/assets")
                        ? "public, max-age=31536000, immutable" // file names carry a content hash
                        : "no-cache", // index.html, sw.js, manifest: always check for a new version
            });
        }

        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment()) app.MapOpenApi();

        app.MapGet("/api/health", () => new { ok = true }).WithTags("System");
        app.MapAuthEndpoints();
        app.MapUserEndpoints();
        app.MapServiceEndpoints();
        app.MapSessionEndpoints();
        app.MapStatsEndpoints();
        app.MapSystemEndpoints();

        app.MapFallback(async context =>
        {
            var isPage = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && !context.Request.Path.StartsWithSegments("/api");
            if (!isPage || webRoot is null)
            {
                await ApiErrors.Write(context.Response, StatusCodes.Status404NotFound, "not_found");
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(webRoot.GetFileInfo("index.html"));
        });

        return app;
    }

    private static FileExtensionContentTypeProvider ContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".webmanifest"] = "application/manifest+json";
        return provider;
    }
}
