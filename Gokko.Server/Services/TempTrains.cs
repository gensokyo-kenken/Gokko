using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// 催物用の臨時列車（運転課の画面 untenka.html で作り、司令用管理画面で適用する）。Data/TempTrains.json に保存する。
///
/// ・グループ（例「野球臨」）に、運行番号ごとの列車（列番・種別・行先・駅ごとの停車/通過・着・発・番線）をまとめる
/// ・状態: 下書き → 申請中 → 承認（差戻しは理由を付けて下書きと同じく直せる）。申請は誰でも、承認は運転課課長代理だけ
/// ・承認したグループを司令用管理画面で適用すると、所定のダイヤに重ねる（<see cref="RailData.SetExtraTrains"/>）。
///   所定のダイヤ（Timetable.json）は変えない。取消で外す（走っている自動運転の列車も消す）
/// ・同じ運行番号の中で、ある列車の終点から次の列車が出るなら、折返でつなぐ（自動運転の列車がそのまま続けて走る）
/// ・適用中かどうかも保存し、サーバーを起動したときに重ね直す
/// </summary>
public sealed class TempTrains
{
    public const string FileName = "TempTrains.json";

    public const string Draft = "下書き";
    public const string Pending = "申請中";
    public const string Approved = "承認";
    public const string Rejected = "差戻し";

    /// <summary>種別 → 列番の末尾の文字（RouteEngine の列番の読み方と同じ）。普通は文字なし、回送は頭に「回」。</summary>
    public static readonly (string Type, string Suffix)[] Types =
        [("特急", "A"), ("快急", "K"), ("急行", "B"), ("区急", "D"), ("準急", "C"), ("普通", ""), ("回送", "")];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly RailData _rail;
    private readonly ILogger<TempTrains>? _log;
    private List<Group> _groups = new();
    private int _seq;

    public sealed class FileShape
    {
        public int Seq { get; set; }
        public List<Group> Groups { get; set; } = new();
    }

    public sealed class Group
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = Draft;
        public string Owner { get; set; } = "";
        public string OwnerName { get; set; } = "";
        /// <summary>差戻しの理由・承認のひとこと。</summary>
        public string Comment { get; set; } = "";
        public string ApprovedBy { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
        /// <summary>司令用管理画面で適用しているか。</summary>
        public bool Applied { get; set; }
        public List<TrainSpec> Trains { get; set; } = new();
    }

    public sealed class TrainSpec
    {
        /// <summary>運行番号（"24"・"24S"）。前の形の数字も読む。</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(UnyoJsonConverter))]
        public string Unyo { get; set; } = "";
        public string Number { get; set; } = "";
        public string Type { get; set; } = "";
        /// <summary>種別表記（出発信号機の列選に出す。C特1・急行 など）。空なら種別。</summary>
        public string Label { get; set; } = "";
        public string Dest { get; set; } = "";
        public List<StopSpec> Stops { get; set; } = new();
    }

    /// <summary>1駅。Stop は「停」か「通」。時刻は "HH:mm:ss"（"HH:mm" でもよい）。Track は番線。</summary>
    public sealed class StopSpec
    {
        public string St { get; set; } = "";
        public string Stop { get; set; } = "通";
        public string? Arr { get; set; }
        public string? Dep { get; set; }
        public int? Track { get; set; }
    }

