using Gokko.Common;
using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>
/// 乗務タブレット（tablet.html。スマホでも）から放送装置を扱う（車内・車外・連絡の入・切）。
/// ヘッダー X-Crew-Token（ログインの合い札）の人が、PC のクライアントで運転士・車掌として乗っている列車の放送装置を扱う。
/// 声は PC のマイクから送る（スマホはボタンだけ）。
/// </summary>
public static class OnboardEndpoints
{
    public sealed record OnboardPressRequest(string? Action);

    public static void MapOnboardEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/tid/onboard");

        // 今の状態（乗っていなければ aboard = false）
        g.MapGet("", (HttpRequest http, CrewRoster crew, OnboardAudio onboard) =>
        {
            if (crew.UserOfToken(http.Headers["X-Crew-Token"].ToString()) is not { } id)
                return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
            return Results.Ok(View(id, onboard));
        });

        // ボタンを押した（車内・車外・連絡のどれか。押すたびに入・切。どれか1つ）
        g.MapPost("", (HttpRequest http, OnboardPressRequest req, CrewRoster crew, OnboardAudio onboard) =>
        {
            if (crew.UserOfToken(http.Headers["X-Crew-Token"].ToString()) is not { } id)
                return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
            if (onboard.TalkerOf(id) is not { } conn)
                return Results.BadRequest(new { message = "PC のクライアントで、運転士か車掌として列車に乗ってから使ってください" });
            var result = onboard.Press(conn, req.Action ?? "");
            return Results.Ok(new { ok = result.Ok, message = result.Message, state = View(id, onboard) });
        });
    }

    private static object View(string crewId, OnboardAudio onboard)
    {
        if (onboard.TalkerOf(crewId) is not { } conn || onboard.ViewOf(conn) is not { } st)
            return new { aboard = false, message = "PC のクライアントで、運転士か車掌として列車に乗ると使えます" };
        var broadcaster = st.Members.FirstOrDefault(m => m.Id == st.BroadcasterId);
        return new
        {
            aboard = true,
            train = st.TrainNumber,
            role = st.Role == CrewRole.Conductor ? "車掌" : "運転士",
            inside = st.Broadcast == OnboardChannel.Inside && st.BroadcasterId == conn,
            outside = st.Broadcast == OnboardChannel.Outside && st.BroadcasterId == conn,
            intercom = st.Intercom,
            phoneMic = st.PhoneMic,
            otherBroadcast = st.Broadcast != OnboardChannel.None && st.BroadcasterId != conn
                ? $"{broadcaster?.Label ?? ""}が{(st.Broadcast == OnboardChannel.Outside ? "車外" : "車内")}放送を流しています" : "",
            members = st.Members.Select(m => m.Label + (m.Intercom ? "（連絡 入）" : "")).ToList()
        };
    }
}
