using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>
/// ハブの呼び出し（在線の報告・音声など）1回ごとの時間を、種類ごとに ServerLoad へ記録する（どこが重いかを見るため）。
/// </summary>
public sealed class HubTiming(ServerLoad load) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext ctx, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next(ctx);
        }
        finally
        {
            load.RecordWork($"{ctx.Hub.GetType().Name}.{ctx.HubMethodName}", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

public static class LoadTimingExtensions
{
    /// <summary>
    /// 画面の API（HTTP）1回ごとの時間を、道筋（"GET /api/diaview/diagram" など）ごとに ServerLoad へ記録する。
    /// つなぎっぱなしのもの（ハブの WebSocket・信号制御盤の text/event-stream）は、待っている時間が長いだけなので数えない。
    /// 静的ファイルは "GET (ファイル)" にまとめる。
    /// </summary>
    public static void UseLoadTiming(this WebApplication app, ServerLoad load, ResponseCache cache)
    {
        app.Use(async (ctx, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            await next(ctx);

            // 操作（GET 以外）のあとは、使い回している答えを捨てる（◆をずらしたのが、次に読んだときにすぐ見えるように）
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                cache.Clear();
                ctx.RequestServices.GetRequiredService<RouteEngine>().ClearViews(); // 作り置き（RouteEngine.View）も捨てる
            }

            if (ctx.WebSockets.IsWebSocketRequest) return;
            if (ctx.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true) return;
            var route = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (route != null && !route.StartsWith('/')) route = "/" + route;
            load.RecordWork($"{ctx.Request.Method} {route ?? "(ファイル)"}", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        });
    }
}