    public TempTrains(string dataDir, RailData rail, ILogger<TempTrains>? log = null)
    {
        _path = Path.Combine(dataDir, FileName);
        _rail = rail;
        _log = log;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json) is { } f)
            {
                _groups = f.Groups;
                _seq = Math.Max(f.Seq, _groups.Count);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} を読めませんでした", FileName);
        }

        lock (_lock)
        {
            // 起動したとき: 適用していたグループを重ね直す（承認でなくなっていたものは外す）
            foreach (var g in _groups.Where(g => g.Applied && g.Status != Approved)) g.Applied = false;
            if (_groups.Any(g => g.Applied))
            {
                var msg = ApplyLayer();
                if (msg != null) _log?.LogWarning("臨時列車: {Message}", msg);
            }
        }
    }

    /// <summary>適用・取消で、ダイヤから外した列番（走っている自動運転の列車を消すため）。</summary>
    public event Action<IReadOnlyCollection<string>>? Removed;

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape { Seq = _seq, Groups = _groups }, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} に保存できませんでした", FileName);
        }
    }

    // =========================================================================================
    // 一覧・編集（運転課の画面）
    // =========================================================================================

    public List<Group> Groups()
    {
        lock (_lock) return _groups.Select(Clone).ToList();
    }

    public Group? Get(string? id)
    {
        lock (_lock) return _groups.FirstOrDefault(g => g.Id == id) is { } g ? Clone(g) : null;
    }

    private static Group Clone(Group g) => JsonSerializer.Deserialize<Group>(JsonSerializer.Serialize(g, Json), Json)!;

    private static bool Editable(Group g) => g.Status is Draft or Rejected;

    /// <summary>グループを作る・直す（下書き・差戻しのものだけ、作った人が）。うまくいけば (id, 注意) を返す。</summary>
    public (string? Id, string? Error, List<string> Warnings) Save(string? id, string name, List<TrainSpec> trains, string user, string userName)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return (null, "グループの名前を入れてください", new());
        if (name.Length > 30) return (null, "グループの名前は30文字までにしてください", new());
        if ((trains?.Count ?? 0) > 100) return (null, "1つのグループの列車は100本までにしてください", new());
        trains = Normalize(trains ?? new());
        var (errors, warnings) = Check(trains, id);
        if (errors.Count > 0) return (null, string.Join("\n", errors), warnings);
        lock (_lock)
        {
            Group g;
            if (string.IsNullOrEmpty(id))
            {
                g = new Group { Id = $"g{++_seq}", Owner = user, OwnerName = userName };
                _groups.Add(g);
            }
            else
            {
                g = _groups.FirstOrDefault(x => x.Id == id)!;
                if (g == null) return (null, "そのグループはありません", warnings);
                if (g.Owner != user) return (null, "作った人だけが直せます", warnings);
                if (!Editable(g)) return (null, $"{g.Status}のグループは直せません（取り下げて下書きに戻してください）", warnings);
            }
            g.Name = name;
            g.Trains = trains;
            g.UpdatedUtc = DateTime.UtcNow;
            Save();
            return (g.Id, null, warnings);
        }
    }

    /// <summary>
    /// 状態を変える。action: submit（申請。作った人）・withdraw（取り下げて下書きへ。作った人か課長代理。適用中は不可）・
    /// approve / reject（課長代理。申請中のものだけ）・delete（作った人。適用中・承認は不可）。
    /// </summary>
    public string? Act(string? id, string action, string user, bool approver, string? comment)
    {
        lock (_lock)
        {
            var g = _groups.FirstOrDefault(x => x.Id == id);
            if (g == null) return "そのグループはありません";
            var mine = g.Owner == user;
            switch (action)
            {
                case "submit":
                    if (!mine) return "作った人だけが申請できます";
                    if (!Editable(g)) return $"{g.Status}のグループは申請できません";
                    if (g.Trains.Count == 0) return "列車がありません";
                    var (errors, _) = Check(g.Trains, g.Id);
                    if (errors.Count > 0) return string.Join("\n", errors);
                    g.Status = Pending;
                    g.Comment = "";
                    break;
                case "withdraw":
                    if (!mine && !approver) return "作った人か運転課課長代理だけが取り下げられます";
                    if (g.Applied) return "適用中です。先に司令用管理画面で取り消してください";
                    if (g.Status is not (Pending or Approved)) return "申請中・承認のグループだけ取り下げられます";
                    g.Status = Draft;
                    g.ApprovedBy = "";
                    break;
                case "approve":
                    if (!approver) return "運転課課長代理だけが承認できます";
                    if (g.Status != Pending) return "申請中のグループだけ承認できます";
                    var (errs, _) = Check(g.Trains, g.Id);
                    if (errs.Count > 0) return string.Join("\n", errs);
                    g.Status = Approved;
                    g.ApprovedBy = user;
                    g.Comment = (comment ?? "").Trim();
                    break;
                case "reject":
                    if (!approver) return "運転課課長代理だけが差し戻せます";
                    if (g.Status != Pending) return "申請中のグループだけ差し戻せます";
                    g.Status = Rejected;
                    g.Comment = (comment ?? "").Trim();
                    break;
                case "delete":
                    if (!mine && !approver) return "作った人か運転課課長代理だけが消せます";
                    if (g.Applied || g.Status == Approved) return "承認したグループは消せません（取り下げてから消してください）";
                    _groups.Remove(g);
                    Save();
                    return null;
                default:
                    return "その操作はありません";
            }
            g.UpdatedUtc = DateTime.UtcNow;
            Save();
            return null;
        }
    }

    // =========================================================================================
    // 適用・取消（司令用管理画面）
    // =========================================================================================

    /// <summary>グループを適用する（on）・取り消す。うまくいかなければ理由を返す。</summary>
    public string? Apply(string? id, bool on)
    {
        List<string> removed;
        lock (_lock)
        {
            var g = _groups.FirstOrDefault(x => x.Id == id);
            if (g == null) return "そのグループはありません";
            if (on && g.Status != Approved) return "承認されたグループだけ適用できます";
            if (g.Applied == on) return null;
            if (on)
            {
                // ほかの適用中のグループ・所定のダイヤと列番がかぶらないか
                var used = _groups.Where(x => x.Applied).SelectMany(x => x.Trains).Select(t => t.Number).ToHashSet();
                var dup = g.Trains.Select(t => t.Number).Where(n => used.Contains(n) || _rail.BaseDia.ContainsKey(n)).ToList();
                if (dup.Count > 0) return $"列番がほかの列車とかぶっています: {string.Join("、", dup)}";
            }
            g.Applied = on;
            removed = on ? new() : g.Trains.Select(t => t.Number).ToList();
            var msg = ApplyLayer();
            if (msg != null) { g.Applied = !on; ApplyLayer(); return msg; }
            Save();
        }
        if (removed.Count > 0) Removed?.Invoke(removed);
        return null;
    }

    /// <summary>適用中のグループの列車を、所定のダイヤに重ねる（_lock の中）。うまくいかなければ理由を返す。</summary>
    private string? ApplyLayer()
    {
        var extra = new List<DiaTrain>();
        foreach (var g in _groups.Where(g => g.Applied))
            extra.AddRange(Build(g.Trains));
        var skipped = _rail.SetExtraTrains("催物", extra);
        return skipped.Count > 0 ? $"所定のダイヤにある列番は足せませんでした: {string.Join("、", skipped)}" : null;
    }

    public string StationName(string id) => _rail.Line.StationName(id);

    /// <summary>適用中の列番（司令用管理画面の一覧）。</summary>
    public HashSet<string> AppliedNumbers()
    {
        lock (_lock) return _groups.Where(g => g.Applied).SelectMany(g => g.Trains).Select(t => t.Number).ToHashSet();
    }

    // =========================================================================================
    // 入力の確かめ・ダイヤの列車に直す
    // =========================================================================================

    private static readonly Regex TimeRe = new(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?$");

    /// <summary>"H:mm" "HH:mm:ss" → "HH:mm:ss"。読めなければ null。</summary>
    public static string? NormTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = TimeRe.Match(s.Normalize(System.Text.NormalizationForm.FormKC).Trim());
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value), se = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (h > 27 || mi > 59 || se > 59) return null;
        return $"{h % 24:00}:{mi:00}:{se:00}";
    }

    /// <summary>運転日の秒（4時前は翌日）。</summary>
    private static double? DaySec(string? s)
    {
        if (NormTime(s) is not { } t) return null;
        var p = t.Split(':');
        var v = int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60 + int.Parse(p[2]);
        return v < 4 * 3600 ? v + 86400 : v;
    }

    private List<TrainSpec> Normalize(List<TrainSpec> trains)
    {
        foreach (var t in trains)
        {
            t.Number = RailData.NormalizeTrain(t.Number);
            t.Unyo = Gokko.Common.Unyo.Normalize(t.Unyo) ?? "";
            t.Type = (t.Type ?? "").Trim();
            t.Label = (t.Label ?? "").Trim();
            t.Dest = (t.Dest ?? "").Trim();
            t.Stops ??= new();
            foreach (var s in t.Stops)
            {
                s.St = (s.St ?? "").Trim();
                s.Stop = s.Stop == "停" ? "停" : "通";
                s.Arr = s.Stop == "停" ? NormTime(s.Arr) ?? (string.IsNullOrWhiteSpace(s.Arr) ? null : s.Arr) : null;
                s.Dep = NormTime(s.Dep) ?? (string.IsNullOrWhiteSpace(s.Dep) ? null : s.Dep);
            }
        }
        return trains.OrderBy(t => t.Unyo, Gokko.Common.Unyo.Comparer).ThenBy(t => t.Stops.Select(s => DaySec(s.Dep ?? s.Arr)).FirstOrDefault(x => x != null) ?? 0).ToList();
    }

    /// <summary>種別に合う列番の形か（末尾の文字・回送の「回」）。</summary>
    private static string? NumberTypeError(TrainSpec t)
    {
        if (RouteEngine.TrainInfo(t.Number) is not { } info) return $"{t.Number}: 列番に数字がありません";
        if (info.Type == t.Type) return null;
        var suffix = Types.FirstOrDefault(x => x.Type == t.Type).Suffix;
        return t.Type == "回送" ? $"{t.Number}: 回送の列番は「回」で始めてください"
            : t.Type == "普通" ? $"{t.Number}: 普通の列番は、数字のあとに種別の文字（A・B・C・D・K）を付けないでください"
            : $"{t.Number}: {t.Type}の列番は、末尾を {suffix} にしてください";
    }

    /// <summary>
    /// 入力を確かめる。errors があれば保存・申請・承認できない。warnings は知らせるだけ。
    /// 確かめること: 列番の形（種別・向き）・列番のかぶり・駅の並び・時刻の順・停車駅の番線
    /// </summary>
    public (List<string> Errors, List<string> Warnings) Check(List<TrainSpec> trains, string? groupId)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var line = _rail.Line;
        var downUp = DownIncreasing();
        var seen = new HashSet<string>();
        var types = Types.Select(x => x.Type).ToHashSet();
        foreach (var t in trains)
        {
            var n = t.Number;
            if (n.Length == 0) { errors.Add("列番が空の列車があります"); continue; }
            if (!seen.Add(n)) errors.Add($"{n}: 同じ列番が2本あります");
            if (_rail.BaseDia.ContainsKey(n)) errors.Add($"{n}: 所定の時刻表にある列番です");
            if (t.Unyo.Length == 0) errors.Add($"{n}: 運行番号を入れてください（24・24S など）");
            if (!types.Contains(t.Type)) { errors.Add($"{n}: 種別を選んでください"); continue; }
            if (NumberTypeError(t) is { } te) errors.Add(te);
            if (t.Stops.Count < 2) { errors.Add($"{n}: 駅を2つ以上入れてください"); continue; }

            // 駅は路線の駅で、向きに沿って並んでいること
            var zones = t.Stops.Select(s => line.ZoneOfStation(s.St)).ToList();
            if (zones.Any(z => z < 0)) { errors.Add($"{n}: 路線にない駅があります"); continue; }
            var inc = zones[^1] > zones[0];
            for (var i = 1; i < zones.Count; i++)
                if ((zones[i] > zones[i - 1]) != inc || zones[i] == zones[i - 1]) { errors.Add($"{n}: 駅の並びが逆になっている所があります"); break; }
            if (RouteEngine.TrainInfo(n) is { } info && downUp is { } du && (info.Dir == Dir.Down) != (inc == du))
                errors.Add($"{n}: 列番の数字の最後が{(info.Dir == Dir.Down ? "奇数（下り）" : "偶数（上り）")}ですが、駅の並びは{(info.Dir == Dir.Down ? "上り" : "下り")}です");
            if (t.Stops[0].Stop != "停" || t.Stops[^1].Stop != "停") errors.Add($"{n}: 発駅と終点は停車にしてください");

            // 時刻: 停車駅は着・発（始発は発だけ、終点は着だけでよい）、通過駅は通過の時刻。前の駅より前にならないこと
            double? prev = null;
            for (var i = 0; i < t.Stops.Count; i++)
            {
                var s = t.Stops[i];
                var name = line.StationName(s.St);
                var first = i == 0;
                var last = i == t.Stops.Count - 1;
                if (s.Arr != null && DaySec(s.Arr) == null || s.Dep != null && DaySec(s.Dep) == null) { errors.Add($"{n} {name}: 時刻の形が違います（例 10:05 か 10:05:30）"); continue; }
                if (last ? s.Arr == null && s.Dep == null : s.Dep == null) { errors.Add($"{n} {name}: {(last ? "着" : s.Stop == "停" ? "発" : "通過")}の時刻を入れてください"); continue; }
                foreach (var v in new[] { DaySec(s.Arr), DaySec(s.Dep) })
                {
                    if (v == null) continue;
                    if (prev != null && v < prev) { errors.Add($"{n} {name}: 前の駅より早い時刻になっています"); break; }
                    prev = v;
                }
                if (_rail.Stations.TryGetValue(s.St, out var st) && st.RouteControl && s.Stop == "停" && s.Track == null && !first)
                    errors.Add($"{n} {name}: 停車する駅の番線を入れてください");
                if (first && _rail.Stations.TryGetValue(s.St, out var st0) && st0.RouteControl && s.Track == null)
                    errors.Add($"{n} {name}: 発駅の番線を入れてください");
            }
        }

        // ほかのグループの列番とかぶっていないか（知らせるだけ。適用するときに、適用中のものとかぶれば断る）
        lock (_lock)
            foreach (var g in _groups.Where(g => g.Id != groupId))
            foreach (var n in g.Trains.Select(x => x.Number).Where(seen.Contains))
                warnings.Add($"{n}: グループ「{g.Name}」にも同じ列番があります（同時には適用できません）");

        // 所定の運行番号とかぶっていないか（知らせるだけ）
        var baseUnyo = _rail.BaseDia.Values.Select(d => d.Unyo).Where(u => u != null).ToHashSet();
        foreach (var u in trains.Select(t => t.Unyo).Distinct().Where(u => baseUnyo.Contains(u)))
            warnings.Add($"運行番号 {u} は所定の時刻表でも使っています（乗務員の行路と重なるおそれがあります）");
        return (errors, warnings);
    }

    /// <summary>下り（列番が奇数）は、路線の駅の並び（ゾーン）が増える向きか。所定のダイヤから読む。</summary>
    public bool? DownIncreasing()
    {
        int votes = 0;
        foreach (var d in _rail.BaseDia.Values.Take(400))
        {
            var zs = d.Stops.Select(s => s.StationId is { } id ? _rail.Line.ZoneOfStation(id) : -1).Where(z => z >= 0).ToList();
            if (zs.Count < 2 || zs[0] == zs[^1] || RouteEngine.TrainInfo(d.Number) is not { } info) continue;
            votes += (zs[^1] > zs[0]) == (info.Dir == Dir.Down) ? 1 : -1;
        }
        return votes == 0 ? null : votes > 0;
    }

    /// <summary>入力した列車を、ダイヤの列車に直す。同じ運行番号で、終点から次の列車が出るなら折返でつなぐ。</summary>
    private List<DiaTrain> Build(List<TrainSpec> trains)
    {
        var list = new List<DiaTrain>();
        var byUnyo = trains.Where(t => t.Unyo.Length > 0).GroupBy(t => t.Unyo).ToList();
        foreach (var g in byUnyo)
        {
            var ordered = g.OrderBy(t => DaySec(t.Stops.FirstOrDefault()?.Dep) ?? 0).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var t = ordered[i];
                var d = new DiaTrain
                {
                    Number = t.Number, Type = t.Type, TypeLabel = t.Label.Length > 0 ? t.Label : t.Type,
                    Dest = t.Dest.Length > 0 ? t.Dest : _rail.Line.StationName(t.Stops[^1].St), Unyo = t.Unyo
                };
                foreach (var s in t.Stops)
                {
                    d.Stops.Add(new DiaStop(_rail.Line.StationName(s.St), s.St, s.Stop, NormTime(s.Arr), NormTime(s.Dep), s.Track));
                    d.Stations[s.St] = (s.Stop == "停" ? "停車" : "通過", s.Track);
                }
                var last = t.Stops[^1].St;
                if (_rail.Stations.TryGetValue(last, out var ts) && ts.RouteControl) d.TerminalId = last;
                if (i + 1 < ordered.Count && ordered[i + 1].Stops.FirstOrDefault() is { } nf && nf.St == last)
                    d.Turn = new DiaTurn("折返", ordered[i + 1].Number, NormTime(nf.Dep), "発");
                list.Add(d);
            }
        }
        return list;
    }
}
