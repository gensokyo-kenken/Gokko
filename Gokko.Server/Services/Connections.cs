using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// ダイヤ運転の接続待ち（例: 大道寺で、東井行の普通が、遅れている大道寺行の準急の到着を待ってから出る）。
/// 組はダイヤ（Timetable.json）の注記「(接)」から作る。「(連)」（連絡）は待たない。
///   ・待つ列車の注記「(接)準道②43着」: この列車は、その駅に 43分に着く準急大道寺行（2番線）を待つ
///   ・着く列車の注記「(接)普東①45発」: 45分に出る普通東井行（1番線）が、この列車を待つ
/// 同じ組が両側に書かれていれば1つにまとめる。進路を扱う駅（Stations.json）の組だけ。
/// 司令用管理画面から、組ごとに接続を切れる（切った組は待たない。再起動で戻る）。
/// </summary>
public sealed class ConnectionTable(RailData data)
{
    /// <summary>接続の組。時刻は運転日の秒（ダイヤの時刻）。</summary>
    public sealed record Connection(string StationId, string StationName, string Waiter, string Feeder,
        double WaiterDep, double FeederArr, int? WaiterTrack, int? FeederTrack)
    {
        public string Key => $"{StationId}|{Waiter}|{Feeder}";
    }

    private readonly object _lock = new();
    private readonly HashSet<string> _cut = new();
    private List<Connection>? _all;
    private Dictionary<(string Station, string Waiter), List<Connection>>? _byWaiter;

    // (接)準道②43着 → 種別の頭文字・行先の略・番線（あれば）・分・着/発
    private static readonly Regex NoteRx = new(@"\(接\)(?<t>\D)(?<d>\D*?)(?<tr>[①-⑳])?(?<mm>\d{1,2})(?<k>[発着])", RegexOptions.Compiled);

    public IReadOnlyList<Connection> All { get { Build(); return _all!; } }

    /// <summary>その駅で、この列車が待つ接続（切った組を含む）。</summary>
    public IReadOnlyList<Connection> ForWaiter(string stationId, string waiter)
    {
        Build();
        return _byWaiter!.TryGetValue((stationId, waiter), out var l) ? l : Array.Empty<Connection>();
    }

    public bool IsCut(Connection c) { lock (_lock) return _cut.Contains(c.Key); }

    public bool SetCut(string key, bool cut)
    {
        if (All.All(c => c.Key != key)) return false;
        lock (_lock) { if (cut) _cut.Add(key); else _cut.Remove(key); }
        return true;
    }

    private void Build()
    {
        if (_all != null) return;
        lock (_lock)
        {
            if (_all != null) return;
            var list = new List<Connection>();
            var keys = new HashSet<string>();
            var trains = data.Dia.Values.ToList();
            foreach (var t in trains)
                foreach (var s in t.Stops)
                {
                    if (s.StationId is not { } sid || !data.Stations.ContainsKey(sid) || string.IsNullOrEmpty(s.Note)) continue;
                    foreach (Match m in NoteRx.Matches(s.Note))
                    {
                        var arrive = m.Groups["k"].Value == "着"; // 相手が着く（この列車が待つ）
                        var mine = arrive ? Sec(s.Dep) : Sec(s.Arr) ?? Sec(s.Dep);
                        if (mine == null) continue;
                        var other = Find(trains, t, sid, m, arrive, mine.Value);
                        if (other == null) continue;
                        var os = other.Value.Stop;
                        var c = arrive
                            ? new Connection(sid, s.Name, t.Number, other.Value.Train.Number, mine.Value, other.Value.T, s.Track, os.Track)
                            : new Connection(sid, s.Name, other.Value.Train.Number, t.Number, other.Value.T, mine.Value, os.Track, s.Track);
                        if (c.FeederArr > c.WaiterDep || c.Waiter == c.Feeder || !keys.Add(c.Key)) continue;
                        list.Add(c);
                    }
                }

            _byWaiter = list.GroupBy(c => (c.StationId, c.Waiter)).ToDictionary(g => g.Key, g => g.ToList());
            _all = list.OrderBy(c => c.WaiterDep).ToList();
        }
    }

    /// <summary>注記の相手の列車（種別の頭文字・番線・分が合い、行先の略が終点の名前に入っているものを優先し、時刻が近いもの。40分以内）。</summary>
    private static (DiaTrain Train, DiaStop Stop, double T)? Find(List<DiaTrain> trains, DiaTrain self, string sid, Match m, bool arrive, double mine)
    {
        var type = m.Groups["t"].Value;
        var dest = m.Groups["d"].Value;
        int? track = m.Groups["tr"].Success ? m.Groups["tr"].Value[0] - '①' + 1 : null;
        var mm = int.Parse(m.Groups["mm"].Value);
        return trains.Where(o => o != self && o.Type.StartsWith(type, StringComparison.Ordinal))
            .Select(o => (Train: o, Stop: o.Stops.FirstOrDefault(x => x.StationId == sid)))
            .Where(x => x.Stop != null && (track == null || x.Stop.Track == track))
            .Select(x => (x.Train, x.Stop!, T: (arrive ? Sec(x.Stop!.Arr) ?? Sec(x.Stop!.Dep) : Sec(x.Stop!.Dep)) ?? -1))
            .Where(x => x.T >= 0 && (int)(x.T / 60) % 60 == mm && Math.Abs(x.T - mine) <= 40 * 60)
            .OrderBy(x => dest.Length > 0 && x.Train.Stops.LastOrDefault()?.Name.Contains(dest) == true ? 0 : 1)
            .ThenBy(x => Math.Abs(x.T - mine))
            .Select(x => ((DiaTrain, DiaStop, double)?)x)
            .FirstOrDefault();
    }

    private static double? Sec(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var p = text.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var hh) || !int.TryParse(p[1], out var mm)) return null;
        var ss = p.Length > 2 && int.TryParse(p[2], out var x) ? x : 0;
        return hh * 3600 + mm * 60 + ss;
    }
}
