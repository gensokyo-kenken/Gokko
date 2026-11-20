using System.Text;

namespace Gokko.Server.Services;

/// <summary>
/// 運行番号から行路を組む（勤怠管理の［運行番号から作る］）。時間（17:00〜19:00 など）を決めると、その間に館浜〜赤山町を走る列車を
/// 漏れなく拾い、いちばん少ない運転士で受け持つように行路を組む（Cover）。
/// ・同じ編成の続き（終点の折返・途中の列番変更）には、そのまま乗り続けられる。
/// ・着いた駅から出るほかの列車にも乗り換えられる（渡り。赤山町は5分、ほかの駅は3分以上あと。渡りの上限（既定30分）まで。短いほどよい）。
///   同じ編成の折返しを待つ間は「待機」（待たざるを得ないので長さは問わない）。どちらも行路表では休憩の行に「渡り22分」「待機16分」と書く。
///   例: 特急で赤山町まで行き、赤山町から普通で戻る（赤換）／館浜に着いた特急から、館浜を出る特急へ乗り換える（館交）。館浜〜駒野の回送は回送だけの行路になる。
/// ・人数が同じなら、待ちが短い・同じ編成に乗り続ける・特急と普通、営業と回送をまたがない組み方を選ぶ。
/// ・行路番号は、運行番号の順に 道1, 道2, …。特急の列車に乗る行路は 駒1, 駒2, …（駒列）。
/// ・できた行路は Data/Duties.{名前}.csv（名前は勤怠管理で決める。既定は Duties.unyo.csv）に書き出し、そのまま行路表として読む。
/// </summary>
public static class UnyoDuties
{
    public const string FileName = "Duties.unyo.csv";
    public const string StartName = "館浜";
    public const string EndName = "赤山町";

    /// <summary>1本の列車の、範囲の中で乗る区間（時刻は秒。0時からの通し。24時を越えてもよい）。</summary>
    private sealed record Seg(DiaTrain Dia, string From, string To, int Dep, int Arr, bool LeavesAtEnd, bool EntersAtEnd);

    public sealed record Result(string File, int Duties, int Trains, int Express, string Window);

