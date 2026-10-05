namespace SalonTracker.Api.Infrastructure;

/// <summary>
/// Security headers are set by the app itself, not by the proxy in front of it:
/// in the private (Tailscale) mode there is no Caddy, and the app must be safe on its own.
/// </summary>
public static class SecurityHeaders
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " + // also covers the service worker (worker-src)
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " + // React writes style attributes
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "manifest-src 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool isProduction) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = isProduction
                ? ContentSecurityPolicy + "; upgrade-insecure-requests"
                : ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            // only meaningful over HTTPS; in development it would pin http://localhost to https
            if (isProduction) headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            await next();
        });
}
