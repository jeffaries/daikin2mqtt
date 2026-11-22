using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Daikin2Mqtt.Daikin;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Service;

/// <summary>The configuration web interface: static Vue/Vuetify files (wwwroot) and a small JSON API.</summary>
public static class WebInterface
{
    public static void UseWebInterface(this WebApplication app, WebOptions web)
    {
        // When a password is set, the whole interface (files, API and live monitor) is behind HTTP Basic authentication.
        app.Use(async (context, next) =>
        {
            if (!string.IsNullOrEmpty(web.Password) && !HasValidCredentials(context.Request, web))
            {
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"daikin2mqtt\", charset=\"UTF-8\"";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapHub<MonitorHub>("/hubs/monitor");

        app.MapGet("/api/config", (ConfigEditor editor) => Results.Json(editor.Read()));

        app.MapPut("/api/config", (JsonObject body, ConfigEditor editor) =>
        {
            if (body["config"] is not JsonObject config || body["entities"] is not JsonArray entities)
            {
                return Results.BadRequest(new { errors = new[] { "Expected { \"config\": {...}, \"entities\": [...] }." } });
            }

            try
            {
                var errors = editor.Save(config, entities);
                return errors.Count == 0 ? Results.Ok(new { saved = true }) : Results.BadRequest(new { errors });
            }
            catch (IOException ex)
            {
                return Results.Json(new { errors = new[] { $"Cannot write the configuration: {ex.Message}" } }, statusCode: 500);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { errors = new[] { $"Cannot write the configuration (is the folder mounted read-only?): {ex.Message}" } }, statusCode: 500);
            }
        });

        // Looks for Daikin units on the LAN (UDP broadcast); the interface offers the ones found for Daikin.Units.
        app.MapPost("/api/scan", async (IDaikinScanner scanner, IOptions<DaikinOptions> options, CancellationToken ct) =>
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.ScanTimeoutSeconds, 1, 30));
            try
            {
                var found = await scanner.ScanAsync(timeout, ct);
                return Results.Json(new { units = found });
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
            {
                return Results.Json(new { units = Array.Empty<DaikinScanResult>(), error = ex.Message });
            }
        });

        // The container restart policy (restart: unless-stopped) brings the service back with the new files.
        app.MapPost("/api/restart", (IHostApplicationLifetime lifetime) =>
        {
            _ = Task.Delay(500).ContinueWith(_ => lifetime.StopApplication());
            return Results.Accepted();
        });
    }

    private static bool HasValidCredentials(HttpRequest request, WebOptions web)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var expected = Encoding.UTF8.GetBytes($"{(string.IsNullOrEmpty(web.Username) ? "admin" : web.Username)}:{web.Password}");
            var given = Convert.FromBase64String(header["Basic ".Length..].Trim());
            return CryptographicOperations.FixedTimeEquals(given, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
