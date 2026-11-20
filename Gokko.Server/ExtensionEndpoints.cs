using System.Security.Cryptography;
using System.Text;
using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>内線番号の管理（管理画面 admin.html の「内線番号」）。ヘッダー X-Admin-Key に AdminKey を付けて呼ぶ。</summary>
public static class ExtensionEndpoints
{
    public sealed record ExtensionNumbersRequest(string? Dispatcher, Dictionary<string, string>? Stations);

    public static void MapExtensionEndpoints(this WebApplication app, ServerOptions options)
    {
        var g = app.MapGroup("/api/admin/extension").AddEndpointFilter(async (ctx, next) =>
        {
            if (string.IsNullOrEmpty(options.AdminKey))
                return Results.Problem("AdminKey が設定されていないため、管理機能は無効です", statusCode: 503);
            var given = ctx.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(options.AdminKey)))
                return Results.Problem("管理キーが違います", statusCode: 401);
            return await next(ctx);
        });

        g.MapGet("", (ExtensionBook book, ExtensionExchange exchange) => Results.Ok(View(book, exchange)));

        g.MapPost("", async (ExtensionNumbersRequest req, ExtensionBook book, ExtensionExchange exchange, ExtensionPublisher publisher, IncidentManager incidents) =>
        {
            var error = book.Set(req.Dispatcher, req.Stations, exchange.NumberedStations().Select(s => s.Id).ToList());
            if (error != null) return Results.BadRequest(new { message = error });
            incidents.Note("内線番号を決め直しました（司令 " + book.Dispatcher + "）");
            await publisher.PublishAsync();
            return Results.Ok(View(book, exchange));
        });
    }

    private static object View(ExtensionBook book, ExtensionExchange exchange) => new
    {
        dispatcher = book.Dispatcher,
        stations = exchange.NumberedStations().Select(s => new { id = s.Id, name = s.Name, number = book.Station(s.Id) }).ToList()
    };
}
