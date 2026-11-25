using System.Text;
using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// 特発臨（運転整理のために、司令員がその場で立てる臨時列車）。司令用管理画面で立てる。
///
/// ・いま駅の番線にいる列車（自動運転の列車でもユーザーの列車でも）を選び、その駅・番線から始まるスジ（列番・種別・停車駅・時刻）を立てる。
///   時刻は、標準運転時分（RunTimes.json）から引いた案を、司令員が直してから立てる
/// ・立てたスジは、所定のダイヤに重ねる（<see cref="RailData.SetExtraTrains"/>、出どころ「特発臨」）。自動進路構成・運転順序・ダイヤグラム・
///   自動運転の列車からは、ふつうのダイヤの列車に見える
/// ・元の列車は、その駅で打ち切りにする（列車別設定の「打ち切り」と同じ指示）。元の列車の後運用は、時刻表どおりに残す。
///   「元の列車の後運用につなぐ」を選んだときは、後運用の差し替え（<see cref="TurnChanges"/>）で特発臨の終点からつなぐ
/// ・自動運転の列車は、その駅に止まったら特発臨の列番に変わって走る（<see cref="AiTrains"/>）。ユーザーの列車は、タブレットに特発臨が出るので、
///   運転士が列番を入れ直す
/// ・取り消せる。走り出す前なら、元の列車の打ち切りも外す。走り出したあとに取り消すと、自動運転の列車は消える
/// ・残し方は後運用の差し替えと同じ: メモリにだけ持ち、サーバーを再起動したとき・ロープレ時刻を設定したときに消える
/// </summary>
public sealed class TokuhatsuRinji(RailData rail, CentralControl central, PlayerRegistry players, GameClock clock)
{
    public const string Source = "特発臨";

    /// <summary>種別（列番の読み方と合わせる。<see cref="RouteEngine.TrainInfo"/>）。</summary>
    public static readonly string[] Types = ["普通", "準急", "区急", "急行", "快急", "特急", "回送"];

    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();

    private sealed class Entry
    {
        public required DiaTrain Dia { get; init; }
        /// <summary>元の列車の列番。</summary>
        public required string From { get; init; }
        public required string StationId { get; init; }
        public required int Track { get; init; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        /// <summary>特発臨の列番の列車が出てきた（自動運転の列車が列番を変えた・運転士が列番を入れ直した）。</summary>
        public bool Started { get; set; }
        /// <summary>元の列車の後運用を、特発臨の終点からつないだ。</summary>
        public bool TurnLinked { get; set; }
    }

    public sealed record StopSpec(string St, string? Stop, string? Arr, string? Dep, int? Track);

    /// <summary>立てる・案を引くときの入力。Stops があれば、それをそのまま立てる。なければ Dest・Dep・StopAt から案を引く。</summary>
    public sealed record Request(string? Train, string? Number, string? Type, string? Dest, string? Dep, List<string>? StopAt, List<StopSpec>? Stops, bool LinkTurn);

    public event Action<string>? Changed;

    /// <summary>取り消した特発臨のうち、もう走り出していた列番（自動運転の列車を消すため）。</summary>
    public event Action<IReadOnlyCollection<string>>? Removed;

    // =========================================================================================
    // 自動運転の列車・タブレットから
    // =========================================================================================

    /// <summary>その列車に、まだ走り出していない特発臨があれば、(特発臨の列番, 駅ID, 番線)。</summary>
    public (string Number, string StationId, int Track)? PendingFor(string train)
    {
        var key = RailData.NormalizeTrain(train);
        lock (_lock)
            return _entries.FirstOrDefault(e => !e.Started && e.From == key) is { } e ? (e.Dia.Number, e.StationId, e.Track) : null;
    }

    /// <summary>特発臨の列番か（自動運転の列車を時刻表から出すときは、特発臨は出さない。元の列車が列番を変えて走る）。</summary>
    public bool IsRinji(string train)
    {
        var key = RailData.NormalizeTrain(train);
        lock (_lock) return _entries.Any(e => e.Dia.Number == key);
    }

    /// <summary>特発臨の列番の列車が出てきた。</summary>
    public void MarkStarted(string number)
    {
        var key = RailData.NormalizeTrain(number);
        lock (_lock)
            foreach (var e in _entries.Where(e => e.Dia.Number == key)) e.Started = true;
    }

