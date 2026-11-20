namespace Gokko.Server.Services;

/// <summary>
/// 後運用の差し替え（ダイヤ運転中に、遅れた列車を別の運用に折り返しでつなぐ）。
/// 列車 A の後運用（終点で折り返して次になる列車）を C に差し替えると、もともと C に折り返すはずだった列車 X が、
/// A のもとの後運用 B を受け持つ（2つの運用の、そこから先を入れ替える）。折返の時刻・仕方（発／入換）は後運用の列車のものなので、
/// A と X の「終点のあと」（<see cref="DiaTurn"/>）をそのまま入れ替える。C の前の列車がなければ、A を C につなぐだけ。
/// 運行番号も運用ごと入れ替わる（差し替えた先の列車とその先は、つないだ列車の運行番号になる）。
/// 時刻表（Timetable.json）は書き換えず、メモリに持つだけ（サーバーを再起動すると消える）。取消で元に戻す。
/// </summary>
public sealed class TurnChanges(RailData rail)
{
    private readonly object _lock = new();
    // 列番 → 差し替えた「終点のあと」（null は「なし」に差し替えた）
    private readonly Dictionary<string, DiaTurn?> _over = new();
    private readonly List<Change> _changes = new();
    private Dictionary<string, string>? _origPred;
    private Dictionary<string, string>? _pred;
    private Dictionary<string, string>? _nextPred;

    private sealed record Change(string A, string? X, DiaTurn? PrevA, DiaTurn? PrevX, DiaTurn? NewA, DiaTurn? NewX);

    /// <summary>差し替えるたびに増える（ダイヤグラムの計画の線を読み直す合図）。</summary>
    public int Version { get; private set; }

    public event Action<string>? Changed;

    /// <summary>その列車の「終点のあと」。差し替えていればそれ、なければ時刻表のもの。</summary>
    public DiaTurn? TurnOf(DiaTrain d)
    {
        lock (_lock) return _over.TryGetValue(d.Number, out var t) ? t : d.Turn;
    }

    public bool IsChanged(string train)
    {
        lock (_lock) return _over.ContainsKey(RailData.NormalizeTrain(train));
    }

    /// <summary>運行番号（差し替えたあとは、つないだ列車の運行番号を受け継ぐ）。</summary>
    public int? UnyoOf(DiaTrain d)
    {
        lock (_lock) return _over.Count == 0 ? d.Unyo : Unyo(d.Number, 0);
    }

    private int? Unyo(string number, int depth)
    {
        var d = rail.Dia.GetValueOrDefault(number);
        if (d == null) return null;
        if (depth < 60 && (Pred().TryGetValue(number, out var p) || NextPred().TryGetValue(number, out p)) && rail.Dia.TryGetValue(p, out var pd))
        {
            var pu = Unyo(p, depth + 1);
            if (_over.ContainsKey(p) || pu != pd.Unyo) return pu;
        }
        return d.Unyo;
    }

    // 時刻表の折返のつなぎ（後運用の列番 → 前の列車）
    private Dictionary<string, string> OrigPred()
    {
        if (_origPred != null) return _origPred;
        var m = new Dictionary<string, string>();
        foreach (var d in rail.Dia.Values)
            if (d.Turn is { Kind: "折返", Number: { } n }) m.TryAdd(n, d.Number);
        return _origPred = m;
    }

    // 差し替えを入れた折返のつなぎ
    private Dictionary<string, string> Pred()
    {
        if (_pred != null) return _pred;
        var m = new Dictionary<string, string>(OrigPred());
        foreach (var (a, _) in _over)
            if (rail.Dia.TryGetValue(a, out var d) && d.Turn is { Kind: "折返", Number: { } n } && m.GetValueOrDefault(n) == a) m.Remove(n);
        foreach (var (a, t) in _over)
            if (t is { Kind: "折返", Number: { } n }) m[n] = a;
        return _pred = m;
    }

    // 途中駅の列番の読み替え（読み替えたあとの列番 → 前の列番）
    private Dictionary<string, string> NextPred()
    {
        if (_nextPred != null) return _nextPred;
        var m = new Dictionary<string, string>();
        foreach (var d in rail.Dia.Values)
            if (d.Next is { } nx) m.TryAdd(nx.Number, d.Number);
        return _nextPred = m;
    }

    private static string? Terminal(DiaTrain d) => d.Stops.Count > 0 ? d.Stops[^1].StationId : null;
    private static string? Origin(DiaTrain d) => d.Stops.Count > 0 ? d.Stops[0].StationId : null;

