namespace Gokko.Server.Services;

/// <summary>
/// 勤怠管理の「行路のダイヤ」と「割り当てのない列番」。範囲は館浜〜赤山町（いまの行路表で組んでいる区間）。
/// ・行路ごとに、乗る列車の区間を駅・時刻の点の並びにする（ダイヤグラムの線）。
/// ・行路表の時間（いちばん早い始まり〜いちばん遅い終わり）の中で範囲を走りきる列車のうち、どの行路にも入っていない列車を「行路のない列車」にする。
/// </summary>
public static class DutyDiagram
{
    public const string StartName = "館浜";
    public const string EndName = "赤山町";

    public sealed record Line(string Train, List<int[]> Points);
    public sealed record Uncovered(string Train, string Type, string From, string To, int Dep, int Arr, List<int[]> Points);

    public static int? Sec(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = text.Trim().Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var m)) return null;
        return h * 3600 + m * 60 + (p.Length > 2 && int.TryParse(p[2], out var s) ? s : 0);
    }

    /// <summary>範囲の駅（館浜〜赤山町。線区の並び）。</summary>
    public static List<string> RangeIds(RailData rail)
    {
        var ids = rail.Line.StationIds.ToList();
        var a = ids.FindIndex(x => rail.Line.StationName(x) == StartName);
        var b = ids.FindIndex(x => rail.Line.StationName(x) == EndName);
        if (a < 0 || b < 0) return ids;
        return ids.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).ToList();
    }

    /// <summary>列車の、from〜to（駅名。null なら範囲の端まで）の点（[駅の段, 秒]）。範囲の外の駅は入れない。</summary>
    public static List<int[]> PointsOf(DiaTrain d, Dictionary<string, int> row, string? from, string? to)
    {
        var stops = d.Stops;
        var i0 = from != null ? stops.FindIndex(s => s.Name == from) : -1;
        var i1 = to != null ? stops.FindLastIndex(s => s.Name == to) : -1;
        if (i0 < 0) i0 = 0;
        if (i1 < 0) i1 = stops.Count - 1;
        var pts = new List<int[]>();
        for (var i = i0; i <= i1 && i < stops.Count; i++)
        {
            var s = stops[i];
            if (s.StationId == null || !row.TryGetValue(s.StationId, out var r)) continue;
            if (Sec(s.Arr) is { } a) pts.Add([r, a]);
            if (Sec(s.Dep) is { } b && b != Sec(s.Arr)) pts.Add([r, b]);
        }

        // 日をまたぐ列車は、前の点より小さくなった時刻に24時間を足す
        for (var k = 1; k < pts.Count; k++)
            if (pts[k][1] < pts[k - 1][1] - 12 * 3600) pts[k][1] += 24 * 3600;
        return pts;
    }

    public static object Build(RailData rail, DutyRoster roster, Func<string, string?> userOf, Func<string, List<string>> extrasOf)
    {
        var range = RangeIds(rail);
        var row = range.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var inDuty = new HashSet<string>();
        var duties = roster.Duties.Select(d =>
        {
            var lines = d.Items.Where(x => x.Kind != "休憩" && rail.DiaOf(x.Train) != null).Select(x =>
            {
                inDuty.Add(x.Train);
                return new Line(x.Train, PointsOf(rail.DiaOf(x.Train)!, row, x.From, x.To));
            }).Where(l => l.Points.Count > 0).ToList();
            return new { id = d.Id, user = userOf(d.Id), extras = extrasOf(d.Id), lines };
        }).Where(d => d.lines.Count > 0).ToList();

        var all = duties.SelectMany(d => d.lines).SelectMany(l => l.Points).Select(p => p[1]).ToList();
        int? from = all.Count > 0 ? all.Min() : null, to = all.Count > 0 ? all.Max() : null;

        // 行路のない列車（行路表の時間に範囲を走るもの）
        var uncovered = new List<Uncovered>();
        if (from != null)
            foreach (var d in rail.Dia.Values)
            {
                if (inDuty.Contains(d.Number)) continue;
                var pts = PointsOf(d, row, null, null);
                if (pts.Count < 2) continue;
                if (pts[0][1] < from || pts[^1][1] > to)
                {
                    // 0時をまたいだ時間で比べる
                    if (!(pts[0][1] + 86400 >= from && pts[^1][1] + 86400 <= to)) continue;
                    foreach (var p in pts) p[1] += 86400;
                }

                var first = d.Stops.First(s => s.StationId != null && row.ContainsKey(s.StationId));
                var last = d.Stops.Last(s => s.StationId != null && row.ContainsKey(s.StationId));
                uncovered.Add(new Uncovered(d.Number, d.Type, first.Name, last.Name, pts[0][1], pts[^1][1], pts));
            }

        return new
        {
            stations = range.Select(id => rail.Line.StationName(id)).ToList(),
            from, to,
            duties,
            uncovered = uncovered.OrderBy(x => x.Dep).ToList()
        };
    }
}
