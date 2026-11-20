using System.Text;

namespace Gokko.Server.Services;

/// <summary>
/// 勤怠管理の「行路を組む」: 列車を選んで、ほかの行路（または新しい行路）へ移す。行路のない列車も入れられる。
/// いまの行路表を写して Data/Duties.{名前}.csv（既定は Duties.edit.csv）に書き、それを行路表として読む（手で作った CSV は書き換えない）。
/// 移したあとの行路は、列車を発車の順に並べ直し、5分以上あく所に「休憩」を入れ直す。列車がなくなった行路は消す。
/// </summary>
public static class DutyEdit
{
    public const string FileName = "Duties.edit.csv";
    private const int BreakSec = 300;

    private sealed class Item
    {
        public string Kind = "乗務";
        public string Train = "";
        public string? From;
        public string? To;
        public string Note = "";
    }

    /// <summary>列車の、from の駅の発車時刻（秒）。わからなければ大きな数。</summary>
    private static (int Dep, int Arr, string? FromName, string? ToName) Span(RailData rail, Item x)
    {
        if (rail.DiaOf(x.Train) is not { } d) return (int.MaxValue, int.MaxValue, x.From, x.To);
        var inLine = d.Stops.Where(s => s.StationId != null && rail.Line.ZoneOfStation(s.StationId) >= 0).ToList();
        if (inLine.Count == 0) inLine = d.Stops;
        var a = x.From != null ? d.Stops.FirstOrDefault(s => s.Name == x.From) ?? inLine[0] : inLine[0];
        var b = x.To != null ? d.Stops.LastOrDefault(s => s.Name == x.To) ?? inLine[^1] : inLine[^1];
        return (DutyDiagram.Sec(a.Dep ?? a.Arr) ?? int.MaxValue, DutyDiagram.Sec(b.Arr ?? b.Dep) ?? int.MaxValue, a.Name, b.Name);
    }

    /// <summary>
    /// trains を target の行路へ移す（target が空なら newId の新しい行路を作る）。うまくいけば書いたファイル名、だめなら理由。
    /// </summary>
    public static (string? File, string? Error, string? Target) Move(string dataDir, RailData rail, DutyRoster roster, List<string> trains, string? target, string? newId, string? name = null)
    {
        var (outFile, nameErr) = OutFile(dataDir, roster, name);
        if (outFile == null) return (null, nameErr, null);
        var keys = trains.Select(RailData.NormalizeTrain).Where(t => t.Length > 0).Distinct().ToList();
        if (keys.Count == 0) return (null, "移す列車を選んでください", null);
        var (duties, order) = Copy(roster);

        var to = (target ?? "").Trim();
        if (to.Length == 0)
        {
            to = (newId ?? "").Trim();
            if (to.Length == 0) return (null, "新しい行路の行路番号を入れてください", null);
            if (to.Contains(',') || to.StartsWith('#')) return (null, "行路番号に「,」や先頭の「#」は使えません", null);
            if (duties.ContainsKey(to)) return (null, $"行路 {to} はもうあります（移す先で選んでください）", null);
            duties[to] = new List<Item>();
            order.Add(to);
        }
        else if (!duties.ContainsKey(to)) return (null, $"行路 {to} はありません", null);

        // 範囲（館浜〜赤山町）の端の駅（行路のない列車を入れるときの乗る区間）
        var range = DutyDiagram.RangeIds(rail).ToHashSet();
        foreach (var key in keys)
        {
            Item? moved = null;
            foreach (var (id, items) in duties)
            {
                if (id == to) continue;
                var i = items.FindIndex(x => x.Train == key);
                if (i < 0) continue;
                moved ??= items[i];
                items.RemoveAt(i);
            }

            if (moved == null && duties[to].Any(x => x.Train == key)) continue; // もうその行路にある
            if (moved == null)
            {
                if (rail.DiaOf(key) is not { } d) return (null, $"{key} はダイヤにありません", null);
                var inRange = d.Stops.Where(s => s.StationId != null && range.Contains(s.StationId)).ToList();
                moved = new Item { Train = d.Number, From = inRange.FirstOrDefault()?.Name, To = inRange.LastOrDefault()?.Name };
            }

            duties[to].Add(moved);
        }

        Write(dataDir, outFile, rail, roster, duties, order);
        return (outFile, null, to);
    }

