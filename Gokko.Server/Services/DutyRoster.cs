using System.Text;

namespace Gokko.Server.Services;

/// <summary>行路の1項目（乗務する列車、または休憩・便乗・入換）。時刻はダイヤから求める（休憩は前後の項目の間）。</summary>
public sealed record DutyItem(int Order, string Kind, string Train, string? From, string? To, string Note);

/// <summary>行路（運転士1人の乗務の並び）。</summary>
public sealed record Duty(string Id, List<DutyItem> Items);

/// <summary>
/// 行路表（Data/Duties.csv など。勤怠管理で Duties*.csv から選ぶ → DutyRosters）。ない場合は空（行路を使わない運転会）。
/// 列: 行路番号,順,種別,列車番号,開始駅,終了駅,備考（1行目は見出し）。種別は 乗務・入換・便乗・休憩。
/// 開始駅・終了駅は、列車の途中から・途中まで乗るときだけ書く（空なら、その列車のダイヤの線区の中の最初・最後の駅）。
/// 運行番号（編成のつながり）とは別のもの。行路はユーザーが組んで CSV で渡す。
/// </summary>
public sealed class DutyRoster
{
    public const string FileName = "Duties.csv";

    /// <summary>読んだファイル名（Duties.csv など）。</summary>
    public string File { get; private init; } = FileName;

    /// <summary>そのファイルがあったか。</summary>
    public bool Exists { get; private init; }

    public List<Duty> Duties { get; } = new();

    /// <summary>読み込みで見つけた注意（行がおかしい、列番がダイヤにない など）。</summary>
    public List<string> Problems { get; } = new();

    public static DutyRoster Load(string dataDir, RailData rail, string fileName = FileName)
    {
        var path = Path.Combine(dataDir, fileName);
        var roster = new DutyRoster { File = fileName, Exists = System.IO.File.Exists(path) };
        if (!roster.Exists) return roster;

        var byId = new Dictionary<string, Duty>();
        var lines = System.IO.File.ReadAllLines(path, Encoding.UTF8);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 4) { roster.Problems.Add($"{i + 1}行目: 列が足りません"); continue; }
            var id = f[0];
            if (!int.TryParse(f[1], out var order)) { roster.Problems.Add($"{i + 1}行目: 順が数字ではありません"); continue; }
            var kind = f[2].Length > 0 ? f[2] : "乗務";
            var train = RailData.NormalizeTrain(f[3]);
            if (kind != "休憩" && rail.DiaOf(train) == null) roster.Problems.Add($"{i + 1}行目: {train} はダイヤにありません");
            if (!byId.TryGetValue(id, out var duty)) byId[id] = duty = new Duty(id, new List<DutyItem>());
            duty.Items.Add(new DutyItem(order, kind, train, f.Length > 4 && f[4].Length > 0 ? f[4] : null, f.Length > 5 && f[5].Length > 0 ? f[5] : null,
                f.Length > 6 ? string.Join(",", f.Skip(6)) : ""));
        }

        foreach (var d in byId.Values)
        {
            d.Items.Sort((a, b) => a.Order.CompareTo(b.Order));
            roster.Duties.Add(d);
        }

        roster.Duties.Sort((a, b) => IdOrder.Compare(a.Id, b.Id));
        return roster;
    }

    /// <summary>
    /// 行路番号の並び。グループごと（変 → 赤 → 頭に字のないもの → そのほか（試 など）、その中で 道 → 駒）に並べ、
    /// グループの中は数字を数として比べる（道1, 道2, …, 道9, 道10, 道11。泊のつかない行路が先）。
    /// </summary>
    public static readonly IComparer<string> IdOrder = Comparer<string>.Create((a, b) =>
    {
        var c = GroupRank(a ?? "").CompareTo(GroupRank(b ?? ""));
        return c != 0 ? c : CompareIds(a, b);
    });

    private static int GroupRank(string id)
    {
        var series = id.StartsWith('変') ? 0 : id.StartsWith('赤') ? 1 : id.StartsWith('道') || id.StartsWith('駒') ? 2 : 3;
        var rest = series < 2 ? id[1..] : id;
        var depot = rest.StartsWith('道') ? 0 : rest.StartsWith('駒') ? 1 : 2;
        return series * 3 + depot;
    }

    private static int CompareIds(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
                continue;
            }

            if (a[i] != b[j]) return a[i].CompareTo(b[j]);
            i++; j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>その列車を受け持つ行路（最初に見つかったもの）。</summary>
    public Duty? DutyOfTrain(string train)
    {
        var key = RailData.NormalizeTrain(train);
        return Duties.FirstOrDefault(d => d.Items.Any(x => x.Kind != "休憩" && x.Train == key));
    }

    public Duty? DutyById(string id) => Duties.FirstOrDefault(d => d.Id == id);
}

