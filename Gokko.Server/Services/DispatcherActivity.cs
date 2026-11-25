using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>
/// 司令員の操作を覚える（CrewRoster.Touch）。DispatcherIdleLimit のあいだ操作がなければ、司令員は自動で退勤になる（退勤忘れ）。
/// ・画面: 合い札（X-Crew-Token）つきの、GET 以外の呼び出し（司令用管理画面・ダイヤグラムの操作など）。読みに来るだけ（GET）は数えない
/// ・クライアント: 司令員として参加している接続からのハブの呼び出し（列車無線の応答・一斉通告・区間防護など）。定期の報告は数えない
/// </summary>
public sealed class DispatcherActivityHubFilter(TrainRadio radio, CrewRoster crew) : IHubFilter
{
    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext ctx, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (ctx.HubMethodName != Gokko.Common.Protocol.Report && radio.DispatcherCrew(ctx.Context.ConnectionId) is { } id)
            crew.Touch(id);
        return next(ctx);
    }
}

public static class DispatcherActivityExtensions
{
    public static void UseDispatcherActivity(this WebApplication app, CrewRoster crew)
    {
        app.Use(async (ctx, next) =>
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
                && ctx.Request.Headers["X-Crew-Token"].ToString() is { Length: > 0 } token)
                crew.Touch(crew.UserOfToken(token));
            await next(ctx);
        });
    }
}
