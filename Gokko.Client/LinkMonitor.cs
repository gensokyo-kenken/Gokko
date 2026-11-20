using System.Globalization;
using System.Text;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>通信状況の1項目のよしあし。</summary>
public enum LinkLevel
{
    /// <summary>判断しない（未接続にしてある・参考の値）。</summary>
    None,
    Ok,

    /// <summary>遅れ気味。</summary>
    Warn,

    /// <summary>途切れている・つながっていない。</summary>
    Bad
}

/// <summary>通信状況の1行（「通信状況」の窓に出す）。</summary>
public sealed record LinkItem(string Group, string Name, string Value, LinkLevel Level);

/// <summary>
/// 通信状況を集める（0.5秒ごとに <see cref="Update"/> を呼ぶ）。
/// ・自分: サーバーとの往復・配信が届いてからの時間・中継との往復・ゲームのデータ・相手の操作が届いてからの時間・位置のずれ
/// ・相手（同じ列番の運転士・車掌・添乗）: サーバーから届く、相手のクライアントが測った同じ項目
/// 悪くなった・戻ったときは、exe のフォルダの crew-logs に記録し、途切れたときは画面の下でお知らせする。
/// </summary>
public sealed class LinkMonitor : IDisposable
{
    /// <summary>これだけ続いたら、記録する（一瞬の揺れは残さない）。</summary>
    private const int LogAfterMs = 1000;

    /// <summary>途切れがこれだけ続いたら、お知らせする。</summary>
    private const int NoticeAfterMs = 1500;

    /// <summary>同じ項目のお知らせは、これだけ間をあける。</summary>
    private const int NoticeIntervalMs = 10_000;

    /// <summary>ふだんの値を記録する間隔。</summary>
    private const int SummaryMs = 60_000;

    private sealed class Track
    {
        public LinkLevel Level;
        public long SinceMs;
        public bool Logged, Noticed;
        public long LastNoticeMs;
    }

    private readonly CrewRole _role;
    private readonly bool _crewMode;
    private readonly Dictionary<string, Track> _tracks = new();
    private StreamWriter? _log;
    private bool _logFailed;
    private long _lastSummaryMs;

    public LinkMonitor(CrewRole role, bool crewMode)
    {
        _role = role;
        _crewMode = crewMode;
    }

    /// <summary>画面の下のお知らせに出す。</summary>
    public event Action<string>? Notice;

    public IReadOnlyList<LinkItem> Items { get; private set; } = [];

    /// <summary>いちばん悪い項目のよしあし。</summary>
    public LinkLevel Worst { get; private set; } = LinkLevel.None;

    /// <summary>サーバーへの報告に入れる、自分の通信状況。</summary>
    public LinkHealth Health { get; private set; } = new();

    /// <summary>記録のファイル（まだ書いていなければ null）。</summary>
    public string? LogPath { get; private set; }