/// <summary>
/// いま使う行路表。Data/ にある Duties*.csv（Duties.sample.csv を除く）から、勤怠管理の画面で選んで読み直せる（再起動しなくてよい）。
/// 選んだファイル名は Data/DutySelection.txt に覚えておき、再起動しても同じファイルを読む。
/// 行路の割り当て（CrewRoster）は行路番号で持っているので、切り替えても消えない。
/// </summary>
public sealed class DutyRosters
{
    public const string SelectionFile = "DutySelection.txt";

    private readonly string _dataDir;
    private readonly RailData _rail;
    private readonly ILogger<DutyRosters>? _log;
    private readonly object _lock = new();
    private DutyRoster _current;

    public DutyRosters(string dataDir, RailData rail, ILogger<DutyRosters>? log = null)
    {
        _dataDir = dataDir;
        _rail = rail;
        _log = log;
        var name = DutyRoster.FileName;
        try
        {
            var saved = Path.Combine(dataDir, SelectionFile);
            if (System.IO.File.Exists(saved) && System.IO.File.ReadAllText(saved, Encoding.UTF8).Trim() is { Length: > 0 } s)
            {
                if (Files().Contains(s)) name = s;
                else _log?.LogWarning("行路表 {File} が見つからないので {Default} を読みます", s, DutyRoster.FileName);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "{File} を読めませんでした", SelectionFile); }

        _current = DutyRoster.Load(dataDir, rail, name);
    }

    public string DataDir => _dataDir;
    public RailData Rail => _rail;

    /// <summary>いまの行路表（読み直すと入れ替わる。1回の処理の中では、取り出したものを使い続ける）。</summary>
    public DutyRoster Current => Volatile.Read(ref _current);

    /// <summary>選べる行路表のファイル名（Duties.csv を先に）。</summary>
    public List<string> Files()
    {
        if (!Directory.Exists(_dataDir)) return new();
        return Directory.GetFiles(_dataDir, "Duties*.csv")
            .Select(p => Path.GetFileName(p))
            .Where(n => !n.Equals("Duties.sample.csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Equals(DutyRoster.FileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>勤怠管理で作った行路表（運行番号から作る・行路を組む）のファイル名。もとからある行路表は入れない。</summary>
    public List<string> KintaiFiles() => Files().Where(n => DutyEdit.MadeByKintai(Path.Combine(_dataDir, n))).ToList();

    /// <summary>勤怠管理で作った行路表を消す（管理画面から）。手で作ったもの・いま使っているものは消さない。うまくいかなければ理由を返す。</summary>
    public string? Delete(string? fileName)
    {
        var name = (fileName ?? "").Trim();
        lock (_lock)
        {
            if (!Files().Contains(name)) return $"行路表 {name} はありません";
            if (!DutyEdit.MadeByKintai(Path.Combine(_dataDir, name))) return $"{name} は勤怠管理で作った行路表ではないので消せません";
            if (name == Current.File) return $"{name} はいま使っている行路表なので消せません（勤怠管理でほかの行路表に切り替えてから消してください）";
            try { System.IO.File.Delete(Path.Combine(_dataDir, name)); }
            catch (Exception ex) { return $"{name} を消せませんでした: {ex.Message}"; }
            _log?.LogInformation("行路表 {File} を消しました", name);
        }

        return null;
    }

    /// <summary>行路表を読み直す（同じファイルでもよい）。うまくいかなければ理由を返す。</summary>
    public string? Load(string? fileName)
    {
        var name = (fileName ?? "").Trim();
        if (!Files().Contains(name)) return $"行路表 {name} はありません";
        lock (_lock)
        {
            DutyRoster roster;
            try { roster = DutyRoster.Load(_dataDir, _rail, name); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "行路表 {File} を読めませんでした", name);
                return $"行路表 {name} を読めませんでした: {ex.Message}";
            }

            Volatile.Write(ref _current, roster);
            try { System.IO.File.WriteAllText(Path.Combine(_dataDir, SelectionFile), name, new UTF8Encoding(false)); }
            catch (Exception ex) { _log?.LogWarning(ex, "{File} に書けませんでした", SelectionFile); }
            _log?.LogInformation("行路表を {File} に切り替えました（{Count} 行路・注意 {Problems} 件）", name, roster.Duties.Count, roster.Problems.Count);
        }

        return null;
    }
}