    private static double? Sec(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var p = text.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var hh) || !int.TryParse(p[1], out var mm)) return null;
        return hh * 3600 + mm * 60 + (p.Length > 2 && int.TryParse(p[2], out var ss) ? ss : 0);
    }

    /// <summary>
    /// 画面に出す後運用（列車別設定・ダイヤグラムの◆の窓）。number はいまの後運用、original は時刻表の後運用、
    /// candidates は差し替えられる列車（終点の駅から、着いたあとに出る列車。早い順）。
    /// </summary>
    public object? View(DiaTrain? d)
    {
        if (d == null) return null;
        lock (_lock)
        {
            var t = _over.TryGetValue(d.Number, out var o) ? o : d.Turn;
            var changed = _over.ContainsKey(d.Number);
            var term = Terminal(d);
            var candidates = new List<object>();
            if (term != null && rail.Line.ZoneOfStation(term) >= 0)
            {
                var arr = Sec(d.Stops[^1].Arr) ?? Sec(d.Stops[^1].Dep) ?? 0;
                candidates = rail.Dia.Values
                    .Where(c => c.Number != d.Number && Origin(c) == term && !NextPred().ContainsKey(c.Number))
                    .Select(c => (c, dep: Sec(c.Stops[0].Dep) ?? Sec(c.Stops[0].Arr)))
                    .Where(x => x.dep is { } s && s >= arr && s <= arr + 3 * 3600)
                    .OrderBy(x => x.dep)
                    .Take(30)
                    .Select(x => (object)new
                    {
                        train = x.c.Number,
                        type = x.c.Type,
                        time = x.c.Stops[0].Dep ?? x.c.Stops[0].Arr,
                        unyo = Unyo(x.c.Number, 0),
                        prev = Pred().GetValueOrDefault(x.c.Number)
                    }).ToList();
            }
            return new
            {
                kind = t?.Kind,
                number = t?.Number,
                time = t?.Time,
                how = t?.How,
                original = d.Turn?.Number,
                changed,
                cancelable = _changes.Any(c => c.A == d.Number || c.X == d.Number),
                candidates
            };
        }
    }

    /// <summary>列車 train の後運用を next に差し替える。うまくいかなければ理由を返す。</summary>
    public string? Set(string train, string next)
    {
        string message;
        lock (_lock)
        {
            var a = rail.DiaOf(train);
            var c = rail.DiaOf(next);
            if (a == null) return $"{train} は時刻表にありません";
            if (c == null) return $"{next} は時刻表にありません";
            if (a.Number == c.Number) return "同じ列車です";
            if (Terminal(a) is not { } term || Origin(c) != term) return $"{c.Number} は {a.Number} の終点から出る列車ではありません";
            if (NextPred().ContainsKey(c.Number)) return $"{c.Number} は途中駅で列番が変わって続く列車なので、後運用にできません";
            var curA = _over.TryGetValue(a.Number, out var oa) ? oa : a.Turn;
            if (curA?.Number == c.Number) return $"{c.Number} はもう {a.Number} の後運用です";

            var xNo = Pred().GetValueOrDefault(c.Number);
            var x = xNo != null ? rail.Dia.GetValueOrDefault(xNo) : null;
            var curX = x == null ? null : _over.TryGetValue(x.Number, out var ox) ? ox : x.Turn;
            var newA = x != null ? curX : new DiaTurn("折返", c.Number, c.Stops[0].Dep ?? c.Stops[0].Arr, "発");
            var newX = curA;

            Put(a, newA);
            if (x != null) Put(x, newX);
            _changes.Add(new Change(a.Number, x?.Number, curA, curX, newA, newX));
            Version++;
            message = $"後運用の差し替え: {a.Number} の後運用 {curA?.Number ?? "なし"} → {c.Number}"
                + (x != null ? $"（{x.Number} の後運用は {newX?.Number ?? "なし"}）" : "");
        }

        Changed?.Invoke(message);
        return null;
    }

    /// <summary>列車 train が関わる、いちばん新しい差し替えを取り消す。取り消すものがなければ理由を返す。</summary>
    public string? Cancel(string train)
    {
        string message;
        lock (_lock)
        {
            var no = RailData.NormalizeTrain(train);
            var i = _changes.FindLastIndex(c => c.A == no || c.X == no);
            if (i < 0) return $"{no} の後運用は差し替えていません";
            var ch = _changes[i];
            _changes.RemoveAt(i);
            if (rail.Dia.TryGetValue(ch.A, out var a)) Put(a, ch.PrevA);
            if (ch.X != null && rail.Dia.TryGetValue(ch.X, out var x)) Put(x, ch.PrevX);
            Version++;
            message = $"後運用の差し替えを取消: {ch.A} の後運用を {ch.PrevA?.Number ?? "なし"} に戻す"
                + (ch.X != null ? $"（{ch.X} は {ch.PrevX?.Number ?? "なし"}）" : "");
        }

        Changed?.Invoke(message);
        return null;
    }

    /// <summary>差し替えをすべて取り消して、時刻表どおりに戻す（ロープレ時刻を設定したとき）。取り消した数を返す。</summary>
    public int Reset()
    {
        lock (_lock)
        {
            var n = _changes.Count;
            if (n == 0 && _over.Count == 0) return 0;
            _changes.Clear();
            _over.Clear();
            _pred = null;
            Version++;
            return n;
        }
    }

    private void Put(DiaTrain d, DiaTurn? t)
    {
        if (t == d.Turn) _over.Remove(d.Number);
        else _over[d.Number] = t;
        _pred = null;
    }
}