    /// <summary>記録の置き場（exe のフォルダの crew-logs。運転の記録と同じ）。</summary>
    public static string LogDir => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "crew-logs");

    private const string Me = "自分";

    public void Update(ServerLink server, GameLink game, CrewController? crew, ClientState? state)
    {
        var items = new List<LinkItem>();
        void Add(string group, string name, string value, LinkLevel level) => items.Add(new LinkItem(group, name, value, level));

        // ---- 自分: サーバー ----
        var joined = server.IsJoined;
        if (server.Status == ServerStatus.Stopped) Add(Me, "サーバー", "未接続", LinkLevel.None);
        else if (server.Status == ServerStatus.Rejected) Add(Me, "サーバー", "断られました", LinkLevel.Bad);
        else if (!joined) Add(Me, "サーバー", "つながっていません（つなぎ直しています）", LinkLevel.Bad);
        else
        {
            var pending = server.ReportPendingMs;
            if (pending > 1000) Add(Me, "サーバーとの往復", $"返事を {Sec(pending)} 待っています", pending > 3000 ? LinkLevel.Bad : LinkLevel.Warn);
            else Add(Me, "サーバーとの往復", Ms(server.RttMs), RttLevel(server.RttMs));
            var age = server.StateAgeMs;
            Add(Me, "サーバーからの配信", age < 0 ? "まだ届いていません" : $"{Sec(age)}前", age is < 0 or > 5000 ? LinkLevel.Bad : age > 2000 ? LinkLevel.Warn : LinkLevel.Ok);
        }

        if (server.ReconnectCount > 0) Add(Me, "サーバーにつなぎ直した回数", $"{server.ReconnectCount} 回", LinkLevel.None);

        // ---- 自分: ゲーム ----
        if (game.Stopped) Add(Me, "TRAIN CREW", "切断中", LinkLevel.None);
        else if (!game.IsConnected) Add(Me, "TRAIN CREW", "つながっていません", LinkLevel.Bad);
        else
        {
            var g = game.SecondsSinceData * 1000;
            var where = game.Latest?.InGame == true ? "運転中" : "運転画面の外";
            Add(Me, "TRAIN CREW", $"{where}（データ {Sec((long)g)}前）", g > 5000 ? LinkLevel.Bad : g > 2000 ? LinkLevel.Warn : LinkLevel.Ok);
        }

        // ---- 自分: 中継（運転士と車掌の間） ----
        if (crew != null && joined)
        {
            if (!crew.CrewConnected) Add(Me, "中継（運転士と車掌の間）", crew.CrewMessage.Length > 0 ? crew.CrewMessage : "つながっていません", LinkLevel.Bad);
            else Add(Me, "中継との往復", Ms(crew.CrewRttMs), RttLevel(crew.CrewRttMs));

            var st = crew.CrewStatus;
            var expect = _role == CrewRole.Driver ? st is { Conductors: > 0 } : crew.CrewJoined && st is { Drivers: > 0 };
            if (expect)
            {
                var a = crew.PeerInputAgeMs;
                // 運転士へは車掌が1秒ごとに、車掌・添乗へは運転士が0.1〜0.5秒ごとに送る
                var (warn, bad) = _role == CrewRole.Driver ? (2500, 4000) : (1000, 3000);
                var gap = crew.PeerArrivalGapMs;
                var value = a < 0 ? "まだ届いていません"
                    : $"{Sec(a)}前" + (_role != CrewRole.Driver && gap > 0 ? $"（間隔 {gap} ms）" : "");
                Add(Me, _role == CrewRole.Driver ? "車掌の操作" : "運転士の操作", value,
                    a < 0 || a > bad ? LinkLevel.Bad : a > warn ? LinkLevel.Warn : LinkLevel.Ok);
            }

            if (!double.IsNaN(crew.LastGap))
            {
                var abs = Math.Abs(crew.LastGap);
                Add(Me, _role == CrewRole.Driver ? "位置のずれ（車掌が前ならプラス・粗い）" : "位置のずれ（自分が前ならプラス）",
                    string.Create(CultureInfo.InvariantCulture, $"{crew.LastGap:+0.0;-0.0} m"),
                    abs > 150 ? LinkLevel.Bad : abs > 50 ? LinkLevel.Warn : LinkLevel.Ok);
            }
        }

        // ---- 相手（同じ列番の運転士・車掌・添乗） ----
        if (joined && state?.CrewPeers is { Count: > 0 } peers)
        {
            var riderNo = 0;
            var riders = peers.Count(p => p.Role == CrewRole.Rider);
            foreach (var p in peers.OrderBy(p => p.Role switch { CrewRole.Driver => 0, CrewRole.Conductor => 1, _ => 2 }))
            {
                var group = p.Role switch
                {
                    CrewRole.Driver => "運転士",
                    CrewRole.Conductor => "車掌",
                    _ => (riders > 1 ? $"添乗 {++riderNo}" : "添乗") + (p.Passenger ? "（乗客）" : "")
                };
                Add(group, "サーバーへの報告", $"{Sec(p.ReportAgeMs)}前", p.ReportAgeMs > 5000 ? LinkLevel.Bad : p.ReportAgeMs > 2000 ? LinkLevel.Warn : LinkLevel.Ok);
                Add(group, "TRAIN CREW", !p.TrainCrewConnected ? "つながっていません" : p.InGame ? "運転中" : "運転画面の外",
                    !p.TrainCrewConnected ? LinkLevel.Bad : p.InGame ? LinkLevel.Ok : LinkLevel.Warn);
                if (p.Link is not { } l) continue;
                if (l.GameDataAgeMs > 2000) Add(group, "ゲームのデータ", $"{Sec(l.GameDataAgeMs)}前", l.GameDataAgeMs > 5000 ? LinkLevel.Bad : LinkLevel.Warn);
                Add(group, "サーバーとの往復", Ms(l.ServerRttMs), RttLevel(l.ServerRttMs));
                // 乗務に関わるのは運転士と、車掌・添乗のうち自分と組む側だけ（添乗どうしの中継は見ない）
                var related = _role == CrewRole.Driver || p.Role == CrewRole.Driver;
                if (!related || p.Passenger) continue;
                Add(group, "中継との往復", l.CrewRttMs < 0 ? "つながっていません" : Ms(l.CrewRttMs), l.CrewRttMs < 0 ? LinkLevel.Bad : RttLevel(l.CrewRttMs));
                if (l.CrewJoined && l.PeerInputAgeMs >= 0)
                {
                    var (warn, bad) = p.Role == CrewRole.Driver ? (2500, 4000) : (1000, 3000);
                    Add(group, p.Role == CrewRole.Driver ? "車掌の操作が届いてから" : "運転士の操作が届いてから", $"{Sec(l.PeerInputAgeMs)}前",
                        l.PeerInputAgeMs > bad ? LinkLevel.Bad : l.PeerInputAgeMs > warn ? LinkLevel.Warn : LinkLevel.Ok);
                }
            }
        }

        Health = new LinkHealth
        {
            ServerRttMs = server.RttMs,
            CrewRttMs = crew?.CrewRttMs ?? -1,
            GameDataAgeMs = game.IsConnected ? (int)Math.Min(game.SecondsSinceData * 1000, int.MaxValue) : -1,
            PeerInputAgeMs = crew == null ? -1 : (int)Math.Clamp(crew.PeerInputAgeMs, -1, int.MaxValue),
            CrewJoined = crew?.CrewJoined ?? false
        };
        Items = items;
        Worst = items.Count == 0 ? LinkLevel.None : items.Max(i => i.Level);
        Watch(items, joined);
    }

    // =====================================================================================
    // 記録とお知らせ
    // =====================================================================================

    private void Watch(List<LinkItem> items, bool joined)
    {
        var now = Environment.TickCount64;
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            if (item.Level == LinkLevel.None) continue;
            var key = item.Group == Me ? item.Name : $"{item.Group}の{item.Name}";
            seen.Add(key);
            if (!_tracks.TryGetValue(key, out var t)) _tracks[key] = t = new Track { Level = LinkLevel.Ok, SinceMs = now };

            if (item.Level != t.Level)
            {
                if (item.Level < t.Level)
                {
                    // よくなった（途切れ→遅れ気味も、途切れの終わりとして扱う）
                    if (t.Logged) Write($"戻った　{key}: {item.Value}（{Sec(now - t.SinceMs)}続いた）");
                    if (t.Noticed) Notice?.Invoke($"通信: {key}が戻りました（{Sec(now - t.SinceMs)}途切れていました）");
                }

                t.Logged = t.Noticed = false;
                t.Level = item.Level;
                t.SinceMs = now;
            }

            if (t.Level == LinkLevel.Ok) continue;
            var lasted = now - t.SinceMs;
            if (!t.Logged && lasted >= LogAfterMs)
            {
                Write($"{(t.Level == LinkLevel.Bad ? "途切れ" : "遅れ　")}　{key}: {item.Value}");
                t.Logged = true;
            }

            if (t.Level == LinkLevel.Bad && !t.Noticed && lasted >= NoticeAfterMs && now - t.LastNoticeMs >= NoticeIntervalMs)
            {
                // 「6.0秒前」は「6.0秒 届いていません」と言い直す
                var i = item.Value.IndexOf("秒前", StringComparison.Ordinal);
                var what = i >= 0 ? item.Value[..(i + 1)] + " 届いていません" : item.Value;
                Notice?.Invoke($"通信: {key}が {what}");
                t.Noticed = true;
                t.LastNoticeMs = now;
            }
        }

        // 表示から消えた項目（相手が抜けた・接続をやめた）は、記録だけ締める
        foreach (var key in _tracks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            if (_tracks[key].Logged) Write($"終わり　{key}（項目がなくなりました）");
            _tracks.Remove(key);
        }

        // ふだんの値も、ときどき残す（あとで遅れの目安と比べるため）
        if (joined && now - _lastSummaryMs >= SummaryMs)
        {
            _lastSummaryMs = now;
            Write("ようす　" + string.Join("　", items.Where(i => i.Group == Me).Select(i => $"{i.Name} {i.Value}"))
                  + (items.Any(i => i.Group != Me) ? "　｜　" + string.Join("　", items.Where(i => i.Group != Me).Select(i => $"{i.Group}の{i.Name} {i.Value}")) : ""));
        }
    }

    private void Write(string line)
    {
        if (_logFailed) return;
        try
        {
            if (_log == null)
            {
                Directory.CreateDirectory(LogDir);
                var role = _role switch { CrewRole.Conductor => "車掌", CrewRole.Rider => "添乗", _ => _crewMode ? "運転士" : "特発のみ" };
                LogPath = Path.Combine(LogDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_通信_{role}.log");
                _log = new StreamWriter(LogPath, false, new UTF8Encoding(true)) { AutoFlush = true };
            }

            _log.WriteLine($"{DateTime.Now:HH:mm:ss.f}　{line}");
        }
        catch
        {
            _logFailed = true; // 書けない場所（読み取り専用のフォルダなど）なら、記録はあきらめる
        }
    }

    public void Dispose()
    {
        try { _log?.Dispose(); } catch { }
        _log = null;
    }

    // =====================================================================================

    private static LinkLevel RttLevel(int ms) => ms < 0 ? LinkLevel.None : ms > 1000 ? LinkLevel.Bad : ms > 300 ? LinkLevel.Warn : LinkLevel.Ok;

    private static string Ms(int ms) => ms < 0 ? "測っています" : $"{ms} ms";

    private static string Sec(long ms) => string.Create(CultureInfo.InvariantCulture, $"{ms / 1000.0:0.0}秒");
}