    /// <summary>タブレットに出す特発臨（その列車がまだ走り出していない特発臨の元の列車なら）。</summary>
    public object? TabletNotice(string train)
    {
        var key = RailData.NormalizeTrain(train);
        lock (_lock)
        {
            if (_entries.FirstOrDefault(e => !e.Started && e.From == key) is not { } e) return null;
            var first = e.Dia.Stops[0];
            var last = e.Dia.Stops[^1];
            return new
            {
                number = e.Dia.Number, type = e.Dia.Type, from = e.From, station = first.Name, track = e.Track,
                dep = first.Dep, dest = last.Name, arr = last.Arr ?? last.Dep
            };
        }
    }

    // =========================================================================================
    // 司令用管理画面
    // =========================================================================================

    /// <summary>いま駅の番線に止まっている列車（特発臨を立てられる列車）。駅を出ていくところ・入ってくるところの列車は、止まるまで出さない。</summary>
    public List<object> Candidates()
    {
        var list = new List<(string Key, object Row)>();
        foreach (var p in players.Snapshot())
        {
            if (p.TrainNumber.Length == 0 || p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix)) continue;
            if (p.Speed > 1 || Where(p) is not { } w) continue;
            var number = RailData.NormalizeTrain(p.TrainNumber);
            var dia = rail.DiaOf(number);
            list.Add((number, new
            {
                train = number,
                station = w.Station.Id,
                stationName = w.Station.Name,
                track = w.Track,
                ai = p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix),
                speed = Math.Round(p.Speed),
                dir = RouteEngine.TrainInfo(number)?.Dir == Dir.Down ? "下り" : "上り",
                type = dia?.Type ?? RouteEngine.TrainInfo(number)?.Type,
                dest = dia?.Dest,
                rinji = PendingFor(number)?.Number
            }));
        }
        return list.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Row).ToList();
    }

    /// <summary>列車がいる駅・番線（先頭がいる番線の回路で見る）。</summary>
    private (StationDef Station, int Track)? Where(PlayerInfo p)
    {
        foreach (var st in rail.ControlStations)
        foreach (var (track, circuit) in st.TrackCircuit)
            if (p.TrackCircuits.Contains(circuit)) return (st, track);
        return null;
    }

    /// <summary>立てた特発臨の一覧。</summary>
    public List<object> View()
    {
        RefreshStarted();
        lock (_lock)
            return _entries.Select(e => (object)new
            {
                number = e.Dia.Number,
                type = e.Dia.Type,
                from = e.From,
                station = rail.Line.StationName(e.StationId),
                track = e.Track,
                dest = e.Dia.Dest,
                dep = e.Dia.Stops[0].Dep,
                arr = e.Dia.Stops[^1].Arr ?? e.Dia.Stops[^1].Dep,
                started = e.Started,
                turn = rail.Turns.TurnOf(e.Dia)?.Number,
                stops = e.Dia.Stops.Select(s => new { name = s.Name, stop = s.Stop, arr = s.Arr, dep = s.Dep, track = s.Track })
            }).ToList();
    }

    // 特発臨の列番の列車が出ていれば、走り出した印を付ける
    private void RefreshStarted()
    {
        var running = players.Snapshot().Select(p => RailData.NormalizeTrain(p.TrainNumber)).ToHashSet();
        lock (_lock)
            foreach (var e in _entries.Where(e => !e.Started && running.Contains(e.Dia.Number))) e.Started = true;
    }

    /// <summary>
    /// スジの案を引く: 元の列車のいる駅・番線から行先まで、路線の駅を並べ、標準運転時分で時刻を入れる。
    /// 停車駅は StopAt（なければ種別がふだん止まる駅）。発車時刻は Dep（なければ今のロープレ時刻の3分後）。
    /// </summary>
    public (object? Plan, string? Error) Draft(Request r)
    {
        var (from, error) = Origin(r.Train);
        if (from is not { } f) return (null, error);
        var type = (r.Type ?? "").Trim();
        if (!Types.Contains(type)) return (null, "種別を選んでください");
        var destZone = r.Dest is { Length: > 0 } dz ? rail.Line.ZoneOfStation(dz) : -1;
        var hereZone = rail.Line.ZoneOfStation(f.Station.Id);
        if (destZone < 0) return (null, "行先の駅を選んでください");
        if (destZone == hereZone) return (null, "行先が今いる駅です");
        var dir = destZone > hereZone ? Dir.Down : Dir.Up;

        double dep;
        if (r.Dep is { Length: > 0 } dt)
        {
            if (Sec(dt) is not { } d) return (null, "発車時刻の形が違います（例 14:20 か 14:20:30）");
            dep = d;
        }
        else
        {
            if (clock.Now is not { } now) return (null, "ロープレ時刻が設定されていません（発車時刻を入れてください）");
            dep = Math.Ceiling((now.TotalSeconds + 180) / 30) * 30;
        }

        // 行先までの路線の駅（ゾーンの順）
        var ids = rail.Line.StationIds.Where(id => rail.Line.ZoneOfStation(id) is var z && z >= 0 && z >= Math.Min(hereZone, destZone) && z <= Math.Max(hereZone, destZone))
            .OrderBy(id => rail.Line.ZoneOfStation(id) * dir.Step()).ToList();
        var stopAt = r.StopAt?.ToHashSet() ?? UsualStops(type);
        var stops = new List<StopSpec>();
        var t = dep;
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var first = i == 0;
            var last = i == ids.Count - 1;
            var stop = first || last || stopAt.Contains(id);
            if (first)
            {
                stops.Add(new StopSpec(id, "停", null, Hms(t), f.Track));
                continue;
            }

            var prevStops = stops[^1].Stop == "停";
            t += RunSeconds(type, dir, ids[i - 1], id, prevStops, stop);
            var arr = Math.Ceiling(t / 15) * 15;
            if (last) stops.Add(new StopSpec(id, "停", Hms(arr), null, null));
            else if (stop)
            {
                t = Math.Ceiling((arr + Math.Max(rail.DefaultDwellSeconds, 20)) / 15) * 15;
                stops.Add(new StopSpec(id, "停", Hms(arr), Hms(t), null));
            }
            else
            {
                t = arr;
                stops.Add(new StopSpec(id, "通", null, Hms(arr), null));
            }
        }

        return (new
        {
            train = f.Train,
            station = f.Station.Id,
            stationName = f.Station.Name,
            track = f.Track,
            dir = dir.Label(),
            type,
            stops = stops.Select(s => new
            {
                st = s.St, name = rail.Line.StationName(s.St), stop = s.Stop, arr = s.Arr, dep = s.Dep, track = s.Track,
                control = rail.Stations.TryGetValue(s.St, out var sd) && sd.RouteControl
            })
        }, null);
    }

    /// <summary>特発臨を立てる。うまくいかなければ理由を返す。</summary>
    public string? Create(Request r)
    {
        var (from, error) = Origin(r.Train);
        if (from is not { } f) return error;
        var number = RailData.NormalizeTrain(r.Number);
        var type = (r.Type ?? "").Trim();
        if (number.Length == 0) return "列番を入れてください";
        if (!Types.Contains(type)) return "種別を選んでください";
        if (rail.DiaOf(number) != null) return $"{number} はもう時刻表にある列番です";
        if (RouteEngine.TrainInfo(number) is not { } info) return $"{number}: 列番に数字がありません";
        if (info.Type != type)
            return type == "回送" ? $"{number}: 回送の列番は「回」で始めてください"
                : type == "普通" ? $"{number}: 普通の列番は、数字のあとに種別の文字（A・B・C・D・K）を付けないでください"
                : $"{number}: {type}の列番は、末尾を {Suffix(type)} にしてください";

        var stops = r.Stops ?? new();
        if (stops.Count < 2) return "駅を2つ以上入れてください";
        if (stops[0].St != f.Station.Id) return $"発駅は、元の列車がいる {f.Station.Name} にしてください";
        var zones = stops.Select(s => rail.Line.ZoneOfStation(s.St)).ToList();
        if (zones.Any(z => z < 0)) return "路線にない駅があります";
        for (var i = 1; i < zones.Count; i++)
            if ((zones[i] - zones[i - 1]) * info.Dir.Step() <= 0)
                return $"{number} は列番の数字の最後が{(info.Dir == Dir.Down ? "奇数（下り）" : "偶数（上り）")}ですが、駅の並びが{info.Dir.Label()}になっていません";

        // 時刻: 発駅は発、終点は着、途中の停車駅は着・発、通過駅は通過の時刻（発の欄）。前の駅より前にならないこと
        var dia = new DiaTrain { Number = number, Type = type, TypeLabel = type, Dest = rail.Line.StationName(stops[^1].St) };
        double? prev = null;
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            var name = rail.Line.StationName(s.St);
            var first = i == 0;
            var last = i == stops.Count - 1;
            var stop = first || last || s.Stop == "停" ? "停" : "通";
            var arr = first || stop == "通" ? null : NormTime(s.Arr);
            var dep = last ? null : NormTime(s.Dep);
            if (!first && stop == "停" && s.Arr is { Length: > 0 } && arr == null || !last && s.Dep is { Length: > 0 } && dep == null)
                return $"{name}: 時刻の形が違います（例 14:20 か 14:20:30）";
            if (first && dep == null) return $"{name}: 発車時刻を入れてください";
            if (last && arr == null) return $"{name}: 着く時刻を入れてください";
            if (!first && !last && dep == null) return $"{name}: {(stop == "停" ? "発" : "通過")}の時刻を入れてください";
            foreach (var v in new[] { Sec(arr), Sec(dep) })
            {
                if (v == null) continue;
                var vv = prev is { } p0 && v < p0 - 12 * 3600 ? v + 86400 : v; // 0時をまたぐ
                if (prev != null && vv < prev) return $"{name}: 前の駅より早い時刻になっています";
                prev = vv;
            }

            var track = first ? f.Track : s.Track;
            if (track is { } tk && rail.Stations.TryGetValue(s.St, out var sd) && sd.RouteControl && !sd.TrackCircuit.ContainsKey(tk))
                return $"{name}: {tk}番線はありません";
            dia.Stops.Add(new DiaStop(name, s.St, stop, arr, dep, track));
            dia.Stations[s.St] = (stop == "停" ? "停車" : "通過", track);
        }

        var lastId = stops[^1].St;
        if (rail.Stations.TryGetValue(lastId, out var ts) && ts.RouteControl) dia.TerminalId = lastId;
        dia.Unyo = rail.DiaOf(f.Train) is { } od ? rail.Turns.UnyoOf(od) : null;

        lock (_lock)
        {
            if (_entries.Any(e => e.Dia.Number == number)) return $"{number} はもう特発臨で立てています";
            if (_entries.Any(e => !e.Started && e.From == f.Train)) return $"{f.Train} には、まだ走り出していない特発臨があります（先に取り消してください）";
            _entries.Add(new Entry { Dia = dia, From = f.Train, StationId = f.Station.Id, Track = f.Track });
            var skipped = ApplyLayer();
            if (skipped.Contains(number))
            {
                _entries.RemoveAll(e => e.Dia.Number == number);
                ApplyLayer();
                return $"{number} はほかの臨時列車の列番とかぶっています";
            }
        }

        // 元の列車は、その駅で打ち切り
        central.SetStop(f.Train, f.Station.Id, "打ち切り");

        // 元の列車の後運用につなぐ（後運用の差し替え。元の列車の後運用は「なし」になる）
        string? turnNote = null;
        if (r.LinkTurn && rail.DiaOf(f.Train) is { } orig && rail.Turns.TurnOf(orig) is { Kind: "折返", Number: { Length: > 0 } next })
        {
            var err = rail.Turns.Set(number, next);
            if (err == null)
            {
                lock (_lock) _entries.First(e => e.Dia.Number == number).TurnLinked = true;
                turnNote = $"・後運用 {next}";
            }
            else turnNote = $"・後運用はつなげませんでした（{err}）";
        }

        Changed?.Invoke($"特発臨: {number}（{type}）{f.Station.Name} {f.Track}番線 {dia.Stops[0].Dep}発 → {dia.Dest} {dia.Stops[^1].Arr}着。{f.Train} は {f.Station.Name} で打ち切り{turnNote}");
        return null;
    }

    /// <summary>特発臨を取り消す。うまくいかなければ理由を返す。</summary>
    public string? Cancel(string? number)
    {
        RefreshStarted();
        var key = RailData.NormalizeTrain(number);
        Entry? e;
        lock (_lock)
        {
            e = _entries.FirstOrDefault(x => x.Dia.Number == key);
            if (e == null) return $"{key} の特発臨はありません";
        }

        // 後運用の差し替えを先に戻す（特発臨をダイヤから外す前に）
        if (e.TurnLinked) rail.Turns.Cancel(key);
        lock (_lock)
        {
            _entries.Remove(e);
            ApplyLayer();
        }

        if (!e.Started && central.StopOverride(e.From, e.StationId) == "打ち切り") central.SetStop(e.From, e.StationId, null);
        if (e.Started) Removed?.Invoke([key]);
        Changed?.Invoke(e.Started
            ? $"特発臨を取消: {key}（走り出していたので、自動運転の列車なら消します）"
            : $"特発臨を取消: {key}（{e.From} の {rail.Line.StationName(e.StationId)} での打ち切りも外します）");
        return null;
    }

    /// <summary>すべて取り消す（ロープレ時刻を設定したとき。打ち切り・後運用の差し替えは、それぞれのリセットで消える）。取り消した数を返す。</summary>
    public int Reset()
    {
        List<string> started;
        int n;
        lock (_lock)
        {
            n = _entries.Count;
            if (n == 0) return 0;
            started = _entries.Where(e => e.Started).Select(e => e.Dia.Number).ToList();
            _entries.Clear();
            ApplyLayer();
        }
        if (started.Count > 0) Removed?.Invoke(started);
        return n;
    }

    // _lock の中で呼ぶ
    private List<string> ApplyLayer() => rail.SetExtraTrains(Source, _entries.Select(e => e.Dia).ToList());

    // =========================================================================================
    // 下ごしらえ
    // =========================================================================================

    /// <summary>元の列車と、いる駅・番線。いなければ理由。</summary>
    private ((string Train, StationDef Station, int Track)? From, string? Error) Origin(string? train)
    {
        var key = RailData.NormalizeTrain(train);
        if (key.Length == 0) return (null, "元の列車を選んでください");
        var p = players.Snapshot().FirstOrDefault(x => RailData.NormalizeTrain(x.TrainNumber) == key && !x.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix));
        if (p == null) return (null, $"{key} は走っていません");
        if (Where(p) is not { } w) return (null, $"{key} は駅の番線にいません（特発臨は、駅の番線に止まっている列車から立てます）");
        if (p.Speed > 1) return (null, $"{key} は {w.Station.Name} で走っています（止まってから立ててください）");
        return ((key, w.Station, w.Track), null);
    }

    private static string Suffix(string type) => type switch { "特急" => "A", "急行" => "B", "準急" => "C", "区急" => "D", "快急" => "K", _ => "" };

    private Dictionary<string, HashSet<string>>? _usualStops;

    /// <summary>種別がふだん止まる駅: 所定のダイヤで、その種別の列車の1割以上が止まる駅。回送は止まらない。</summary>
    private HashSet<string> UsualStops(string type)
    {
        if (type == "回送") return new();
        _usualStops ??= rail.BaseDia.Values
            .SelectMany(t => t.Stops.Where(s => s.StationId != null).Select(s => (Id: s.StationId!, t.Type, Stop: s.Stop == "停")))
            .GroupBy(x => x.Type)
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Id).Where(s => s.Count(x => x.Stop) * 10 >= s.Count()).Select(s => s.Key).ToHashSet());
        return _usualStops.TryGetValue(type, out var set) ? set : new();
    }

    /// <summary>
    /// 隣の駅までの標準運転時分（秒）。同じ種別・向き・停車の組み合わせ → 同じ種別・向き → ほかの種別で停車の組み合わせが同じもの →
    /// ほかの種別で同じ区間、の順に探す。回送は特急の通過の時分を使う。見つからなければ 120秒。
    /// </summary>
    private int RunSeconds(string type, Dir dir, string fromId, string toId, bool fromStops, bool toStops)
    {
        var from = rail.Line.StationName(fromId);
        var to = rail.Line.StationName(toId);
        var rows = rail.RunTimes.Where(x => x.Dir == dir && x.From == from && x.To == to).ToList();
        if (rows.Count == 0) return 120;
        var t = type == "回送" ? "特急" : type;
        return (rows.FirstOrDefault(x => x.Type == t && x.FromStops == fromStops && x.ToStops == toStops)
            ?? rows.FirstOrDefault(x => x.FromStops == fromStops && x.ToStops == toStops && x.Type == "普通")
            ?? rows.FirstOrDefault(x => x.FromStops == fromStops && x.ToStops == toStops)
            ?? rows.FirstOrDefault(x => x.Type == t)
            ?? rows[0]).Seconds;
    }

    private static readonly Regex TimeRe = new(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?$");

    /// <summary>"H:mm" "HH:mm:ss" → "HH:mm:ss"。読めなければ null。</summary>
    private static string? NormTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = TimeRe.Match(s.Normalize(NormalizationForm.FormKC).Trim());
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value), se = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (h > 27 || mi > 59 || se > 59) return null;
        return $"{h % 24:00}:{mi:00}:{se:00}";
    }

    private static double? Sec(string? s)
    {
        if (NormTime(s) is not { } t) return null;
        var p = t.Split(':');
        return int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60 + int.Parse(p[2]);
    }

    private static string Hms(double sec)
    {
        var s = (int)Math.Round(sec) % 86400;
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
    }
}
