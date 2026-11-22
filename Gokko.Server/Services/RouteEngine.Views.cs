using System.Collections.Concurrent;
using System.Diagnostics;

namespace Gokko.Server.Services;

/// <summary>
/// 画面用の答えの作り置き。
/// 在線位置の画面・ダイヤグラムなどの API が、そのつど RouteEngine のロックを取って作ると、CPU を取り上げられて
/// Tick が遅くなったときに、ロックを待つ要求がスレッドを抱えたまま積み重なる（11/22 の運転会でスレッドが 18 → 132、配信が最大 20 秒遅れた）。
/// そこで、配信のループが Tick のすぐあとに <see cref="BuildViews"/> で1回だけロックを取って作り、画面の API はそれを返すだけにする。
/// 見ている画面がない答えは作らない（最後に読まれてから <see cref="WantedFor"/> まで作る）。
/// </summary>
public sealed partial class RouteEngine
{
    /// <summary>作り置きの種類。</summary>
    public const string ViewTid = "tid", ViewOrders = "orders", ViewSections = "sections", ViewDiagram = "diagram",
        ViewQueue = "queue", ViewAppear = "appear", ViewConnections = "connections";

    /// <summary>最後に読まれてからこの間は、作り置きを作り続ける。</summary>
    private static readonly TimeSpan WantedFor = TimeSpan.FromSeconds(5);

    /// <summary>作り置きがこれより古ければ使わず、その場で作る（配信のループが止まっているときなど）。</summary>
    private static readonly TimeSpan ViewMaxAge = TimeSpan.FromSeconds(2);

    private sealed record ViewSet(long At, Dictionary<string, object> Items);

    private readonly ConcurrentDictionary<string, long> _viewWanted = new();
    private volatile ViewSet? _views;
    private long _viewGen; // ClearViews で増やす（作っている途中に捨てられたら、作ったものは置かない）

    private Func<object> ViewBuilder(string kind) => kind switch
    {
        ViewTid => TidView,
        ViewOrders => OrdersView,
        ViewSections => SectionsView,
        ViewDiagram => DiagramView,
        ViewQueue => QueueView,
        ViewAppear => AppearView,
        ViewConnections => ConnectionsView,
        _ => throw new ArgumentException($"作り置きの種類が違います: {kind}", nameof(kind))
    };

    /// <summary>
    /// 画面用の答え。新しい作り置きがあればそれを（ロックを取らない）、なければその場で作る。
    /// 返すものは作ったあと誰も書き換えないので、何人で同時に JSON にしてもよい。
    /// </summary>
    public object View(string kind)
    {
        var now = Stopwatch.GetTimestamp();
        _viewWanted[kind] = now;
        var set = _views;
        if (set != null && Stopwatch.GetElapsedTime(set.At, now) < ViewMaxAge && set.Items.TryGetValue(kind, out var item)) return item;
        return ViewBuilder(kind)();
    }

    /// <summary>
    /// 作り置きを作り直す（配信のループが Tick のすぐあとに呼ぶ）。見ている画面がある種類だけを、1回のロックでまとめて作る。
    /// 戻り値は作った種類の数。
    /// </summary>
    public int BuildViews()
    {
        var now = Stopwatch.GetTimestamp();
        var kinds = _viewWanted.Where(kv => Stopwatch.GetElapsedTime(kv.Value, now) < WantedFor).Select(kv => kv.Key).ToList();
        if (kinds.Count == 0)
        {
            _views = null;
            return 0;
        }

        var gen = Interlocked.Read(ref _viewGen);
        var items = new Dictionary<string, object>();
        lock (_lock) // 各 View も同じロックを取る（入れ子でもよい）。まとめて取って、同じ時点の答えにそろえる
            foreach (var kind in kinds)
                items[kind] = ViewBuilder(kind)();
        if (Interlocked.Read(ref _viewGen) == gen) _views = new ViewSet(now, items);
        return items.Count;
    }

    /// <summary>作り置きを捨てる（操作のすぐあと。◆をずらしたなどが、次に読んだときにすぐ見えるように、次はその場で作る）。</summary>
    public void ClearViews()
    {
        Interlocked.Increment(ref _viewGen);
        _views = null;
    }
}
