using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>催物用臨停設定（司令用管理画面）。駅・種別・向き・時間帯で、まとめて臨時停車にする。</summary>
public static class EventStopEndpoints
{
    /// <param name="Station">駅ID。</param>
    /// <param name="Kind">臨時停車させるいちばん上の種別（特急・快急・急行・区急・準急）。それより下の種別（準急まで）も止まる。</param>
    /// <param name="Dir">"上り"・"下り"・"上下"（省くと上下）。</param>
    /// <param name="From">開始時刻 "HH:mm"。</param>
    /// <param name="To">終了時刻 "HH:mm"（空なら終わりなし）。</param>
    public sealed record EventStopRequest(string? Station, string? Kind, string? Dir, string? From, string? To);

    public sealed record EventStopRemoveRequest(long Id);

    /// <summary>司令用管理画面・管理画面・司令員の画面のグループ（認証はグループのもの）に足す。</summary>
    public static void MapEventStops(this RouteGroupBuilder g)
    {
        // 一覧と、選べる駅（信号場を除く路線の駅）・種別
        g.MapGet("/event-stops", (EventStops events, RailData rail) => Results.Ok(new
        {
            rules = events.List().Select(r => new
            {
                id = r.Id, station = r.Station, stationName = r.StationName, kind = r.Kind, kindLabel = EventStops.KindLabel(r.Kind),
                dir = r.Dir?.Label() ?? "上下", from = EventStops.FormatTime(r.FromSec), to = r.ToSec is { } t ? EventStops.FormatTime(t) : null
            }),
            stations = rail.Line.StationIds.Where(id => !rail.Line.StationName(id).Contains("信号場"))
                .Select(id => new { id, name = rail.Line.StationName(id) }),
            kinds = EventStops.KindOrder.Select(k => new { kind = k, label = EventStops.KindLabel(k) }),
            dwellSeconds = EventStops.DwellSeconds
        }));

        g.MapPost("/event-stops/add", (EventStopRequest req, EventStops events, RailData rail, ResponseCache cache) =>
        {
            var station = (req.Station ?? "").Trim();
            if (!rail.Line.StationIds.Contains(station)) return Results.BadRequest(new { message = "駅を選んでください" });
            var kind = (req.Kind ?? "").Trim();
            if (!EventStops.KindOrder.Contains(kind)) return Results.BadRequest(new { message = "種別を選んでください" });
            if (req.Dir is not (null or "" or "上り" or "下り" or "上下")) return Results.BadRequest(new { message = "上り・下り・上下から選んでください" });
            Dir? dir = req.Dir switch { "上り" => Dir.Up, "下り" => Dir.Down, _ => null };
            if (EventStops.ParseTime(req.From) is not { } from) return Results.BadRequest(new { message = "開始時刻を「11:00」のように入れてください" });
            int? to = null;
            if (!string.IsNullOrWhiteSpace(req.To))
            {
                if (EventStops.ParseTime(req.To) is not { } t) return Results.BadRequest(new { message = "終了時刻を「18:00」のように入れてください（終わりなしなら空）" });
                if (t <= from) return Results.BadRequest(new { message = "終了時刻は開始時刻より後にしてください" });
                to = t;
            }
            var rule = events.Add(station, rail.Line.StationName(station), kind, dir, from, to);
            cache.Clear();
            return Results.Ok(new { ok = true, id = rule.Id });
        });

        g.MapPost("/event-stops/remove", (EventStopRemoveRequest req, EventStops events, ResponseCache cache) =>
        {
            if (!events.Remove(req.Id)) return Results.NotFound(new { message = "その設定はもうありません" });
            cache.Clear();
            return Results.Ok(new { ok = true });
        });
    }
}