    public static int? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = text.Trim().Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var m)) return null;
        var s = p.Length > 2 && int.TryParse(p[2], out var ss) ? ss : 0;
        return h * 3600 + m * 60 + s;
    }

    private static string Hm(int sec) => $"{sec / 3600:00}:{sec / 60 % 60:00}";

    /// <summary>行路を組んで CSV にする。from・to は秒（to が from より小さければ日をまたぐ）。</summary>
    public static (string Csv, Result Info) Build(RailData rail, int from, int to, int minTurnSec = 300, int maxTransferSec = 1800)
    {
        if (to <= from) to += 24 * 3600;
        var ids = rail.Line.StationIds;
        var a = ids.ToList().FindIndex(x => rail.Line.StationName(x) == StartName);
        var b = ids.ToList().FindIndex(x => rail.Line.StationName(x) == EndName);
        if (a < 0 || b < 0) throw new InvalidOperationException($"線区に {StartName}・{EndName} が見つかりません");
        var range = ids.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).ToHashSet();
        var endId = ids[b];

        // 範囲の中で乗る区間（時間と重なるもの）
        var segs = new Dictionary<string, Seg>();
        foreach (var d in rail.Dia.Values)
        {
            var idx = d.Stops.Select((s, i) => (s, i)).Where(x => x.s.StationId != null && range.Contains(x.s.StationId)).ToList();
            if (idx.Count < 2) continue;
            var first = idx[0];
            var last = idx[^1];
            // 途中の駅で列番が変わる列車は、その駅まで（その先は次の列番の列車）
            if (d.Next is { } nx && idx.FirstOrDefault(x => x.s.StationId == nx.StationId) is { s: not null } cut && cut.i > first.i) last = cut;
            if (ParseTime(first.s.Dep ?? first.s.Arr) is not { } dep || ParseTime(last.s.Arr ?? last.s.Dep) is not { } arr) continue;
            if (arr < dep) arr += 24 * 3600;
            // 0時〜3時ごろの列車は、日をまたいだ時間として比べる
            int? shift = null;
            foreach (var k in new[] { 0, 24 * 3600, -24 * 3600 })
                if (dep + k < to && arr + k > from) { shift = k; break; }
            if (shift is not { } sh) continue;
            var leaves = last.s.StationId == endId && last.i < d.Stops.Count - 1 && d.Next == null;
            var enters = first.s.StationId == endId && first.i > 0;
            segs[d.Number] = new Seg(d, first.s.Name, last.s.Name, dep + sh, arr + sh, leaves, enters);
        }

        // 運行（同じ編成）のつながり: 列番変更 → 終点の折返
        var next = new Dictionary<string, string>();
        var hasPrev = new HashSet<string>();
        foreach (var s in segs.Values)
        {
            string? n = null;
            if (s.Dia.Next is { } nx && segs.ContainsKey(RailData.NormalizeTrain(nx.Number))) n = RailData.NormalizeTrain(nx.Number);
            else if (!s.LeavesAtEnd && rail.Turns.TurnOf(s.Dia) is { Kind: "折返", Number: { } tn } && segs.ContainsKey(RailData.NormalizeTrain(tn))) n = RailData.NormalizeTrain(tn);
            if (n == null || hasPrev.Contains(n) || segs[n].Dep < s.Arr) continue;
            next[s.Dia.Number] = n;
            hasPrev.Add(n);
        }

        var chains = Cover(segs.Values.OrderBy(s => s.Dep).ToList(), next, minTurnSec, maxTransferSec);

        // 行路番号: 運行番号の順に 道1, 道2, …（特急に乗る行路は 駒1, 駒2, …）
        var named = chains.Select(c => (Chain: c, Unyo: rail.Turns.UnyoOf(c[0].Dia), Express: c.Any(IsExpress)))
            .OrderBy(x => x.Unyo ?? int.MaxValue).ThenBy(x => x.Chain[0].Dep).ToList();
        int nDo = 0, nKoma = 0;
        var csv = new StringBuilder();
        var window = $"{Hm(from)}〜{Hm(to)}";
        csv.AppendLine("行路番号,順,種別,列車番号,開始駅,終了駅,備考");
        csv.AppendLine($"# 運行番号から作った行路（{window}・{StartName}〜{EndName}）。勤怠管理の［運行番号から作る］で作り直すと上書きされます");
        foreach (var (chain, unyo, express) in named)
        {
            var id = express ? $"駒{++nKoma}" : $"道{++nDo}";
            var order = 0;
            Seg? prev = null;
            foreach (var s in chain)
            {
                // 乗っていない間: 同じ編成の折返しを待つのは「待機」、降りてほかの列車を待つのは「渡り」（種別は休憩の行。備考に書く）
                var same = prev != null && next.GetValueOrDefault(prev.Dia.Number) == s.Dia.Number;
                if (prev != null && (!same || s.Dep - prev.Arr >= minTurnSec))
                    csv.AppendLine($"{id},{++order},休憩,,{prev.To},,{(same ? "待機" : "渡り")}{(s.Dep - prev.Arr + 59) / 60}分");
                csv.AppendLine($"{id},{++order},乗務,{s.Dia.Number},{s.From},{s.To},{(prev == null && unyo != null ? $"{unyo}運行" : "")}");
                prev = s;
            }
        }

        return (csv.ToString(), new Result(FileName, named.Count, segs.Count, nKoma, window));
    }

    private const int SwapMinSec = 180;  // 赤山町以外で、ほかの編成へ乗り換えるのに要る時間（3分）
    private const int SameUnitBonus = 5; // 同じ編成に乗り続けるときの、比べる重みの割引（分）
    private const int TypePenalty = 5;   // 特急 ⇔ 特急以外 に乗り換えるときの重み（分）
    private const int DeadheadPenalty = 15; // 営業列車 ⇔ 回送 に乗り換えるときの重み（分）

    private static bool IsExpress(Seg s) => s.Dia.Type == "特急" || s.Dia.Type == "回送" && s.Dia.Number.EndsWith('A');

    /// <summary>
    /// 列車をいちばん少ない運転士で受け持つように並べる（最小路被覆。二部グラフの最小費用最大マッチング）。
    /// 次に乗れる列車: 同じ編成の続き（折返・列番変更）か、着いた駅から出るほかの列車（赤山町は5分、ほかは3分以上あと・休憩の上限まで）。
    /// 人数が同じなら、待ちが短い・同じ編成に乗り続ける・特急と普通、営業と回送をまたがない組み方を選ぶ。
    /// </summary>
    private static List<List<Seg>> Cover(List<Seg> segs, Dictionary<string, string> sameUnit, int minTurnSec, int maxTransferSec)
    {
        var n = segs.Count;
        // 節: 0 = 出発, 1..n = 前の列車, n+1..2n = 次の列車, 2n+1 = 到着
        var S = 0;
        var T = 2 * n + 1;
        var g = new List<int[]>[T + 1]; // [行き先, 容量, 費用, 逆の辺の番号]
        for (var k = 0; k <= T; k++) g[k] = new List<int[]>();
        void Add(int u, int v, int cost)
        {
            g[u].Add([v, 1, cost, g[v].Count]);
            g[v].Add([u, 0, -cost, g[u].Count - 1]);
        }

        const int Base = 100; // 費用を負にしないための底上げ（どの組でも同じだけ足すので、選び方は変わらない）
        for (var i = 0; i < n; i++)
        {
            Add(S, 1 + i, 0);
            Add(1 + n + i, T, 0);
            var a = segs[i];
            var unit = sameUnit.GetValueOrDefault(a.Dia.Number);
            for (var j = 0; j < n; j++)
            {
                if (i == j) continue;
                var b = segs[j];
                var gap = b.Dep - a.Arr;
                var same = unit == b.Dia.Number;
                if (same) { if (gap < 0) continue; }
                else
                {
                    if (a.To != b.From) continue;
                    if (gap < (a.To == EndName ? minTurnSec : SwapMinSec) || gap > maxTransferSec) continue;
                }

                // 渡りは短いほどよい（20分を越える分は2倍に数える）。同じ編成の待機は、待たざるを得ないのでそのまま数える
                var cost = gap / 60 + (same ? -SameUnitBonus : Math.Max(0, gap / 60 - 20))
                           + (IsExpress(a) != IsExpress(b) ? TypePenalty : 0)
                           + ((a.Dia.Type == "回送") != (b.Dia.Type == "回送") ? DeadheadPenalty : 0);
                Add(1 + i, 1 + n + j, Math.Max(0, cost + SameUnitBonus) + Base);
            }
        }

        // 最小費用流（ダイクストラ＋ポテンシャル）。流せるだけ流す = つなぐ数がいちばん多い = 運転士がいちばん少ない
        var pot = new long[T + 1];
        while (true)
        {
            var dist = Enumerable.Repeat(long.MaxValue, T + 1).ToArray();
            var prev = new (int U, int E)[T + 1];
            dist[S] = 0;
            var pq = new PriorityQueue<int, long>();
            pq.Enqueue(S, 0);
            while (pq.TryDequeue(out var u, out var du))
            {
                if (du > dist[u]) continue;
                for (var e = 0; e < g[u].Count; e++)
                {
                    var ed = g[u][e];
                    if (ed[1] <= 0) continue;
                    var nd = du + ed[2] + pot[u] - pot[ed[0]];
                    if (nd >= dist[ed[0]]) continue;
                    dist[ed[0]] = nd;
                    prev[ed[0]] = (u, e);
                    pq.Enqueue(ed[0], nd);
                }
            }

            if (dist[T] == long.MaxValue) break;
            for (var k = 0; k <= T; k++) if (dist[k] != long.MaxValue) pot[k] += dist[k];
            for (var v = T; v != S; v = prev[v].U)
            {
                var ed = g[prev[v].U][prev[v].E];
                ed[1] -= 1;
                g[v][ed[3]][1] += 1;
            }
        }

        // 流れた辺 = 「i の次に j に乗る」
        var succ = new int[n];
        Array.Fill(succ, -1);
        var hasPred = new bool[n];
        for (var i = 0; i < n; i++)
            foreach (var ed in g[1 + i])
                if (ed[0] > n && ed[0] <= 2 * n && ed[1] == 0)
                {
                    succ[i] = ed[0] - 1 - n;
                    hasPred[succ[i]] = true;
                }

        var chains = new List<List<Seg>>();
        for (var i = 0; i < n; i++)
        {
            if (hasPred[i]) continue;
            var list = new List<Seg>();
            for (var k = i; k >= 0; k = succ[k]) list.Add(segs[k]);
            chains.Add(list);
        }

        return chains;
    }

    /// <summary>行路を組んで Data/Duties.{名前}.csv に書く（名前を決めなければ Duties.unyo.csv）。</summary>
    public static Result Write(string dataDir, RailData rail, int from, int to, int maxTransferSec, string? name = null)
    {
        var file = name == null ? FileName : FileNameFor(name, dataDir).File ?? throw new InvalidOperationException(FileNameFor(name, dataDir).Error);
        var (csv, info) = Build(rail, from, to, maxTransferSec: maxTransferSec);
        File.WriteAllText(Path.Combine(dataDir, file), csv, new UTF8Encoding(false));
        return info with { File = file };
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