    /// <summary>行路番号 from を to に変える（中の列車はそのまま）。うまくいけば書いたファイル名、だめなら理由。</summary>
    public static (string? File, string? Error) Rename(string dataDir, RailData rail, DutyRoster roster, string? from, string? to, string? name = null)
    {
        var a = (from ?? "").Trim();
        var b = (to ?? "").Trim();
        if (a.Length == 0 || b.Length == 0) return (null, "変える行路と、新しい行路名を入れてください");
        if (b.Contains(',') || b.StartsWith('#')) return (null, "行路名に「,」や先頭の「#」は使えません");
        var (outFile, nameErr) = OutFile(dataDir, roster, name);
        if (outFile == null) return (null, nameErr);
        var (duties, order) = Copy(roster);
        if (!duties.Remove(a, out var items)) return (null, $"行路 {a} はありません");
        if (a == b) return (null, "新しい行路名が、いまと同じです");
        if (duties.ContainsKey(b)) return (null, $"行路 {b} はもうあります");
        duties[b] = items;
        order[order.IndexOf(a)] = b;
        Write(dataDir, outFile, rail, roster, duties, order);
        return (outFile, null);
    }

    /// <summary>書く先: 名前を決めればその CSV。決めなければ、いまの行路表が勤怠管理で作ったものならそれ、手で作ったものなら Duties.edit.csv。</summary>
    private static (string? File, string? Error) OutFile(string dataDir, DutyRoster roster, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) return FileNameFor(name, dataDir);
        return (MadeByKintai(Path.Combine(dataDir, roster.File)) ? roster.File : FileName, null);
    }

    /// <summary>いまの行路表を写す（休憩は書くときに入れ直すので除く）。</summary>
    private static (Dictionary<string, List<Item>> Duties, List<string> Order) Copy(DutyRoster roster) =>
        (roster.Duties.ToDictionary(d => d.Id, d => d.Items.Where(x => x.Kind != "休憩")
            .Select(x => new Item { Kind = x.Kind, Train = x.Train, From = x.From, To = x.To, Note = x.Note }).ToList()),
         roster.Duties.Select(d => d.Id).ToList());

    /// <summary>行路を CSV に書く。列車を発車の順に並べ、5分以上あく所に「休憩」を入れる。列車のない行路は書かない。</summary>
    private static void Write(string dataDir, string outFile, RailData rail, DutyRoster roster, Dictionary<string, List<Item>> duties, List<string> order)
    {
        var csv = new StringBuilder();
        csv.AppendLine("行路番号,順,種別,列車番号,開始駅,終了駅,備考");
        csv.AppendLine($"# 勤怠管理の「行路を組む」で組んだ行路（もとは {roster.File}）。組み直すと上書きされます");
        foreach (var id in order.OrderBy(x => x, DutyRoster.IdOrder))
        {
            var items = duties[id].Select(x => (x, s: Span(rail, x))).OrderBy(t => t.s.Dep).ToList();
            var n = 0;
            (Item x, (int Dep, int Arr, string? FromName, string? ToName) s)? prev = null;
            foreach (var t in items)
            {
                if (prev is { } p && p.s.Arr != int.MaxValue && t.s.Dep != int.MaxValue && t.s.Dep - p.s.Arr >= BreakSec)
                    csv.AppendLine($"{id},{++n},休憩,,{p.s.ToName},,{(t.s.Dep - p.s.Arr + 59) / 60}分");
                csv.AppendLine($"{id},{++n},{t.x.Kind},{t.x.Train},{t.x.From},{t.x.To},{t.x.Note}");
                prev = t;
            }
        }

        File.WriteAllText(Path.Combine(dataDir, outFile), csv.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// 名前から行路表のファイル名（Duties.{名前}.csv）を作る。「Duties.」「.csv」は付けても付けなくてもよい。
    /// 手で作った行路表（勤怠管理で作った印の行がないもの）と同じ名前なら、上書きしないよう断る。
    /// </summary>
    public static (string? File, string? Error) FileNameFor(string? name, string dataDir)
    {
        var n = (name ?? "").Trim();
        if (n.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (n.StartsWith("Duties.", StringComparison.OrdinalIgnoreCase)) n = n[7..];
        if (n.Length == 0) return (null, "行路表の名前を入れてください");
        if (n.Length > 40 || n.StartsWith('.') || n.Contains("..") || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return (null, "行路表の名前に使えない字が入っています（/ \\ : * ? \" < > | など）");
        if (n.Equals("sample", StringComparison.OrdinalIgnoreCase)) return (null, "その名前は使えません");
        var file = $"Duties.{n}.csv";
        var path = Path.Combine(dataDir, file);
        if (File.Exists(path) && !MadeByKintai(path)) return (null, $"{file} は手で作った行路表なので上書きしません。ほかの名前にしてください");
        return (file, null);
    }

    /// <summary>勤怠管理で作った行路表か（2行目に作った印の「#」の行がある）。</summary>
    public static bool MadeByKintai(string path)
    {
        try
        {
            var second = File.ReadLines(path).Skip(1).FirstOrDefault() ?? "";
            return second.StartsWith("# 運行番号から作った行路") || second.StartsWith("# 勤怠管理の「行路を組む」");
        }
        catch { return false; }
    }
}
