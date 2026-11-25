using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>
/// 運転課の画面（untenka.html）の窓口。催物用の臨時列車を作り、申請・承認する。
/// ヘッダー X-Crew-Token にログインの合い札（乗務タブレットと同じログイン）。ログインした乗務員なら誰でも作って申請でき、
/// 承認・差戻しは役職「運転課課長代理」の人だけ。適用・取消は司令用管理画面（/api/dia/temptrains。AdminEndpoints と同じ鍵）
/// </summary>
public static class UntenkaEndpoints
{
    public sealed record SaveRequest(string? Id, string? Name, List<TempTrains.TrainSpec>? Trains);
    public sealed record CheckRequest(string? Id, List<TempTrains.TrainSpec>? Trains);
    public sealed record ActRequest(string? Id, string? Action, string? Comment);

    public static void MapUntenkaEndpoints(this WebApplication app, RouteGroupBuilder dia)
    {
        var g = app.MapGroup("/api/untenka").AddEndpointFilter(async (ctx, next) =>
        {
            var crew = ctx.HttpContext.RequestServices.GetRequiredService<CrewRoster>();
            var token = ctx.HttpContext.Request.Headers["X-Crew-Token"].ToString();
            if (crew.UserOfToken(token) is null)
                return Results.Json(new { message = token.Length > 0 ? "ログインが切れました。もう一度ログインしてください" : "ログインしてください", login = true }, statusCode: 401);
            return await next(ctx);
        });

        static string Me(HttpContext ctx, CrewRoster crew) => crew.UserOfToken(ctx.Request.Headers["X-Crew-Token"].ToString())!;

        // 入力に使う決まりごと（駅・番線・種別・標準運転時分）。画面を開いたときに1回だけ読む
        g.MapGet("/base", (HttpContext ctx, CrewRoster crew, RailData rail, TempTrains temp) =>
        {
            var id = Me(ctx, crew);
            var line = rail.Line;
            var downInc = temp.DownIncreasing();
            // 駅ごとの番線: 所定のダイヤで使っている番線と、向き・停車/通過ごとにいちばん多い番線
            var uses = new List<(string St, string Key, int Track)>();
            foreach (var d in rail.BaseDia.Values)
            {
                if (RouteEngine.TrainInfo(d.Number) is not { } info) continue;
                foreach (var s in d.Stops)
                    if (s.StationId != null && s.Track is { } tr)
                        uses.Add((s.StationId, (info.Dir == Dir.Down ? "下" : "上") + (s.Stop == "通" ? "通" : "停"), tr));
            }
            var byStation = uses.ToLookup(u => u.St);
            var stations = line.StationIds.Where(s => line.ZoneOfStation(s) >= 0).Select(s => new
            {
                id = s,
                name = line.StationName(s),
                zone = line.ZoneOfStation(s),
                control = rail.Stations.TryGetValue(s, out var def) && def.RouteControl,
                tracks = byStation[s].Select(u => u.Track).Concat(def?.TrackCircuit.Keys ?? Enumerable.Empty<int>()).Distinct().Order().ToList(),
                def = byStation[s].GroupBy(u => u.Key).ToDictionary(x => x.Key, x => x.GroupBy(u => u.Track).OrderByDescending(y => y.Count()).First().Key)
            }).OrderBy(s => s.zone).ToList();
            var types = TempTrains.Types.Select(t => new
            {
                type = t.Type,
                suffix = t.Suffix,
                labels = rail.BaseDia.Values.Where(d => d.Type == t.Type && d.TypeLabel.Length > 0).GroupBy(d => d.TypeLabel)
                    .OrderByDescending(x => x.Count()).Select(x => x.Key).ToList()
            });
            var runTimes = rail.RunTimes.Select(r => new
            {
                type = r.Type, dir = r.Dir == Dir.Down ? "下り" : "上り", from = line.StationIdByName(r.From), to = line.StationIdByName(r.To),
                fromStop = r.FromStops, toStop = r.ToStops, seconds = r.Seconds
            }).Where(r => r.from != null && r.to != null);
            return Results.Ok(new
            {
                me = new { id, name = crew.InfoOf(id)?.Name ?? "", approver = crew.HasRole(id, CrewRoster.PlanningRole) },
                downIncreasing = downInc,
                stations, types, runTimes,
                unyoUsed = rail.BaseDia.Values.Select(d => d.Unyo).Where(u => u != null).Distinct().Order()
            });
        });

        // ダイヤグラムに薄く引く、所定の時刻表の線（所定のダイヤは変わらないので、最初に1回だけ作って使い回す）。
        // y はゾーン、時刻は "HH:mm:ss"。singles は単線区間（ゾーンの組）
        object? planCache = null;
        g.MapGet("/plan", (RailData rail) => Results.Ok(planCache ??= new
        {
            singles = rail.SingleTrackSections.Select(p => new[] { rail.Line.ZoneOfStation(p.Item1), rail.Line.ZoneOfStation(p.Item2) }).Where(z => z[0] >= 0 && z[1] >= 0).ToList(),
            trains = rail.BaseDia.Values.Select(t => new
            {
                train = t.Number,
                type = t.Type,
                stops = t.Stops.Where(s => s.StationId != null && rail.Line.ZoneOfStation(s.StationId) >= 0 && (s.Arr != null || s.Dep != null))
                    .Select(s => new { y = rail.Line.ZoneOfStation(s.StationId!), stop = s.Stop, arr = s.Arr, dep = s.Dep, track = s.Track }).ToList()
            }).Where(t => t.stops.Count >= 2).ToList()
        }));

        g.MapGet("/groups", (TempTrains temp) => Results.Ok(new { groups = temp.Groups().OrderBy(x => x.Id.Length).ThenBy(x => x.Id) }));

        g.MapPost("/check", (CheckRequest req, TempTrains temp) =>
        {
            var (errors, warnings) = temp.Check(req.Trains?.Select(Copy).ToList() ?? new(), req.Id);
            return Results.Ok(new { errors, warnings });
        });

        g.MapPost("/groups", (SaveRequest req, HttpContext ctx, CrewRoster crew, TempTrains temp) =>
        {
            var me = Me(ctx, crew);
            var (id, error, warnings) = temp.Save(req.Id, req.Name ?? "", req.Trains?.Select(Copy).ToList() ?? new(), me, crew.InfoOf(me)?.Name ?? "");
            return error != null ? Results.BadRequest(new { ok = false, message = error, warnings }) : Results.Ok(new { ok = true, id, warnings });
        });

        g.MapPost("/act", (ActRequest req, HttpContext ctx, CrewRoster crew, TempTrains temp, IncidentManager log) =>
        {
            var me = Me(ctx, crew);
            var name = temp.Get(req.Id)?.Name;
            var error = temp.Act(req.Id, req.Action ?? "", me, crew.HasRole(me, CrewRoster.PlanningRole), req.Comment);
            if (error != null) return Results.BadRequest(new { ok = false, message = error });
            var word = req.Action switch { "submit" => "申請", "withdraw" => "取り下げ", "approve" => "承認", "reject" => "差戻し", "delete" => "削除", _ => req.Action };
            log.Note($"運転課: 臨時列車「{name}」を{word}しました（ID:{me}）");
            return Results.Ok(new { ok = true });
        });

        // ---- 司令用管理画面: 承認したグループの適用・取消 ----
        dia.MapGet("/temptrains", (TempTrains temp) => Results.Ok(new
        {
            groups = temp.Groups().Where(x => x.Status == TempTrains.Approved || x.Applied).Select(x => new
            {
                id = x.Id, name = x.Name, applied = x.Applied, owner = x.OwnerName, approvedBy = x.ApprovedBy,
                trains = x.Trains.Select(t => new
                {
                    unyo = t.Unyo, number = t.Number, type = t.Type, dest = t.Dest,
                    from = t.Stops.FirstOrDefault()?.St is { } f ? temp.StationName(f) : "", dep = t.Stops.FirstOrDefault()?.Dep,
                    to = t.Stops.LastOrDefault()?.St is { } l ? temp.StationName(l) : "", arr = t.Stops.LastOrDefault()?.Arr ?? t.Stops.LastOrDefault()?.Dep
                })
            })
        }));
        dia.MapPost("/temptrains", (ActRequest req, TempTrains temp, IncidentManager log) =>
        {
            var on = req.Action != "cancel";
            var name = temp.Get(req.Id)?.Name;
            var error = temp.Apply(req.Id, on);
            if (error != null) return Results.BadRequest(new { ok = false, message = error });
            log.Note($"司令用管理画面: 催物用臨時列車「{name}」を{(on ? "適用" : "取消")}しました");
            return Results.Ok(new { ok = true });
        });
    }

    // 受け取った列車をそのまま持たない（保存するのは自分で作った物だけ）
    private static TempTrains.TrainSpec Copy(TempTrains.TrainSpec t) => new()
    {
        Unyo = t.Unyo, Number = t.Number ?? "", Type = t.Type ?? "", Label = t.Label ?? "", Dest = t.Dest ?? "",
        Stops = (t.Stops ?? new()).Take(80).Select(s => new TempTrains.StopSpec { St = s.St ?? "", Stop = s.Stop ?? "通", Arr = s.Arr, Dep = s.Dep, Track = s.Track }).ToList()
    };
}
