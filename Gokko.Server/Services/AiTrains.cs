using System.Globalization;
using System.Text;
using System.Text.Json;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// にぎやかしの列車（サーバーだけで走る列車）。ゲームを持たない参加者として <see cref="PlayerRegistry"/> に載せる。
///
/// ・進路は、プレイヤーの列車と同じく <see cref="RouteEngine"/> が決める。決まった進路は、すぐ開通したものとして扱う
///   （本物のゲームの代わり。開通の判断は RouteEngine が在線・ほかの進路を見て済ませている）。開けた進路は、ほかのプレイヤーのゲームにも写る
/// ・在線は、ほかのプレイヤーへの信号（SignalCalculator の R）・TID・信号制御盤に、ふつうの列車として出る
/// ・自分の信号は、プレイヤーに配るのと同じもの（<see cref="StateBroadcaster"/> の ClientState）を受け取る。
///   閉塞信号機は R でなければ進む。駅の信号機は、自分の進路が開いていて R でなければ進む
/// ・走る道筋は、信号機の「次の信号機」（Signals.json の next）と進路の軌道回路（Interlockings.json）をつないで作る
/// ・距離は Data/AiTrains.json（ゲームの自動運転で走ったときの信号機の位置・番線ごとの停止位置・軌道回路の長さの見込み）。
///   ない所は、軌道回路の長さを決め打ちにする
/// ・速さは、次の停車駅の時刻に間に合うように決める（残り距離 ÷ 残り時分 × 1.1）。上限は <see cref="LimitCap"/>（区間の最高・線路の制限・分岐 40km/h）
///
/// 決まり（2026-11-20 ユーザー。使うのは基本的にダイヤ運転モード）
/// ・自動進路構成（RouteEngine）とは切り離す。自動進路構成は、列車番号ごとに決められたタイミングで決められたように進路・信号を出すだけで、
///   にぎやかしのために変えない。にぎやかしは、時刻・司令員の指示・信号に従って運転するだけ（番線・順序・抑止は、信号と進路に表れる）
/// ・自分で番線を選ぶのは出すときだけ。ダイヤ運転モードではダイヤの番線にだけ出し、空いていなければ待つ。時刻を過ぎた駅には出さない（順序を崩さない）
/// ・ユーザーの列車（出勤した行路の列車・入力された列番）は出さない。ユーザーがその列番で出てきたら、途中駅からでも消してユーザーに運転させる
/// </summary>
public sealed class AiTrains : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);
    private const double Accel = 0.65;          // m/s²（約 2.3km/h/s）
    private const double Decel = 0.70;          // m/s²（約 2.5km/h/s）
    private const double SignalMargin = 8;      // R の信号機の何 m 手前で止まるか
    private const double CarLength = 20;
    private const double DefaultStationCircuit = 150;
    private const double DefaultLineCircuit = 450;
    private const int MinDwellSeconds = 15;
    private const double ShuntSpeed = 25 / 3.6; // 入換の速さ

    // ---- 赤山町（ゲームの線区の三郷方の端） ----
    /// <summary>上りがここに入ったら、ExitSeconds（ロープレ時刻の秒）で消す: 9LT（三郷方の出口）・2RAT（1番線）。</summary>
    private static readonly HashSet<string> ExitCircuits = new() { "TH58_9LT", "TH58_2RAT" };
    private const double ExitSeconds = 20;
    /// <summary>赤山町を2番線で通過する下りは、通過時刻の EntryLeadSeconds 前に 3番線（9LCT）に出す。</summary>
    private const string EntryStation = "TH58";
    private const int EntryTrack = 3;
    private const double EntryLeadSeconds = 20;
    private static readonly TimeSpan TerminalLinger = TimeSpan.FromSeconds(60);
    /// <summary>ダイヤ運転中、継続乗務の在線がこれだけ残ったら、自動運転の列車として引き継ぐ。</summary>
    private static readonly TimeSpan GhostTakeOverAfter = TimeSpan.FromMinutes(3);

    private readonly RailData _rail;
    private readonly PlayerRegistry _players;
    private readonly GameClock _clock;
    private readonly RouteEngine _routes;
    private readonly CentralControl _central;
    private readonly IncidentManager _log;
    private readonly CrewRoster _crew;
    private readonly DutyRosters _duties;
    private readonly CrewRelay _relay;
    private readonly ILogger<AiTrains>? _logger;

    private readonly Dictionary<string, HashSet<string>> _adj;
    private readonly Dictionary<string, List<RouteDef>> _routesBySignal;
    private readonly List<Dictionary<string, double>> _signalRuns = new();
    private readonly List<List<(double Pos, double Kmh)>> _limitRuns = new(); // _signalRuns と同じ並び。制限速度が変わった地点
    private readonly List<Dictionary<string, int>> _trackRuns = new(); // _signalRuns と同じ並び。その走りで止まった駅の番線
    private readonly List<StopSpec> _stops = new();
    private readonly Dictionary<string, double> _circuitLengths = new();
    private readonly Dictionary<int, int> _carsByUnyo = new(); // 運行番号ごとの両数（ない運行は6両）
    private Dictionary<string, Dictionary<string, string>> _couplePartners = new(); // 連結: 列車ID → 駅ID → 番線で待つ増結車（RouteEngine から周期ごとに写す）
    private static readonly TimeSpan CoupleStopB = TimeSpan.FromSeconds(5); // 連結: 停B で止まっている時間
    private readonly List<(string Child, int Cars, string Station, int Track, string Parent)> _pendingSplits = new(); // 解放: これから出す車両

    private readonly object _lock = new();
    private readonly Dictionary<string, Train> _trains = new();
    private int _seq;

    // ---- 自動運転列車アリ/ナシ（アリなら、ダイヤの列車を時刻に合わせて自動で出す） ----
    private bool _enabled;
    private readonly HashSet<string> _errorLogged = new(); // 例外を記録した列車（同じ列車で何度も書かない）
    private readonly HashSet<string> _spawned = new(); // 自動で出した・続けて走った列番（同じ列車を二度出さない）
    private HashSet<string> _userTrains = new();       // ユーザーの列車（出勤した行路の列車・入力した列番）
    private HashSet<string> _userInGame = new();       // 運転画面に出ているユーザーの列番
    private DateTime _lastSpawnUtc;
    private TimeSpan? _lastGame;
    private readonly HashSet<string> _takeOverLogged = new(); // 引き継げなかったことを記録した継続乗務の在線（同じものを何度も書かない）

    public AiTrains(string dataDir, RailData rail, PlayerRegistry players, GameClock clock, RouteEngine routes, CentralControl central,
        IncidentManager log, CrewRoster crew, DutyRosters duties, CrewRelay relay, ILogger<AiTrains>? logger = null)
    {
        _crew = crew;
        _duties = duties;
        _relay = relay;
        _rail = rail;
        _players = players;
        _clock = clock;
        _routes = routes;
        _central = central;
        _log = log;
        _logger = logger;
        _adj = LoadAdjacency(Path.Combine(dataDir, "TrackCircuits.json"));
        _routesBySignal = rail.Routes.Values.Where(r => r.Signal.Length > 0).GroupBy(r => r.Signal).ToDictionary(g => g.Key, g => g.ToList());
        LoadDistances(Path.Combine(dataDir, "AiTrains.json"));
        players.VirtualTrains = Snapshot;
        players.GhostDeleted += OnGhostDeleted;
    }

    // =========================================================================================
    // 出す・消す・一覧（管理画面から）
    // =========================================================================================

    public sealed record View(string Id, string Train, int Cars, string Where, double SpeedKmh, string Status, bool Auto);

    /// <summary>自動運転列車アリか（ダイヤの列車を時刻に合わせて自動で出す）。</summary>
    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
    }

    /// <summary>自動運転列車のアリ/ナシ。ナシにしたら、走っている列車も全部消す。</summary>
    public void SetEnabled(bool on)
    {
        lock (_lock)
        {
            if (_enabled == on) return;
            _enabled = on;
            _spawned.Clear();
            if (!on) _trains.Clear();
        }

        _log.Note(on ? "にぎやかし: 自動運転列車アリにしました（ダイヤの列車を時刻に合わせて出します）" : "にぎやかし: 自動運転列車ナシにしました（走っていた列車は消しました）");
    }

    public List<View> List()
    {
        lock (_lock)
            return _trains.Values.OrderBy(t => t.Seq).Select(t => new View(t.Id, t.Shown ?? t.Number, t.Cars,
                _rail.Line.DescribeZones(t.Info.Zones) ?? string.Join(" ", t.Info.TrackCircuits), Math.Round(t.Speed * 3.6, 1), t.Status, t.Auto)).ToList();
    }

    /// <summary>ダイヤの列車を、これから発車する最初の駅（時刻が来ていない、進路を扱う駅）に出す。</summary>
    public (bool Ok, string Message) Add(string number, int cars, string? stationName) => Add(number, cars, stationName, false);

    private (bool Ok, string Message) Add(string number, int cars, string? stationName, bool auto, int? forceTrack = null, PlayerInfo? takesOver = null)
    {
        number = RailData.NormalizeTrain(number);
        if (!auto && UserTrains(_players.Snapshot()).Contains(number)) return (false, $"{number} はユーザーの列車です（出勤した行路の列車・入力された列番）");
        if (_rail.DiaOf(number) is not { } dia) return (false, $"{number}: ダイヤにない列車です");
        var dir = _routes.DirOfTrain(number);
        if (dir == null) return (false, $"{number}: 列車番号の形が違います");
        cars = Math.Clamp(cars <= 0 ? 6 : cars, 1, 10);
        var now = _clock.Now;

        lock (_lock)
        {
            if (_trains.Values.Any(t => t.Number == number)) return (false, $"{number} はもう走っています");
            if (_players.Snapshot().Any(p => RailData.NormalizeTrain(p.TrainNumber) == number && !p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix)))
                return (false, $"{number} はプレイヤーが運転しています");

            // 出す駅: 指定があればその駅、なければ時刻がまだ来ていない最初の駅（進路を扱う駅で、番線が分かる所）
            var stops = dia.Stops.Where(s => s.StationId != null && _rail.Stations.TryGetValue(s.StationId, out var sd) && sd.RouteControl).ToList();
            DiaStop? start = null;
            foreach (var s in stops)
            {
                if (stationName is { Length: > 0 } && s.Name != stationName) continue;
                if (stationName is not { Length: > 0 } && now is { } n && Time(s.Dep ?? s.Arr) is { } t && t < n) continue;
                if (s == stops.LastOrDefault() && stationName is not { Length: > 0 }) continue; // 終着には出さない
                if (stationName is not { Length: > 0 } && PassesWithoutTrack(s, dir.Value)) continue;
                start = s;
                break;
            }

            if (start == null) return (false, stationName is { Length: > 0 } ? $"{number} は {stationName} を通りません" : $"{number}: これから発車する駅がありません（ロープレ時刻を確かめてください）");
            var st = _rail.Stations[start.StationId!];
            // 番線: ダイヤの番線。ほかの列車がいる・ほかの列車の進路がかかっていれば、同じ向きに出られるほかの番線
            // （ほかの列車の進路の上に出すと、その列車も自分も動けなくなる。RouteEngine も「出現待ち」にする）
            // 切り離した車両（解2076C）は、元の列車（2076C）がいる番線に出す
            var others = _players.Snapshot().Where(p => !(number.StartsWith("解") && RailData.NormalizeTrain(p.TrainNumber) == number[1..])).ToList();
            var occupied = others.SelectMany(p => p.TrackCircuits).ToHashSet();
            foreach (var p in others)
            foreach (var key in p.GameRoutes)
                if (_rail.Routes.TryGetValue(key, out var rd)) occupied.UnionWith(rd.Circuits);
            var options = new List<int>();
            if (forceTrack is { } ft) options.Add(ft);
            else if (start.Track is { } dt) options.Add(dt);
            // ダイヤ運転モードでは、ダイヤの番線だけ（番線を変えない。空くまで待つ）。番線の指定があれば、その番線だけ
            if (forceTrack == null && (!_central.DiaMode || options.Count == 0)) options.AddRange(st.Routes[dir.Value].Depart.Keys.Order().Where(k => !options.Contains(k)));
            if (options.Count == 0) options.AddRange(st.Routes[dir.Value].Arrive.Keys.Order());
            var track = options.FirstOrDefault(k => st.TrackCircuit.TryGetValue(k, out var c) && !occupied.Contains(c), -1);
            if (track < 0) return (false, $"{st.Name}: 出せる番線がありません（ほかの列車・進路があります）");
            var platform = st.TrackCircuit[track];

            var train = new Train
            {
                Id = $"{PlayerRegistry.AiPrefix}{++_seq}", Seq = _seq, Number = number, Dia = dia, Dir = dir.Value, Cars = cars,
                Length = cars * CarLength, StartStation = st.Name, Auto = auto
            };
            // 停車・通過の一覧（出す駅から先。出す駅は、通過の駅でも止まって発車時刻を待つ）
            var from = dia.Stops.IndexOf(start);
            foreach (var s in dia.Stops.Skip(from))
            {
                if (s.StationId == null || !InArea(s.StationId)) continue;
                train.Plan.Add(new PlanStop(s.Name, s.StationId, s.Stop == "停" || s == start, s.Track, Time(s.Arr), Time(s.Dep)));
            }

            // 最初の道筋: 出す番線の軌道回路。停止位置は、番線ごとの表（出発信号機の手前）から
            // 表の駅名はダイヤの駅名（江原検など、駅の設定の名前と違う所がある）。なければ駅の設定の名前
            var stopSpec = FindStop(start.Name, dir.Value, track) ?? FindStop(st.Name, dir.Value, track);
            var before = stopSpec?.Before ?? 20;
            var len = Math.Max(LengthOf(platform), train.Length + before + 10);
            train.Path.Add(new Seg(null, null, 0, len, [new SegCircuit(platform, 0, len)]));
            train.Head = len - before;
            train.StartHead = train.Head;
            train.StartTrack = track;
            train.Info = new PlayerInfo
            {
                ConnectionId = train.Id, Mode = ShareMode.Shared, InGame = true, TrainCrewConnected = true, TrainNumber = number,
                TrainNumberTyped = true, JoinedUtc = DateTime.UtcNow, LastReportUtc = DateTime.UtcNow, TakesOver = takesOver?.ConnectionId,
                // 引き継いだ在線の進路（ゲームに開通している進路）も引き継ぐ。空のまま始めると、RouteEngine が「ゲームで解除された」と見て、その進路を捨てる
                GameRoutes = takesOver != null ? new HashSet<(string, string)>(takesOver.GameRoutes) : new HashSet<(string, string)>(),
                Stops = train.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList()
            };
            train.Status = $"{st.Name} {track}番線で発車待ち";
            UpdateInfo(train);
            _trains[train.Id] = train;
            _spawned.Add(number);
            _log.Note($"にぎやかし: {number}（{cars}両）を {st.Name} {track}番線に出しました" + (takesOver != null ? "（継続乗務の在線を引き継ぎ）" : auto ? "（自動）" : ""));
            return (true, $"{number} を {st.Name} {track}番線に出しました");
        }
    }

    public bool Remove(string id)
    {
        Train? t;
        lock (_lock)
            if (!_trains.Remove(id, out t)) return false;
        _log.Note($"にぎやかし: {t.Number} を消しました");
        return true;
    }

    public void RemoveAll()
    {
        lock (_lock) _trains.Clear();
    }

    private IEnumerable<PlayerInfo> Snapshot()
    {
        lock (_lock) return _trains.Values.Select(t => t.Info.Clone()).ToList();
    }

    // =========================================================================================
    // サーバーからの「配信」（プレイヤーのクライアントが受け取るものと同じ）
    // =========================================================================================

    public void Receive(string id, ClientState state)
    {
        lock (_lock)
        {
            if (!_trains.TryGetValue(id, out var t)) return;
            t.Received = true;
            t.RedSignals = state.Signals.Where(s => s.Phase == "R").Select(s => s.Name).ToHashSet();
            foreach (var r in state.Routes) t.Info.GameRoutes.Add((r.Interlocking, r.RouteName));
            foreach (var r in state.DeleteRoutes)
            {
                // 入ってしまった進路は消さない（ゲームの接近鎖錠の代わり）。まだ入っていなければ、道筋もそこまでに縮める
                var i = t.Path.FindIndex(s => s.Route is { } d && d.Interlock == r.Interlocking && d.Name == r.RouteName);
                if (i >= 0 && t.Path[i].Start <= t.Head) continue;
                // 接近の区間にいる進路も消さない（ゲームなら接近鎖錠で断られる）。駒野の第二出発2L は、手前の 6LT が駒野〜館浜の区間の回路なので、
                // 最後尾が 9LCT を出ると自動進路構成は駒野を抜けたとして消しにくるが、列車はまだ 6LT を走っていて、これから 2L に入る
                // 駅で停車中なら消す（ホームで出発を待つ列車は、出発の進路の接近の区間にいる。自動進路構成が先行列車に譲らせた進路を
                // 持ち続けると、先行列車がその進路を取れない）
                if (i >= 0 && !t.Dwelling && t.Path[i].Route is { Approach.Count: > 0 } ap && t.Info.TrackCircuits.Any(ap.Approach.Contains)) continue;
                if (i > 0) t.Path.RemoveRange(i, t.Path.Count - i);
                t.Info.GameRoutes.Remove((r.Interlocking, r.RouteName));
            }
            t.CanRequestDeparture = state.CanRequestDeparture;
            t.RouteMessage = state.RouteMessage;
            // この先の駅の状態（進路待ちの理由など。プレイヤーには出さない詳しい状態）。止まっているときの理由に出す
            t.PlanDetail = state.Plan.Where(x => !string.IsNullOrEmpty(x.Detail)).Select(x => $"{x.Name}: {x.Detail}").FirstOrDefault();
        }
    }

    // =========================================================================================
    // 周期の処理
    // =========================================================================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTime.UtcNow;
            var dt = Math.Clamp((now - last).TotalSeconds, 0, 1);
            last = now;
            try
            {
                // ユーザーの列車（RouteEngine などのロックの中から呼ばないように、自分のロックの外で集める）
                var snapshot = _players.Snapshot();
                var users = UserTrains(snapshot);
                var inGame = snapshot.Where(p => !PlayerRegistry.IsVirtual(p.ConnectionId) && p.InGame && p.TrainNumber.Length > 0)
                    .Select(p => RailData.NormalizeTrain(p.TrainNumber)).ToHashSet();

                List<string> departures = new();
                var couplePartners = _routes.CouplePartners();
                var coupled = _routes.TakeCoupled();
                var splits = _routes.TakeSplits();
                List<(string Child, int Cars, string Station, int Track, string Parent)> splitSpawns = new();
                lock (_lock)
                {
                    _userTrains = users;
                    _userInGame = inGame;
                    _couplePartners = couplePartners;
                    // ユーザーの列車が連結を終えたら、番線で待っていた増結車（自動運転）を消す
                    foreach (var (partner, train) in coupled)
                        foreach (var p in _trains.Values.Where(x => x.Number == partner).ToList())
                        {
                            _trains.Remove(p.Id);
                            _log.Note($"にぎやかし: {partner} は {train} に連結したので消しました");
                        }
                    // 解放: 元の列車（自動運転）の両数を減らし、切り離した車両を同じ番線に出す（ユーザーが運転するなら出さない）
                    foreach (var (child, parent, station, track) in splits)
                    {
                        var childCars = CarsOf(child);
                        foreach (var p in _trains.Values.Where(x => x.Number == parent))
                        {
                            p.Cars = Math.Max(CarsOf(parent), p.Cars - childCars);
                            p.Length = p.Cars * CarLength;
                        }
                        if (users.Contains(RailData.NormalizeTrain(child)) || _trains.Values.Any(x => x.Number == child)) continue;
                        splitSpawns.Add((child, childCars, station, track, parent));
                    }
                    foreach (var t in _trains.Values.ToList())
                    {
                        if (!_trains.ContainsKey(t.Id)) continue; // この周期の中で消えた（連結した増結車）
                        // ユーザーがその列番で出てきたら、自動運転の列車は消して、ユーザーに運転してもらう（途中駅からでも）
                        // 上りが赤山町の 9LT（三郷方の出口）か 2RAT（1番線）に入ったら、20秒で消す（その先はゲームの線区の外）
                        if (t.Dir == Dir.Up && t.ExitUtc == null && t.Info.TrackCircuits.Any(ExitCircuits.Contains))
                            t.ExitUtc = now + TimeSpan.FromSeconds(ExitSeconds / Math.Max(0.1, _clock.Speed));
                        if (t.ExitUtc is { } ex && now >= ex)
                        {
                            _trains.Remove(t.Id);
                            _log.Note($"にぎやかし: {t.Number} は赤山町から線区の外へ出たので消しました");
                            continue;
                        }

                        if (inGame.Contains(t.Number))
                        {
                            _trains.Remove(t.Id);
                            _log.Note($"にぎやかし: {t.Number} はユーザーが運転を始めたので消しました");
                            continue;
                        }

                        // 1本の列車で例外が出ても、ほかの列車は動かし続ける（周期ごと止まると、後ろに並ぶ列車がすべて動かなくなる）
                        try
                        {
                            Step(t, dt * _clock.Speed, now);
                        }
                        catch (Exception stepError)
                        {
                            if (_errorLogged.Add(t.Id)) _logger?.LogError(stepError, "にぎやかしの列車 {Train} でエラー", t.Number);
                            continue;
                        }
                        if (t.WantDeparture) { departures.Add(t.Id); t.WantDeparture = false; }
                        if (t.FinishedUtc is { } f && now - f > TerminalLinger)
                        {
                            _trains.Remove(t.Id);
                            _log.Note($"にぎやかし: {t.Number} は {t.Status}。在線を消しました");
                        }
                    }
                }

                // 切り離した車両を出す（Add はほかの参加者の在線を見るので、自分のロックの外で）
                lock (_lock)
                {
                    splitSpawns.AddRange(_pendingSplits);
                    _pendingSplits.Clear();
                }
                foreach (var (child, cars, station, track, parent) in splitSpawns)
                {
                    var (ok, msg) = Add(child, cars, station, true, track);
                    _log.Note(ok ? $"にぎやかし: {parent} から {child}（{cars}両）を切り離しました（{station} {track}番線）" : $"にぎやかし: {child} を出せませんでした（{msg}）");
                }

                // 出発要求（RouteEngine のロックの中から呼ばないように、外で）
                foreach (var id in departures) _routes.RequestDeparture(id);

                // 自動運転列車アリ: 時刻が近づいたダイヤの列車を出す（2秒ごと）
                if (now - _lastSpawnUtc > TimeSpan.FromSeconds(2))
                {
                    _lastSpawnUtc = now;
                    AutoSpawn(users, snapshot);
                    TakeOverStaleGhosts();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "にぎやかしの列車でエラー");
            }
        }
    }

    private void Step(Train t, double dt, DateTime utcNow)
    {
        if (!_clock.IsRunning) dt = 0; // ロープレ時刻が止まっている間は止まる
        Extend(t);
        var nowGame = _clock.Now;

        // 次に止まる駅
        var next = t.Shunting ? null : t.Plan.Skip(t.PlanIndex).FirstOrDefault(p => p.Stops);
        double? stopX = next != null ? StopX(t, next) : null;
        var target = t.PathEnd - (t.Blocked ? SignalMargin : 2);
        if (stopX is { } sx && sx < target + 0.01) target = sx;

        // 止まっている（駅で）
        if (t.Dwelling)
        {
            // 終着で、ダイヤの「次の列番」（大道寺で 1651 → 回1651 など）があれば、その列番の停車駅を続ける。
            // 進路は RouteEngine が列番を読み替えて作る（番線に入りきって止まったら）
            if (!t.Plan.Skip(t.DwellIndex + 1).Any()) ChainNext(t);
            var stopNow = t.Plan[t.DwellIndex];
            var depAt = stopNow.Dep ?? stopNow.Arr;
            var ready = (utcNow - t.ArrivedUtc).TotalSeconds * _clock.Speed >= MinDwellSeconds && (depAt == null || nowGame == null || nowGame >= depAt);
            var terminal = !t.Plan.Skip(t.DwellIndex + 1).Any();
            if (terminal)
            {
                // ダイヤではまだ先へ行く（線区の外へ出ていく）: 出発の進路が開いたら出ていく（赤山町は 9LT に入って20秒で消える）。
                // 進路が開かないまま発車時刻から60秒たったら消す
                if (t.Dia.Stops.LastOrDefault()?.StationId != stopNow.StationId)
                {
                    if (ready && !t.DepartureAsked && !_central.DiaMode) { t.WantDeparture = true; t.DepartureAsked = true; }
                    if (ready && t.PathEnd - (t.Blocked ? SignalMargin : 2) > t.Head + 5)
                    {
                        t.Dwelling = false;
                        t.PlanIndex = t.DwellIndex + 1;
                        t.Status = $"{stopNow.Name} から線区の外へ";
                        UpdateInfo(t);
                        return;
                    }

                    if (ready && (depAt == null || nowGame == null || nowGame - depAt > TimeSpan.FromSeconds(60))) t.FinishedUtc ??= utcNow - TerminalLinger;
                    t.Status = ready ? $"{stopNow.Name} で出発の進路待ち（この先は線区の外）" : $"{stopNow.Name} に停車中（{(depAt is { } d0 ? d0.ToString(@"hh\:mm\:ss") : "")}発。この先は線区の外）";
                    UpdateInfo(t);
                    return;
                }

                // 折返が「入換」: 自動進路構成が開ける入換（引上げ）の進路で引上線へ入り、そこで折り返しの列番になる
                if (_rail.Turns.TurnOf(t.Dia) is { Kind: "折返", How: "入換", Number: { Length: > 0 } sn } && _rail.DiaOf(sn) != null)
                {
                    if (IsUserTrain(sn)) { HandOver(t, sn); return; }

                    // 着いた番線から引上げの入換がなく、次の列番の始発の入換がその番線から出る（江ノ原信号場10番線など）:
                    // 引き上げずに、この番線で列番を変える。入換は次の列番の始発の入換として、自動進路構成が入換時刻に開ける
                    if (_routes.TurnsInPlace(t.Dia))
                    {
                        if (!ready)
                        {
                            t.Status = $"{stopNow.Name} {stopNow.Track}番線に到着（{sn} に列番を変えます）";
                            UpdateInfo(t);
                            return;
                        }
                        t.ShuntStation = stopNow;
                        ShuntReverse(t, inPlace: true);
                        UpdateInfo(t);
                        return;
                    }

                    if (!t.DepartureAsked && !_central.DiaMode) { t.WantDeparture = true; t.DepartureAsked = true; } // 入換要求（ダイヤ運転中は自動）
                    if (t.PathEnd - (t.Blocked ? SignalMargin : 2) > t.Head + 5)
                    {
                        t.Dwelling = false;
                        t.Shunting = true;
                        t.ShuntStartHead = t.Head;
                        t.ShuntStation = stopNow;
                        t.Status = "入換（引上げ）中";
                        UpdateInfo(t);
                        return;
                    }

                    // 自動進路構成が扱う駅がもうない（入換の進路を開けない）まま、
                    // 次の列番の発車1分前になったら、この列車は消す（次の列番は、その時刻にダイヤの番線へ出す）
                    if (t.PlanDetail == null && nowGame is { } ng1 && _rail.DiaOf(sn)?.Stops.FirstOrDefault(s => s.StationId == stopNow.StationId) is { } ns
                        && Time(ns.Dep ?? ns.Arr) is { } nsDep && ng1 >= nsDep - TimeSpan.FromSeconds(60))
                    {
                        t.FinishedUtc ??= utcNow - TerminalLinger;
                        t.Status = $"{stopNow.Name} で入換の進路が開かないので終わり（{sn} は {stopNow.Name} の番線に出します）";
                        UpdateInfo(t);
                        return;
                    }

                    t.Status = $"{stopNow.Name} で入換（引上げ）の進路待ち" + (t.PlanDetail is { } pd1 ? $"（{pd1}）" : "");
                    UpdateInfo(t);
                    return;
                }

                // 増結車（列番が「連」＋連結する列車の列番。連777）: 番線で連結を待つ。連結する列車が来なければ、その列車の発車から10分で消す
                if (t.Number.StartsWith("連") && t.Number[1..] is { Length: > 0 } cn)
                {
                    if (nowGame is { } ng2 && _rail.DiaOf(cn)?.Stops.FirstOrDefault(s => s.StationId == stopNow.StationId) is { } cs
                        && Time(cs.Dep ?? cs.Arr) is { } cdep && ng2 > cdep + TimeSpan.FromMinutes(10))
                    {
                        t.FinishedUtc ??= utcNow - TerminalLinger;
                        t.Status = $"{stopNow.Name} で {cn} が来ないので終わり";
                        return;
                    }
                    t.Status = $"{stopNow.Name} で {cn} の連結待ち";
                    UpdateInfo(t);
                    return;
                }

                // ダイヤの「折返」があれば、その列番に変えて、反対向きに同じ番線から出る
                if (TurnBack(t, utcNow))
                {
                    UpdateInfo(t);
                    return;
                }

                t.FinishedUtc ??= utcNow;
                t.Status = $"{stopNow.Name} に到着（終着）";
                return;
            }

            if (ready && !t.DepartureAsked && !_central.DiaMode) { t.WantDeparture = true; t.DepartureAsked = true; }
            // 出発の進路が開いて、道筋が延びたら発車
            // 信号をまだ受け取っていなければ待つ（道筋が出した番線だけのまま、出発信号機の先まで出てしまう）
            if (ready && t.Received && t.PathEnd - (t.Blocked ? SignalMargin : 2) > t.Head + 5)
            {
                // 次の周期から走る（この周期の「次に止まる駅」は今の駅なので、ここで動かすと、また着いたことになる）
                t.Dwelling = false;
                t.PlanIndex = t.DwellIndex + 1;
                t.FromShunt = false;
                t.CoupleStage = 0;
                t.Status = "走行中";
                UpdateInfo(t);
                return;
            }
            else
            {
                t.Status = ready ? $"{stopNow.Name} で出発の進路待ち" + (t.PlanDetail is { } pd0 ? $"（{pd0}）" : "") : $"{stopNow.Name} に停車中（{(depAt is { } d ? d.ToString(@"hh\:mm\:ss") : "")}発）";
                UpdateInfo(t);
                return;
            }
        }

        // 最初の駅: 発車時刻まで待つ
        if (t.PlanIndex == 0 && t.Speed == 0 && t.Plan.Count > 0 && Math.Abs(t.Head - t.StartHead) < 0.5 && !t.FromShunt && !t.MidLine && !t.Shunting) // 入換（引上げ）を始めた列車は除く（出したばかりの番線から、すぐ引き上げる 解2076C など）
        {
            t.Dwelling = true;
            t.DwellIndex = 0;
            t.ArrivedUtc = utcNow.AddSeconds(-MinDwellSeconds);
            UpdateInfo(t);
            return;
        }

        // 速さ: 次の停車駅の時刻に間に合うように
        var cruise = 75 / 3.6;
        if (next != null && nowGame is { } ng && (next.Arr ?? next.Dep) is { } due)
        {
            var remain = (due - ng).TotalSeconds;
            var dist = stopX is { } x ? x - t.Head : EstimateDistance(t, next);
            if (dist is { } dd && dd > 0)
                cruise = remain <= 10 ? 110 / 3.6 : Math.Clamp(dd / remain * 1.1, 35 / 3.6, 110 / 3.6);

            // その手前の通過駅にも時刻があれば、そこにも早く着きすぎないように（通過の進路は、ダイヤの時刻になってから開く）
            foreach (var pass in t.Plan.Skip(t.PlanIndex).TakeWhile(p => p != next).Where(p => !p.Stops))
            {
                if ((pass.Dep ?? pass.Arr) is not { } pdue || EstimateDistance(t, pass) is not { } pd || pd <= 0) continue;
                var prem = (pdue - ng).TotalSeconds;
                if (prem > 10) cruise = Math.Min(cruise, Math.Max(35 / 3.6, pd / prem * 1.1));
            }
        }

        // 線路の制限速度（走ったときの記録がある所だけ）
        cruise = Math.Min(cruise, LimitCap(t));
        if (t.Shunting) cruise = Math.Min(cruise, ShuntSpeed);

        var room = Math.Max(0, target - t.Head);
        var allowed = Math.Sqrt(2 * Decel * room);
        var v = Math.Min(Math.Min(t.Speed + Accel * dt, cruise), allowed);
        if (t.Speed > cruise) v = Math.Max(cruise, t.Speed - Decel * dt);
        v = Math.Min(v, allowed);
        var move = Math.Min(v * dt, room);
        t.Head += move;
        t.Speed = room - move < 0.05 && v < 1 ? 0 : v;

        // 入換（引上げ）: 引上線の奥で止まったら、折り返しの列番になる
        if (t.Shunting && t.Speed == 0 && t.Head - t.ShuntStartHead > 20 && t.PathEnd - t.Head < SignalMargin + 3)
        {
            ShuntReverse(t);
            UpdateInfo(t);
            return;
        }

        // 駅に着いた（停止位置の表がなく、線路の終わり（先に信号機がない）で止まったときも、次の停車駅に着いたとする）
        // 停止位置が線路の終わりより先にある（回路の長さの見込みが短い行き止まりなど）ときも、線路の終わりで止まったら着いたとする
        var atEnd = next != null && (stopX == null || stopX > t.PathEnd - 2) && !t.Blocked && t.Speed == 0 && t.PathEnd - t.Head < 5 && !(t.FromShunt && !t.ShuntMoved); // 引上線で折り返した直後（まだ番線へ動いていない）は除く
        if (next != null && (stopX is { } at && t.Speed == 0 && Math.Abs(t.Head - at) < 0.5 || atEnd))
        {
            // 連結: 停B で一度止まり（完全に止まってから）、連結位置まで進む
            if (t.CoupleStage < 2 && CouplePartnerAt(t, next) != null && CoupleSpec(t, next) != null)
            {
                if (t.CoupleStage == 0) (t.CoupleStage, t.CoupleStopUtc) = (1, utcNow);
                else if ((utcNow - t.CoupleStopUtc).TotalSeconds * _clock.Speed >= CoupleStopB.TotalSeconds) t.CoupleStage = 2;
                t.Status = $"{next.Name} 停B で停止（連結の前）";
                Release(t);
                UpdateInfo(t);
                return;
            }
            if (t.CoupleStage == 2) CoupleWith(t, next);
            SplitAt(t, next);
            t.Dwelling = true;
            t.DwellIndex = t.Plan.IndexOf(next);
            t.DepartureAsked = false;
            t.ArrivedUtc = utcNow;
        }

        // 通り過ぎた駅（通過）は、次の停車駅までの一覧から外す
        while (t.PlanIndex < t.Plan.Count && !t.Plan[t.PlanIndex].Stops && Passed(t, t.Plan[t.PlanIndex])) t.PlanIndex++;

        t.Status = t.Shunting ? "入換（引上げ）中" : t.Speed > 0 ? "走行中" : t.Blocked ? $"{t.EndSignal} の手前で停止" + ((t.PlanDetail ?? t.RouteMessage) is { Length: > 0 } why ? $"（{why}）" : "") : "停止";
        Release(t);
        UpdateInfo(t);
    }

    /// <summary>
    /// 入換（引上げ）で引上線の奥に止まったら、折り返しの列番（ダイヤの「折返」）になる。向きを変え、道筋は今いる軌道回路から作り直す。
    /// ホームへの入換の進路は、自動進路構成が新しい列番の始発の入換として、時刻に合わせて開ける（開いたら、その進路で番線に入る）。
    /// inPlace は、引き上げずに着いた番線で列番を変えるとき（<see cref="RouteEngine.TurnsInPlace"/>）。
    /// </summary>
    private void ShuntReverse(Train t, bool inPlace = false)
    {
        var here = t.ShuntStation ?? t.Plan[t.DwellIndex];
        var turn = _rail.Turns.TurnOf(t.Dia);
        t.Shunting = false;
        if (turn?.Number is not { Length: > 0 } number || _rail.DiaOf(number) is not { } nd || _routes.DirOfTrain(number) is not { } dir)
        {
            t.FinishedUtc ??= DateTime.UtcNow;
            t.Status = "入換（引上げ）を終えました";
            return;
        }

        var i = nd.Stops.FindIndex(s => s.StationId == here.StationId);
        var old = t.Number;
        _spawned.Add(RailData.NormalizeTrain(number));
        t.Number = number;
        t.Dia = nd;
        t.Dir = dir;
        t.Shown = null;
        t.StartTrack = -1;
        t.FromShunt = true;
        t.ShuntMoved = false;
        t.Plan.Clear();
        foreach (var s in nd.Stops.Skip(Math.Max(0, i)))
        {
            if (s.StationId == null || !InArea(s.StationId)) continue;
            var isFirst = t.Plan.Count == 0;
            t.Plan.Add(new PlanStop(s.Name, s.StationId, s.Stop == "停" || isFirst, s.Track, isFirst ? null : Time(s.Arr), Time(s.Dep) ?? (isFirst ? Time(turn.Time) : null)));
        }

        // 今いる軌道回路（引上線）から、反対向きに道筋を作り直す
        var circuit = t.Path.SelectMany(s => s.Circuits).LastOrDefault(c => c.Start < t.Head)?.Name ?? t.Info.TrackCircuits.LastOrDefault() ?? "";
        var len = Math.Max(LengthOf(circuit), t.Length + 10);
        t.Path.Clear();
        t.Path.Add(new Seg(null, null, 0, len, [new SegCircuit(circuit, 0, len)]));
        t.Head = len;
        t.StartHead = t.Head;
        t.Speed = 0;
        t.PlanIndex = 0;
        t.Dwelling = false;
        t.Blocked = true;
        t.EndSignal = null;
        t.Info.TrainNumber = number;
        t.Info.GameRoutes.Clear();
        t.Info.Stops = t.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList();
        t.Info.LastCenter = null;
        if (inPlace)
        {
            t.Status = $"{here.Name} {here.Track}番線で列番変更（{old} → {number}）。入換待ち";
            _log.Note($"にぎやかし: {old} は {here.Name} {here.Track}番線で列番を変えて {number} になりました");
            return;
        }
        t.Status = $"引上線で折り返し（{old} → {number}）。番線への入換待ち";
        _log.Note($"にぎやかし: {old} は {here.Name} の引上線で折り返して {number} になりました");
    }

    /// <summary>
    /// 終着で、ダイヤの「折返」の列車に変わる。列番・向き・停車駅を入れ替え、道筋は今の番線の軌道回路から作り直す
    /// （停止位置は、反対向きの出発信号機の手前）。進路は、列番が変わったので RouteEngine が新しい列車として作る。
    /// </summary>
    private bool TurnBack(Train t, DateTime utcNow)
    {
        var here = t.Plan[t.DwellIndex];
        if (_rail.Turns.TurnOf(t.Dia) is not { Kind: "折返", Number: { Length: > 0 } number } || _rail.DiaOf(number) is not { } nd) return false;
        if (IsUserTrain(number)) { HandOver(t, number); return false; }
        if (_routes.DirOfTrain(number) is not { } dir || !_rail.Stations.TryGetValue(here.StationId, out var st)) return false;
        var i = nd.Stops.FindIndex(s => s.StationId == here.StationId);
        if (i < 0 || i == nd.Stops.Count - 1) return false;
        var track = here.Track is { } ht && st.TrackCircuit.ContainsKey(ht) ? ht
            : st.TrackCircuit.Where(kv => t.Info.TrackCircuits.Contains(kv.Value)).Select(kv => (int?)kv.Key).FirstOrDefault() ?? -1;
        if (!st.TrackCircuit.TryGetValue(track, out var platform)) return false;

        var old = t.Number;
        _spawned.Add(RailData.NormalizeTrain(number));
        t.Number = number;
        t.Dia = nd;
        t.Dir = dir;
        t.Shown = null;
        t.StartStation = st.Name;
        t.StartTrack = track;
        t.Plan.Clear();
        var first = nd.Stops[i];
        t.Plan.Add(new PlanStop(first.Name, here.StationId, true, track, null, Time(first.Dep) ?? Time(_rail.Turns.TurnOf(t.Dia)?.Time)));
        foreach (var s in nd.Stops.Skip(i + 1))
        {
            if (s.StationId == null || !InArea(s.StationId)) continue;
            t.Plan.Add(new PlanStop(s.Name, s.StationId, s.Stop == "停", s.Track, Time(s.Arr), Time(s.Dep)));
        }

        var before = (FindStop(first.Name, dir, track) ?? FindStop(st.Name, dir, track))?.Before ?? 20;
        var len = Math.Max(LengthOf(platform), t.Length + before + 10);
        t.Path.Clear();
        t.Path.Add(new Seg(null, null, 0, len, [new SegCircuit(platform, 0, len)]));
        t.Head = len - before;
        t.StartHead = t.Head;
        t.Speed = 0;
        t.PlanIndex = 0;
        t.DwellIndex = 0;
        t.Dwelling = true;
        t.ArrivedUtc = utcNow;
        t.DepartureAsked = false;
        t.Blocked = true;
        t.EndSignal = null;
        t.Info.TrainNumber = number;
        t.Info.GameRoutes.Clear();
        t.Info.Stops = t.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList();
        t.Info.LastCenter = null;
        t.Status = $"{st.Name} {track}番線で折り返し（{old} → {number}）";
        _log.Note($"にぎやかし: {old} は {st.Name} で折り返して {number} になりました");
        return true;
    }

    /// <summary>ゲームの線区（進路を扱う駅のいちばん端からいちばん端まで）の中の駅か。</summary>
    private bool InArea(string stationId)
    {
        var zone = _rail.Line.ZoneOfStation(stationId);
        if (zone < 0) return false;
        var zones = _rail.ControlStations.Select(st => _rail.Line.ZoneOfStation(st.Id)).Where(z => z >= 0).ToList();
        return zones.Count > 0 && zone >= zones.Min() && zone <= zones.Max();
    }

    private void ChainNext(Train t)
    {
        var here = t.Plan[t.DwellIndex];
        if (t.Dia.Next is not { } nx || nx.StationId != here.StationId || _rail.DiaOf(nx.Number) is not { } nd) return;
        if (IsUserTrain(nx.Number)) { HandOver(t, nx.Number); return; }
        var i = nd.Stops.FindIndex(s => s.StationId == here.StationId);
        if (i < 0 || i == nd.Stops.Count - 1) return;
        var first = nd.Stops[i];
        t.Plan[t.DwellIndex] = here with { Dep = Time(first.Dep) ?? here.Dep };
        foreach (var s in nd.Stops.Skip(i + 1))
        {
            if (s.StationId == null || !InArea(s.StationId)) continue;
            t.Plan.Add(new PlanStop(s.Name, s.StationId, s.Stop == "停", s.Track, Time(s.Arr), Time(s.Dep)));
        }

        t.Dia = nd;
        _spawned.Add(RailData.NormalizeTrain(nd.Number));
        t.Info.Stops = t.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList();
        t.Shown = $"{t.Number} → {nd.Number}";
    }

    /// <summary>道筋の先へ、進める信号機の先を足していく（R・進路なしの信号機で止める）。</summary>
    private void Extend(Train t)
    {
        if (!t.Received) return; // 信号をまだ受け取っていない

        // 先に延ばした道筋のうち、まだ入っていない区間の信号機が R になった・進路が消えたら、そこで縮める
        for (var i = 1; i < t.Path.Count; i++)
        {
            var s = t.Path[i];
            if (s.Start <= t.Head + 1) continue;
            var red = s.Signal != null && t.RedSignals.Contains(s.Signal);
            var gone = s.Route is { } r && !t.Info.GameRoutes.Contains((r.Interlock, r.Name));
            if (!red && !gone) continue;
            t.Path.RemoveRange(i, t.Path.Count - i);
            break;
        }

        for (var guard = 0; guard < 20; guard++)
        {
            var last = t.Path[^1];
            var lastCircuit = last.Circuits[^1].Name;
            var candidates = NextSignals(t, last);
            t.EndSignal = candidates.FirstOrDefault();
            t.Blocked = candidates.Count > 0;
            // 道筋が十分先まであれば、それ以上は延ばさない
            if (last.End - t.Head > 3000) return;
            if (candidates.Count == 0)
            {
                // 信号機のつながりに出てこない進路（入換信号機など）: 開いている自分の進路で、今の軌道回路につながるもの
                if (OpenRouteFrom(t, last) is { } os) { t.Path.Add(os); if (t.FromShunt) t.ShuntMoved = true; continue; }
                return; // 線路の終わり
            }

            Seg? seg = null;
            foreach (var name in candidates)
            {
                if (t.RedSignals.Contains(name)) continue;
                if (_routesBySignal.TryGetValue(name, out var defs))
                {
                    // 駅の信号機: 開いている自分の進路
                    var open = defs.FirstOrDefault(d => t.Info.GameRoutes.Contains((d.Interlock, d.Name)) && d.Circuits.Count > 0 && Adjacent(lastCircuit, d.Circuits));
                    open ??= defs.FirstOrDefault(d => t.Info.GameRoutes.Contains((d.Interlock, d.Name)) && d.Circuits.Count > 0);
                    if (open == null) continue;
                    // 進路の回路が、もう道筋に入っている（新野崎の入換25LD は 22L の先の 5LT まで。22L の回路は 5LT だけ）なら、
                    // 長さ 0 の区間にして、その信号機を過ぎたことにする（その先へ道筋を延ばせるように）
                    var path = RoutePath(open, lastCircuit);
                    seg = path.Count > 0 ? MakeSeg(name, open, path, last.End)
                        : new Seg(name, open, last.End, last.End, [new SegCircuit(lastCircuit, last.End, last.End)]);
                }
                else if (_rail.Signals.TryGetValue(name, out var sig) && sig.Protects.Count > 0 && Order(sig.Protects, lastCircuit) is { Count: > 0 } protects)
                {
                    seg = MakeSeg(name, null, protects, last.End);
                }

                if (seg != null) break;
            }

            seg ??= OpenRouteFrom(t, last);
            if (seg == null)
            {
                // 止める信号機: 自分の進路の信号機（並んで建つものから）を選ぶ
                t.EndSignal = candidates.FirstOrDefault(c => _routesBySignal.TryGetValue(c, out var ds) && ds.Any(d => Adjacent(lastCircuit, d.Circuits))) ?? candidates[0];
                return;
            }

            t.Path.Add(seg);
            t.Blocked = false;
            if (t.FromShunt) t.ShuntMoved = true;
        }
    }

    /// <summary>開いている自分の進路のうち、道筋の最後の軌道回路につながっていて、まだ道筋にないもの（入換など）。R の信号機のものは除く。</summary>
    private Seg? OpenRouteFrom(Train t, Seg last)
    {
        var lastCircuit = last.Circuits[^1].Name;
        if (!_adj.TryGetValue(lastCircuit, out var near)) return null;
        var inPath = t.Path.SelectMany(s => s.Circuits).Select(c => c.Name).ToHashSet();
        foreach (var key in t.Info.GameRoutes)
        {
            if (!_rail.Routes.TryGetValue(key, out var d) || d.Circuits.Count == 0 || t.RedSignals.Contains(d.Signal)) continue;
            if (t.Path.Any(s => s.Route == d) || d.Circuits.Any(inPath.Contains)) continue;
            if (!near.Contains(d.Circuits[0]) && !near.Contains(d.Circuits[^1])) continue;
            if (RoutePath(d, lastCircuit) is not { Count: > 0 } path) continue;
            return MakeSeg(d.Signal, d, path, last.End);
        }

        return null;
    }

    private Seg MakeSeg(string signal, RouteDef? route, List<string> circuits, double start)
    {
        // 長さ: 次の信号機までの距離（走ったときの記録）。なければ軌道回路の長さの合計
        var weights = circuits.Select(LengthOf).ToList();
        var total = weights.Sum();
        var probe = new Seg(signal, route, start, start + total, circuits.Select(c => new SegCircuit(c, 0, 0)).ToList());
        var nextSig = NextSignals(null, probe).FirstOrDefault();
        if (nextSig != null && Distance(signal, nextSig) is { } d && d > 5) total = d;
        var scale = total / Math.Max(1, weights.Sum());
        var list = new List<SegCircuit>();
        var x = start;
        for (var i = 0; i < circuits.Count; i++)
        {
            var len = weights[i] * scale;
            list.Add(new SegCircuit(circuits[i], x, x + len));
            x += len;
        }

        return new Seg(signal, route, start, start + total, list);
    }

    /// <summary>道筋の最後の区間の先にある信号機（並んで建つものは全部）。</summary>
    private List<string> NextSignals(Train? t, Seg last)
    {
        if (last.Circuits.Count == 0) return new List<string>();
        var lastCircuit = last.Circuits[^1].Name;
        IEnumerable<string> raw;
        if (last.Signal != null && _rail.Signals.TryGetValue(last.Signal, out var sig)) raw = sig.Next;
        else if (t != null && last.Signal == null)
        {
            // 出した番線から: その番線の出発の進路の信号機
            var st = _rail.Stations.Values.FirstOrDefault(s => s.Name == t.StartStation);
            raw = st != null && st.Routes[t.Dir].Depart.TryGetValue(t.StartTrack, out var deps) ? deps.Take(1).Select(d => d.Signal) : [];
        }
        else raw = [];

        var list = raw.Where(n => n.Length > 0).Distinct().ToList();
        // 進路が何本もある信号機の「次」は、行き先の番線ごとの出発が並ぶ。今の区間の最後の軌道回路につながるものだけにする
        var near = list.Where(n => FirstCircuits(n).Any(c => c == lastCircuit || (_adj.TryGetValue(lastCircuit, out var a) && a.Contains(c)))).ToList();
        return near.Count > 0 ? near : list;
    }

    private IEnumerable<string> FirstCircuits(string signal)
    {
        if (_routesBySignal.TryGetValue(signal, out var defs)) return defs.Where(d => d.Circuits.Count > 0).Select(d => d.Circuits[0]);
        return _rail.Signals.TryGetValue(signal, out var s) ? s.Protects : [];
    }

    private bool Adjacent(string from, IReadOnlyList<string> circuits) =>
        circuits.Count > 0 && (circuits.Contains(from) || (_adj.TryGetValue(from, out var a) && circuits.Any(a.Contains)));

    /// <summary>軌道回路を、from につながる順に並べる（つながりが分からなければ、書いてある順）。</summary>
    /// <summary>
    /// 進路の軌道回路のうち、列車が実際に通るものを、通る順に。入る所（from につながる回路）から出る所までを、回路のつながりでたどる。
    /// 出る所は、行き先が番線ならその番線の回路、そうでなければ次の信号機の回路につながる回路。
    /// 進路のデータには、列車が通らない回路が入っていることがある（水越の 12R・12L の 21ロT）。
    /// それも在線にすると、その回路を守る信号機（水越の 13L）が停止になり、ほかの列車が動けなくなる。
    /// たどれなければ、全部をつながる順に並べる（<see cref="Order"/>）。
    /// </summary>
    private List<string> RoutePath(RouteDef def, string from)
    {
        var set = def.Circuits.Where(c => c != from).Distinct().ToHashSet();
        if (set.Count == 0) return new List<string>();
        bool Near(string a, string b) => _adj.TryGetValue(a, out var n) && n.Contains(b);

        // 出る所
        var targets = new HashSet<string>();
        var m = System.Text.RegularExpressions.Regex.Match(def.To, @"^(\d+)番線$");
        if (m.Success && _rail.Stations.TryGetValue(def.StationId, out var st) && st.TrackCircuit.TryGetValue(int.Parse(m.Groups[1].Value), out var tc) && set.Contains(tc))
            targets.Add(tc);
        else if (ToCircuit(def, set) is { } toCircuit)
            // 行き先が軌道回路（大道寺の 1RC は「42ﾛT」）: その回路まで。1RB と同じ信号機で、41T の先に 3R も 4R もあるので、
            // 次の信号機の手前の回路で探すと、近い 41T（3R の手前）で切れて、開いていない 3R で止まってしまう
            targets.Add(toCircuit);
        else if (_rail.Signals.TryGetValue(def.Signal, out var sig))
        {
            var after = sig.Next.SelectMany(FirstCircuits).Where(c => !set.Contains(c)).ToHashSet();
            foreach (var c in set)
                if (after.Any(a => Near(c, a))) targets.Add(c);
        }

        // 入る所から出る所まで（いちばん短い道）
        var starts = set.Where(c => Near(from, c)).ToList();
        if (targets.Count > 0 && starts.Count > 0)
        {
            var prev = new Dictionary<string, string?>();
            var q = new Queue<string>();
            foreach (var c in starts) { prev[c] = null; q.Enqueue(c); }
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                if (targets.Contains(c))
                {
                    var path = new List<string>();
                    for (string? x = c; x != null; x = prev[x]) path.Add(x);
                    path.Reverse();
                    return path;
                }

                foreach (var n in set.Where(n => !prev.ContainsKey(n) && Near(c, n)))
                {
                    prev[n] = c;
                    q.Enqueue(n);
                }
            }
        }

        return Order(def.Circuits, from);
    }

    /// <summary>進路の行き先が軌道回路の名前なら、その回路（データには半角の「ﾛ」もあるので、全角にそろえて比べる）。</summary>
    private static string? ToCircuit(RouteDef def, HashSet<string> set)
    {
        var to = def.To.Normalize(System.Text.NormalizationForm.FormKC);
        if (to.Length == 0) return null;
        return set.FirstOrDefault(c => c == to || c.EndsWith("_" + to, StringComparison.Ordinal));
    }

    private List<string> Order(IReadOnlyList<string> circuits, string from)
    {
        var rest = circuits.Where(c => c != from).Distinct().ToList();
        var result = new List<string>();
        var prev = from;
        while (rest.Count > 0)
        {
            var n = rest.FirstOrDefault(c => _adj.TryGetValue(prev, out var a) && a.Contains(c)) ?? rest[0];
            result.Add(n);
            rest.Remove(n);
            prev = n;
        }

        return result;
    }

    /// <summary>
    /// 次の停車駅の停止位置（道筋の上で分かれば）。
    /// ・番線ごとの表（AiTrains.json の stops）にあれば、その信号機からの距離
    /// ・表にない番線・駅は、とりあえず列車が軌道回路の中に収まる位置: 進路を扱う駅はその番線の軌道回路、
    ///   進路を扱わない駅はその駅の区間の軌道回路の、終わりの 5m 手前（最後尾が分岐などにはみ出さないように）
    /// </summary>
    private double? StopX(Train t, PlanStop p)
    {
        var st = _rail.Stations.TryGetValue(p.StationId, out var sd) ? sd : null;
        // 番線: 道筋に入っているその駅の番線の回路から（自動進路構成が番線を変えていれば、そちら）。なければダイヤの番線
        int? track = p.Track;
        SegCircuit? platform = null;
        if (st != null)
            foreach (var c in t.Path.SelectMany(s => s.Circuits))
            {
                // 先頭がもう終わりまで来ている回路は除く（引上線で折り返した直後は、今いる引上線の回路も「番線の回路」なので）
                if (c.End <= t.Head + 1) continue;
                var k = st.TrackCircuit.FirstOrDefault(kv => kv.Value == c.Name);
                if (k.Value == null) continue;
                (track, platform) = (k.Key, c);
                break;
            }

        var spec = st != null && st.RouteControl
            ? _stops.FirstOrDefault(s => s.Station == p.Name && s.Dir == t.Dir && s.Track == (track ?? -1))
            : FindStop(p.Name, t.Dir, 0);
        const double Margin = 5;
        if (spec != null && BoundaryOf(t, spec.Signal) is { } b)
        {
            // 連結: 番線に増結車がいれば、停B（4両停目の4m手前）、そのあと連結位置（増結車の後ろ）
            if (spec.CoupleBefore is { } cb && CouplePartnerAt(t, p) != null)
                return t.CoupleStage >= 2 ? b - cb : b - (spec.StopBBefore ?? cb);
            var x = spec.After is { } after ? b + after : b - spec.Before;
            // 表の位置だと最後尾が番線の回路の手前（分岐の回路）に残るなら、番線の回路に収まるところまで前へ出す（番線の回路の終わりは越えない）。
            // 残したままだと、分岐を通るほかの進路（日野森で 2番線に着いた上りの最後尾が 22T に残り、3番線の下りの 4L が出られなかった）をふさぐ
            if (platform != null && x < platform.End && x - t.Length < platform.Start + Margin)
                x = Math.Min(platform.Start + t.Length + Margin, platform.End - 1);
            return x;
        }

        // 表にない: 軌道回路の中に収める
        if (platform != null) return Math.Max(platform.Start + 1, platform.End - Margin);
        if (st == null || !st.RouteControl)
        {
            var zone = _rail.Line.ZoneOfStation(p.StationId);
            var inZone = t.Path.SelectMany(s => s.Circuits).Where(c => c.End > t.Head - 1 && _rail.Line.ZoneOfCircuit(c.Name) == zone).ToList();
            // その駅の区間のうち、いちばん手前から続いている回路の終わり
            if (inZone.Count > 0)
            {
                var last = inZone[0];
                var all = t.Path.SelectMany(s => s.Circuits).ToList();
                for (var i = all.IndexOf(last) + 1; i < all.Count && _rail.Line.ZoneOfCircuit(all[i].Name) == zone; i++) last = all[i];
                // 道筋がまだ駅の区間の先まで延びていなければ（先の信号機が停止）、決めずに待つ（その手前では信号で止まる）
                var idx = all.IndexOf(last);
                if (idx < all.Count - 1 || !t.Blocked) return Math.Max(last.Start + 1, last.End - Margin);
            }
        }

        // 出発信号機の手前で止まっている（番線の回路が分からない）: 20m 手前
        if (t.Blocked && t.EndSignal is { } e && e.StartsWith(p.Name) && e.Contains("出発")) return t.PathEnd - 20;
        return null;
    }

    /// <summary>連結: その駅の番線で止まって待っている増結車の列番（RouteEngine が見つけたもの）。いなければ null。</summary>
    private string? CouplePartnerAt(Train t, PlanStop p) =>
        _couplePartners.TryGetValue(t.Id, out var m) && m.TryGetValue(p.StationId, out var partner) ? partner : null;

    /// <summary>連結の位置が書いてある停止位置の表（AiTrains.json の stops の couple）。</summary>
    private StopSpec? CoupleSpec(Train t, PlanStop p) =>
        _stops.FirstOrDefault(s => s.Station == p.Name && s.Dir == t.Dir && s.CoupleBefore != null && s.Track == (p.Track ?? -1));

    /// <summary>
    /// 連結位置に着いた: 増結車（自動運転）を消し、両数を足す。先頭は増結車の先頭まで進める（連結位置は増結車の最後尾）。
    /// 増結車がユーザーの列車なら消せないので、両数だけ足す（ユーザーが運転を終えれば在線も消える）。
    /// </summary>
    private void CoupleWith(Train t, PlanStop p)
    {
        t.CoupleStage = 3;
        if (CouplePartnerAt(t, p) is not { } partner) return;
        var cars = CarsOf(partner);
        foreach (var pt in _trains.Values.Where(x => x.Number == partner).ToList())
        {
            cars = pt.Cars;
            _trains.Remove(pt.Id);
        }
        t.Cars += cars;
        t.Length = t.Cars * CarLength;
        t.Head = Math.Min(t.Head + cars * CarLength, t.PathEnd - 1);
        _log.Note($"にぎやかし: {t.Number} に {partner}（{cars}両）を連結しました（{p.Name}。{t.Cars}両）");
    }

    /// <summary>
    /// 解放: ダイヤに「解」＋この列番の列車があり、その最初の駅に着いたら、両数を減らし、切り離した車両を出す
    /// （出すのは周期の終わりに、自分のロックの外で）。
    /// </summary>
    private void SplitAt(Train t, PlanStop p)
    {
        if (_rail.DiaOf("解" + t.Number) is not { } child || child.Stops.FirstOrDefault(s => s.StationId != null) is not { } cs
            || cs.StationId != p.StationId || _trains.Values.Any(x => x.Number == child.Number) || _userTrains.Contains(child.Number)) return;
        var cars = CarsOf(child.Number);
        t.Cars = Math.Max(CarsOf(t.Number), t.Cars - cars);
        t.Length = t.Cars * CarLength;
        _pendingSplits.Add((child.Number, cars, p.Name, cs.Track ?? p.Track ?? -1, t.Number));
    }

    private double? BoundaryOf(Train t, string signal)
    {
        foreach (var s in t.Path)
            if (s.Signal == signal) return s.Start;
        if (t.EndSignal == signal || (t.Blocked && t.EndSignal != null && SamePlace(t.EndSignal, signal))) return t.PathEnd;
        // 並んで建つ信号機（同じ位置）
        foreach (var s in t.Path)
            if (s.Signal != null && SamePlace(s.Signal, signal)) return s.Start;
        return null;
    }

    private bool SamePlace(string a, string b) => a == b || (Distance(a, b) is { } d && Math.Abs(d) < 3) || (Distance(b, a) is { } e && Math.Abs(e) < 3);

    /// <summary>次の停車駅までの距離の見込み（最後に過ぎた信号機から、走ったときの記録で）。</summary>
    private double? EstimateDistance(Train t, PlanStop p)
    {
        var spec = FindStop(p.Name, t.Dir, p.Track ?? 0) ?? _stops.FirstOrDefault(s => s.Station == p.Name && s.Dir == t.Dir);
        if (spec == null) return null;
        var passed = t.Path.LastOrDefault(s => s.Signal != null && s.Start <= t.Head);
        if (passed?.Signal is not { } from) return null;
        if (Distance(from, spec.Signal) is not { } d) return null;
        return d - (t.Head - passed.Start) + (spec.After ?? -spec.Before);
    }

    private bool Passed(Train t, PlanStop p)
    {
        var zone = _rail.Line.ZoneOfStation(p.StationId);
        if (zone < 0) return true;
        var rear = t.Info.Zones.Count == 0 ? (int?)null : t.Dir == Dir.Down ? t.Info.Zones.Min() : t.Info.Zones.Max();
        return rear is { } r && (r - zone) * t.Dir.Step() > 0;
    }

    /// <summary>
    /// 速さの上限（m/s）。
    /// ・区間の最高速度: 館浜〜新野崎 110km/h、新野崎〜大路（赤山町方）100km/h
    /// ・線路の制限: 道筋の信号機を目印に、ゲームの自動運転で走ったときの記録の位置へ置き換えて、今いる所（最後尾まで）の制限と、
    ///   この先 1500m の制限の手前で減速するための速さ。記録の制限は、前の信号機が G の間の値だけ（信号の現示による制限は除いてある）
    /// ・分岐: 記録が当てはまらない駅の進路（記録の走りと違う番線に入る・出る、記録がない）は 40km/h
    /// </summary>
    private double LimitCap(Train t)
    {
        var cap = SectionMax(t);
        const double Turnout = 40 / 3.6;

        // 目印の信号機: 過ぎた信号機のうちいちばん新しいもの、なければ前の信号機
        Seg? refSeg = t.Path.LastOrDefault(s => s.Signal != null && s.Start <= t.Head) ?? t.Path.FirstOrDefault(s => s.Signal != null);
        var refSignal = refSeg?.Signal ?? t.EndSignal;
        var refX = refSeg?.Start ?? t.PathEnd;
        // 使う記録: 目印の信号機がある記録のうち、この先 1500m の駅の進路（番線）に当てはまるもの。なければ最初のもの
        var routesAhead = t.Path.Where(s => s.Route != null && s.End > t.Head - t.Length && s.Start <= t.Head + 1500).Select(s => s.Route!).ToList();
        var candidates = refSignal == null ? new List<int>()
            : Enumerable.Range(0, _signalRuns.Count).Where(i => _signalRuns[i].ContainsKey(refSignal) && _limitRuns[i].Count > 0).ToList();
        int? run = candidates.Cast<int?>().FirstOrDefault(i => routesAhead.All(d => RecordCovers(i!.Value, d, t))) ?? candidates.Cast<int?>().FirstOrDefault();
        if (run is { } i)
            {
                var at = _signalRuns[i][refSignal!];
                var lims = _limitRuns[i];
                var p0 = at + (t.Head - refX);
                double LimitAt(double p)
                {
                    var v = double.PositiveInfinity;
                    foreach (var (pos, kmh) in lims)
                        if (pos <= p) v = kmh; else break;
                    return v;
                }

                // 今の制限: 先頭から最後尾までのうち、いちばん低いもの（上がるのは最後尾が抜けてから）
                var now = LimitAt(p0);
                foreach (var (pos, kmh) in lims)
                    if (pos > p0 - t.Length && pos <= p0) now = Math.Min(now, kmh);
                now = Math.Min(now, LimitAt(p0 - t.Length));
                cap = Math.Min(cap, now / 3.6);
                foreach (var (pos, kmh) in lims)
                {
                    if (pos <= p0) continue;
                    if (pos > p0 + 1500) break;
                    var l = kmh / 3.6;
                    cap = Math.Min(cap, Math.Sqrt(l * l + 2 * Decel * (pos - p0)));
                }
            }

        // 分岐: 記録が当てはまらない駅の進路は 40km/h（列車がその進路にいる間と、その手前からの減速）
        foreach (var s in t.Path)
        {
            if (s.Route is not { } def || s.End <= t.Head - t.Length || s.Start > t.Head + 1500) continue;
            // 入換の進路はいつも 25km/h
            var shunt = def.Kind.Contains("入換") || def.Category.Contains("入換");
            if (!shunt && run is { } j && RecordCovers(j, def, t)) continue;
            var lim = shunt ? ShuntSpeed : Turnout;
            cap = s.Start <= t.Head ? Math.Min(cap, lim) : Math.Min(cap, Math.Sqrt(lim * lim + 2 * Decel * (s.Start - t.Head)));
        }

        return cap;
    }

    /// <summary>区間の最高速度（m/s）。新野崎（TH67）から館浜方は 110km/h、それより赤山町方は 100km/h。</summary>
    private double SectionMax(Train t)
    {
        var z = _rail.Line.ZoneOfStation("TH67");
        return z >= 0 && t.Info.Zones.Count > 0 && t.Info.Zones.Min() >= z ? 110 / 3.6 : 100 / 3.6;
    }

    /// <summary>
    /// 走ったときの記録（run）の制限が、その駅の進路に当てはまるか。記録の走りがその駅に止まっていれば、同じ番線の進路だけ。
    /// 記録の走りが通過した駅（番線の記録がない）は、こちらも通過するときだけ（どちらも本線を通ったと見なす）。
    /// </summary>
    private bool RecordCovers(int run, RouteDef def, Train t)
    {
        if (!_rail.Stations.TryGetValue(def.StationId, out var st)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(def.To + " " + def.From, @"(\d+)番線");
        int? track = m.Success ? int.Parse(m.Groups[1].Value) : null;
        if (_trackRuns[run].TryGetValue(st.Name, out var rt)) return track == null || track == rt;
        return !t.Plan.Any(p => p.Name == st.Name && p.Stops);
    }

    // =========================================================================================
    // 自動運転列車アリ: ダイヤの列車を時刻に合わせて出す・ユーザーの列車
    // =========================================================================================

    /// <summary>
    /// ユーザーの列車の列番: 出勤している人の行路（臨変を含む）の列車と、参加者・車掌が入力している列番。
    /// 自動運転列車アリでも、これらは自動では出さない。
    /// </summary>
    private HashSet<string> UserTrains(List<PlayerInfo> snapshot)
    {
        var set = new HashSet<string>();
        foreach (var p in snapshot)
            if (!PlayerRegistry.IsVirtual(p.ConnectionId) && p.TrainNumber.Length > 0) set.Add(RailData.NormalizeTrain(p.TrainNumber));
        foreach (var c in _relay.Conductors())
            if (c.TrainNumber.Length > 0) set.Add(RailData.NormalizeTrain(c.TrainNumber));
        var roster = _duties.Current;
        foreach (var (duty, user) in _crew.Assignments().Concat(_crew.Extras()))
        {
            if (_crew.AttendedAt(user) == null || roster.DutyById(duty) is not { } d) continue;
            foreach (var item in d.Items)
                if (item.Train.Length > 0) set.Add(RailData.NormalizeTrain(item.Train));
        }

        return set;
    }

    private bool IsUserTrain(string number) => _userTrains.Contains(RailData.NormalizeTrain(number)) || _userInGame.Contains(RailData.NormalizeTrain(number));

    /// <summary>続けて走る列番がユーザーの列車なら、着いたところで消す（ユーザーがその列番で出てくる）。</summary>
    private void HandOver(Train t, string number)
    {
        t.FinishedUtc = DateTime.UtcNow - TerminalLinger;
        t.Status = $"{t.Plan[t.DwellIndex].Name} に到着（{number} はユーザーの列車なので、ここで消します）";
    }

    // =========================================================================================
    // 継続乗務の在線の引き継ぎ（ダイヤ運転中、ユーザーが運転画面を出たあと）
    // =========================================================================================

    /// <summary>ダイヤ運転中、継続乗務の在線が3分残ったら、自動運転の列車として引き継ぐ（自動運転列車のアリ/ナシによらない）。</summary>
    private void TakeOverStaleGhosts()
    {
        if (!_central.DiaMode) return;
        var now = DateTime.UtcNow;
        foreach (var g in _players.Ghosts().Where(g => now - g.SinceUtc >= GhostTakeOverAfter))
        {
            if (_players.TakeGhost(g.Id) is not { } taken) continue;
            var (ok, message) = TakeOver(taken);
            if (ok) { _takeOverLogged.Remove(g.Id); continue; }
            _players.RestoreGhost(taken); // 引き継げなければ、在線は残したまま（2秒ごとに試し直す）
            if (_takeOverLogged.Add(g.Id)) _log.Note($"にぎやかし: 継続乗務 {g.Info.TrainNumber} を引き継げません（{message}）");
        }
    }

    /// <summary>ダイヤ運転中、継続乗務の在線が消されたとき、自動運転列車アリなら、自動運転の列車として引き継ぐ。</summary>
    private void OnGhostDeleted(PlayerRegistry.Ghost g)
    {
        if (!_central.DiaMode || !Enabled) return;
        try
        {
            var (ok, message) = TakeOver(g);
            if (!ok) _log.Note($"にぎやかし: 継続乗務 {g.Info.TrainNumber} を引き継げません（{message}）");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "継続乗務 {Train} の引き継ぎでエラー", g.Info.TrainNumber);
        }
    }

    /// <summary>
    /// 継続乗務の在線（レジストリから外したもの）を、自動運転の列車として、その場所から走らせる。進路・駅の状態は RouteEngine が引き継ぐ。
    /// ・進路を扱う駅の番線にいる: その駅・番線に出したのと同じ（ダイヤの発車時刻まで待って発車）
    /// ・それ以外（駅間・進路を扱わない駅など）: 踏んでいる軌道回路から、最後に過ぎた信号機の先として走り出す。
    ///   ダイヤで止まる駅にいれば、そこに止まっていることにする
    /// 両数は分からないので6両にする。
    /// </summary>
    private (bool Ok, string Message) TakeOver(PlayerRegistry.Ghost g)
    {
        var info = g.Info;
        var number = RailData.NormalizeTrain(info.TrainNumber);
        if (_rail.DiaOf(number) is not { } dia) return (false, $"{number}: ダイヤにない列車です");
        if (_routes.DirOfTrain(number) is not { } dir) return (false, $"{number}: 列車番号の形が違います");
        if (info.TrackCircuits.Count == 0) return (false, "在線の位置が分かりません");

        // 進路を扱う駅の番線にいる
        foreach (var st in _rail.Stations.Values.Where(s => s.RouteControl))
        {
            var k = st.TrackCircuit.FirstOrDefault(kv => info.TrackCircuits.Contains(kv.Value));
            if (k.Value == null) continue;
            if (dia.Stops.FirstOrDefault(s => s.StationId == st.Id) is not { } ds) break; // ダイヤで通らない駅（番線を変えて逃がした先など）: 駅間と同じ扱い
            return Add(number, CarsOf(number), ds.Name, true, k.Key, info);
        }

        lock (_lock)
        {
            if (_trains.Values.Any(t => t.Number == number)) return (false, $"{number} はもう走っています");
            var step = dir.Step();
            // 踏んでいる軌道回路を、後ろから前へ
            var sorted = info.TrackCircuits.OrderBy(c => (_rail.Line.ZoneOfCircuit(c) ?? 0) * step).ToList();
            var ordered = new List<string> { sorted[0] };
            ordered.AddRange(Order(sorted.Skip(1).ToList(), sorted[0]));

            // 最後に過ぎた信号機: 前の回路から、その回路を通る自分の進路の信号機、なければその回路を守る同じ向きの信号機
            string? signal = null;
            RouteDef? route = null;
            for (var i = ordered.Count - 1; i >= 0 && signal == null; i--)
            {
                var c = ordered[i];
                foreach (var key in info.GameRoutes)
                    if (_rail.Routes.TryGetValue(key, out var d) && d.Signal.Length > 0 && d.Circuits.Contains(c)) { signal = d.Signal; route = d; break; }
                if (signal != null) break;
                signal = _rail.Signals.Values
                    .Where(s => s.Protects.Contains(c) && !_routesBySignal.ContainsKey(s.Name) && (s.Direction == null || s.Direction == dir))
                    .OrderByDescending(s => s.Direction == dir).FirstOrDefault()?.Name;
            }

            if (signal == null) return (false, $"{string.Join(" ", info.TrackCircuits)} の手前の信号機が分かりません");

            var train = new Train
            {
                Id = $"{PlayerRegistry.AiPrefix}{++_seq}", Seq = _seq, Number = number, Dia = dia, Dir = dir, Cars = CarsOf(number),
                Length = CarsOf(number) * CarLength, StartStation = "", Auto = true, MidLine = true
            };
            var x = 0.0;
            var circuits = new List<SegCircuit>();
            foreach (var c in ordered)
            {
                var len = LengthOf(c);
                circuits.Add(new SegCircuit(c, x, x + len));
                x += len;
            }

            train.Path.Add(new Seg(signal, route, 0, x, circuits));
            train.Head = x - 1;
            train.StartHead = train.Head;

            // 停車・通過の一覧（今いる所から先）
            var front = info.Zones.Count > 0 ? (dir == Dir.Down ? info.Zones.Max() : info.Zones.Min()) : _rail.Line.ZoneOfCircuit(ordered[^1]) ?? -1;
            foreach (var s in dia.Stops)
            {
                if (s.StationId == null || !InArea(s.StationId)) continue;
                var zone = _rail.Line.ZoneOfStation(s.StationId);
                if (front >= 0 && (zone - front) * step < 0) continue;
                train.Plan.Add(new PlanStop(s.Name, s.StationId, s.Stop == "停", s.Track, Time(s.Arr), Time(s.Dep)));
            }

            train.Info = new PlayerInfo
            {
                ConnectionId = train.Id, Mode = ShareMode.Shared, InGame = true, TrainCrewConnected = true, TrainNumber = number,
                TrainNumberTyped = true, JoinedUtc = DateTime.UtcNow, LastReportUtc = DateTime.UtcNow, TakesOver = info.ConnectionId,
                GameRoutes = new HashSet<(string, string)>(info.GameRoutes), // 進路も引き継ぐ（Add と同じ）
                Stops = train.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList()
            };

            // ダイヤで止まる駅にいる: そこに止まっていることにする（発車時刻が来て、出発の進路が開いたら発車）
            var here = train.Plan.FindIndex(p => p.Stops && _rail.Line.ZoneOfStation(p.StationId) == front);
            if (here >= 0)
            {
                train.Dwelling = true;
                train.DwellIndex = here;
                train.PlanIndex = here;
                train.ArrivedUtc = DateTime.UtcNow.AddSeconds(-MinDwellSeconds);
            }

            var where = _rail.Line.DescribeZones(info.Zones) ?? string.Join(" ", info.TrackCircuits);
            train.Status = here >= 0 ? $"{train.Plan[here].Name} に停車中" : "停止";
            UpdateInfo(train);
            _trains[train.Id] = train;
            _spawned.Add(number);
            _log.Note($"にぎやかし: 継続乗務 {number} の在線を、自動運転の列車として {where} から走らせます");
            return (true, $"{number} を {where} から走らせます");
        }
    }

    /// <summary>
    /// ダイヤの列車のうち、進路を扱う駅の発車時刻が近い（2分前〜30秒過ぎ）ものを、その駅に出す。
    /// 出さないもの: ユーザーの列車、もう出した・走っている列番、走っている列車がこのあと続けて・折り返して走る列番。
    /// 番線にほかの列車がいるなどで出せなければ、時刻の範囲の間は2秒ごとに試し直す。
    /// </summary>
    private void AutoSpawn(HashSet<string> users, List<PlayerInfo> snapshot)
    {
        var now = _clock.Now;
        List<(string Number, string Station, int? Track)> todo = new();

        // ユーザーが運転している列車（継続乗務で残した在線も）の運行番号と、その列車の最初の時刻。
        // 同じ運行番号で、それより後に走る列車は出さない（ユーザーの列車が遅れていても、その後の列番はユーザーが続けて走る。回1750 が遅れているのに 1751 が出た）
        var userUnyo = new Dictionary<int, TimeSpan>();
        foreach (var p in snapshot.Where(p => !p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix) && p.TrainNumber.Length > 0))
        {
            if (_rail.DiaOf(p.TrainNumber) is not { Unyo: { } u } ud || ud.Stops.Select(s => Time(s.Arr ?? s.Dep)).FirstOrDefault(t => t != null) is not { } first) continue;
            userUnyo[u] = userUnyo.TryGetValue(u, out var old) && old < first ? old : first;
        }
        lock (_lock)
        {
            if (!_enabled || now is not { } n || !_clock.IsRunning) return;
            // ロープレ時刻を戻したら、出した記録を消す（もう一度出せるように）
            if (_lastGame is { } lg && n < lg - TimeSpan.FromMinutes(2)) _spawned.Clear();
            _lastGame = n;

            // 走っている列車が、このあと続けて・折り返して走る列番
            // 切り離す列車（解874 など）と、その先の列番（回933）も、元の列車（874）が走っていれば出さない（切り離したときに出る）
            var covered = new HashSet<string>();
            void Cover(DiaTrain? d)
            {
                for (var k = 0; k < 10 && d != null; k++)
                {
                    if (_rail.DiaOf("解" + d.Number) is { } split && covered.Add(split.Number)) Cover(split);
                    var nextNo = d.Next?.Number ?? (_rail.Turns.TurnOf(d) is { Kind: "折返", Number: { Length: > 0 } tn } ? tn : null);
                    if (nextNo == null) break;
                    covered.Add(RailData.NormalizeTrain(nextNo));
                    d = _rail.DiaOf(nextNo);
                }
            }
            foreach (var t in _trains.Values) Cover(t.Dia);
            // ユーザーの列車が切り離す車両も（ユーザーの列車の折り返しは、今までどおり自動で出す）
            foreach (var u in users)
                if (_rail.DiaOf("解" + u) is { } usplit && covered.Add(usplit.Number)) Cover(usplit);

            foreach (var dia in _rail.Dia.Values)
            {
                var number = RailData.NormalizeTrain(dia.Number);
                if (_spawned.Contains(number) || users.Contains(number) || covered.Contains(number) || _trains.Values.Any(t => t.Number == number)) continue;
                var stops = dia.Stops.Where(s => s.StationId != null && _rail.Stations.TryGetValue(s.StationId, out var sd) && sd.RouteControl).ToList();
                if (stops.Count < 2) continue;
                // 時刻がまだ過ぎていない最初の駅（30秒の遅れまで）。終着と、番線を使わずに通過する駅には出さない
                var startDir = _routes.DirOfTrain(number);
                var start = stops.Take(stops.Count - 1).FirstOrDefault(s => Time(s.Dep ?? s.Arr) is { } t && t >= n - TimeSpan.FromSeconds(30)
                    && !(startDir is { } sd2 && PassesWithoutTrack(s, sd2)));
                if (start == null || Time(start.Dep ?? start.Arr) is not { } dep || dep > n + TimeSpan.FromMinutes(2)) continue;
                if (dep < n - TimeSpan.FromSeconds(30)) continue; // 時刻を過ぎた駅には出さない（遅れて出すと、ほかの列車との順番が崩れる）
                if (dia.Unyo is { } du && userUnyo.TryGetValue(du, out var userFirst) && IsLater(dep, userFirst)) continue; // ユーザーの運行番号のこの先の列車
                // 赤山町を2番線で通過する下り（三郷方から入ってくる）: 通過時刻の20秒前に 9LCT（3番線）に出す
                if (start.StationId == EntryStation && start.Stop != "停" && start.Track == 2 && _routes.DirOfTrain(number) == Dir.Down)
                {
                    if (dep - n > TimeSpan.FromSeconds(EntryLeadSeconds)) continue;
                    todo.Add((number, start.Name, EntryTrack));
                    continue;
                }

                todo.Add((number, start.Name, null));
            }
        }

        foreach (var (number, station, track) in todo) Add(number, CarsOf(number), station, true, track);
    }

    /// <summary>最後尾が抜けた進路は、ゲームと同じく解く（道筋からも外す）。</summary>
    private void Release(Train t)
    {
        var tail = t.Head - t.Length;
        while (t.Path.Count > 1 && t.Path[0].End < tail - 1)
        {
            if (t.Path[0].Route is { } r) t.Info.GameRoutes.Remove((r.Interlock, r.Name));
            t.Path.RemoveAt(0);
        }
    }

    private void UpdateInfo(Train t)
    {
        var tail = t.Head - t.Length;
        var circuits = t.Path.SelectMany(s => s.Circuits).Where(c => c.End > tail && c.Start < t.Head + 0.5).Select(c => c.Name).Distinct().ToList();
        if (circuits.Count == 0) circuits = [t.Path[0].Circuits[^1].Name];
        var p = t.Info;
        p.TrackCircuits = circuits;
        p.Zones = _rail.Line.ZonesOf(circuits);
        if (p.Zones.Count > 0)
        {
            var center = p.Zones.Average();
            if (p.LastCenter is { } lc && Math.Abs(center - lc) > 1e-9) p.Direction = center > lc ? 1 : -1;
            p.LastCenter = center;
            p.ZonesUpdatedUtc = DateTime.UtcNow;
        }

        p.Speed = (float)(t.Speed * 3.6);
        p.ReportedSpeed = p.Speed;
        p.TraveledDistance = (float)(t.Head - t.StartHead);
        // 停車駅までの距離（走った距離で数える）。ゲームの列車と同じく、駅間のどこにいるかをダイヤグラムの線に出すため（RouteEngine の Fraction）。
        // これから行く駅（3つまで）だけ、走ったときの記録から見込み直す。過ぎた駅は、最後に見込んだ値のまま
        var ahead = 0;
        foreach (var ps in t.Plan)
        {
            if (ahead >= 3) break;
            if (Passed(t, ps)) continue;
            ahead++;
            if (EstimateDistance(t, ps) is not { } est) continue;
            if (p.Stops.FirstOrDefault(x => x.Name == ps.Name) is { } si) si.Distance = (float)(t.Head - t.StartHead + est);
        }
        p.LastReportUtc = DateTime.UtcNow;
    }

    // =========================================================================================
    // 距離の表
    // =========================================================================================

    /// <summary>自動で出す列車の両数。ダイヤの運行番号から AiTrains.json の carsByUnyo で引く（ない運行は6両）。</summary>
    private int CarsOf(string number) =>
        _rail.DiaOf(number)?.Unyo is { } u && _carsByUnyo.TryGetValue(u, out var c) ? c : 6;

    private double LengthOf(string circuit) =>
        _circuitLengths.TryGetValue(circuit, out var l) ? l : circuit.StartsWith("TH") ? DefaultStationCircuit : DefaultLineCircuit;

    /// <summary>信号機 a から b までの距離（同じ走りの記録に両方あれば）。</summary>
    private double? Distance(string a, string b)
    {
        foreach (var run in _signalRuns)
            if (run.TryGetValue(a, out var x) && run.TryGetValue(b, out var y) && y >= x) return y - x;
        return null;
    }

    /// <summary>
    /// 番線を使わずに本線を通過する駅（江ノ原信号場の通過など。ダイヤに番線がなく、駅にそのまま通る進路がある）。
    /// ここには列車を出さない。出すと、通過する列車を行き止まりや出区用の番線（江ノ原信号場の下りなら11番線）に置いて、そこから発車させてしまう
    /// （手前の駅で番線がふさがって出せないまま時刻を過ぎると、次の駅として選ばれる）
    /// </summary>
    private bool PassesWithoutTrack(DiaStop s, Dir dir) =>
        s.Stop == "通" && s.Track == null && s.StationId != null
        && _rail.Stations.TryGetValue(s.StationId, out var st) && st.Routes[dir].Through.Count > 0;

    private StopSpec? FindStop(string station, Dir dir, int track) =>
        _stops.FirstOrDefault(s => s.Station == station && s.Dir == dir && s.Track == track)
        ?? (track == 0 ? _stops.FirstOrDefault(s => s.Station == station && s.Dir == dir) : null);

    private void LoadDistances(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.TryGetProperty("signalRuns", out var runs))
                foreach (var r in runs.EnumerateArray())
                    if (r.TryGetProperty("signals", out var s))
                    {
                        _signalRuns.Add(s.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()));
                        _limitRuns.Add(r.TryGetProperty("limits", out var lim)
                            ? lim.EnumerateArray().Select(x => (x[0].GetDouble(), x[1].GetDouble())).ToList()
                            : new List<(double, double)>());
                        _trackRuns.Add(r.TryGetProperty("tracks", out var tr)
                            ? tr.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32())
                            : new Dictionary<string, int>());
                    }
            if (root.TryGetProperty("stops", out var stops))
                foreach (var s in stops.EnumerateArray())
                    _stops.Add(new StopSpec(s.GetProperty("station").GetString()!, s.GetProperty("dir").GetString() == "down" ? Dir.Down : Dir.Up,
                        s.GetProperty("track").GetInt32(), s.GetProperty("signal").GetString()!,
                        s.TryGetProperty("before", out var b) ? b.GetDouble() : 0, s.TryGetProperty("after", out var a) ? a.GetDouble() : null,
                        s.TryGetProperty("couple", out var cp) ? cp.GetDouble() : null, s.TryGetProperty("stopB", out var sb) ? sb.GetDouble() : null));
            if (root.TryGetProperty("carsByUnyo", out var carsBy))
                foreach (var c in carsBy.EnumerateObject())
                    if (int.TryParse(c.Name, out var unyo) && c.Value.TryGetInt32(out var n)) _carsByUnyo[unyo] = n;
            if (root.TryGetProperty("circuitLengths", out var lens))
                foreach (var c in lens.EnumerateObject())
                    _circuitLengths[c.Name] = c.Value.GetDouble();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AiTrains.json を読めませんでした");
        }
    }

    private static Dictionary<string, HashSet<string>> LoadAdjacency(string path)
    {
        var adj = new Dictionary<string, HashSet<string>>();
        if (!File.Exists(path)) return adj;
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("trackCircuits", out var list)) return adj;
        HashSet<string> Of(string n) => adj.TryGetValue(n, out var s) ? s : adj[n] = new HashSet<string>();
        foreach (var tc in list.EnumerateArray())
        {
            var name = tc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.Length == 0 || !tc.TryGetProperty("adjacent", out var a)) continue;
            foreach (var x in a.EnumerateArray())
            {
                var other = x.GetString() ?? "";
                if (other.Length == 0) continue;
                Of(name).Add(other);
                Of(other).Add(name);
            }
        }

        return adj;
    }

    /// <summary>a が b と同じか後の時刻か（日をまたぐ所は、12時間以内の差で見る）。</summary>
    private static bool IsLater(TimeSpan a, TimeSpan b)
    {
        var d = (a - b).TotalHours;
        if (d > 12) d -= 24;
        if (d < -12) d += 24;
        return d >= 0;
    }

    private static TimeSpan? Time(string? hms) =>
        hms != null && TimeSpan.TryParseExact(hms, [@"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var t) ? t : null;

    // =========================================================================================
    // 型
    // =========================================================================================

    /// <summary>番線ごとの停止位置。CoupleBefore・StopBBefore は連結する列車の連結位置・停B（信号機の手前 m）。</summary>
    private sealed record StopSpec(string Station, Dir Dir, int Track, string Signal, double Before, double? After, double? CoupleBefore = null, double? StopBBefore = null);
    private sealed record SegCircuit(string Name, double Start, double End);
    private sealed record Seg(string? Signal, RouteDef? Route, double Start, double End, List<SegCircuit> Circuits);
    private sealed record PlanStop(string Name, string StationId, bool Stops, int? Track, TimeSpan? Arr, TimeSpan? Dep);

    private sealed class Train
    {
        public required string Id { get; init; }
        public int Seq { get; init; }
        public required string Number { get; set; }
        public required DiaTrain Dia { get; set; }
        public string? Shown { get; set; }
        public Dir Dir { get; set; }
        public int Cars { get; set; }
        public double Length { get; set; }
        public int CoupleStage { get; set; }      // 連結: 0 まだ・1 停B で停止中・2 連結位置へ・3 連結した
        public DateTime CoupleStopUtc { get; set; }
        public required string StartStation { get; set; }
        public int StartTrack { get; set; }
        public PlayerInfo Info { get; set; } = null!;
        public List<Seg> Path { get; } = new();
        public List<PlanStop> Plan { get; } = new();
        public int PlanIndex { get; set; }
        public double Head { get; set; }
        public double StartHead { get; set; }
        public double Speed { get; set; } // m/s
        public double PathEnd => Path[^1].End;
        public bool Blocked { get; set; }
        public string? EndSignal { get; set; }
        public HashSet<string> RedSignals { get; set; } = new();
        public bool Received { get; set; }
        public bool Dwelling { get; set; }
        public int DwellIndex { get; set; }
        public DateTime ArrivedUtc { get; set; }
        public bool DepartureAsked { get; set; }
        public bool WantDeparture { get; set; }
        public bool CanRequestDeparture { get; set; }
        public string? RouteMessage { get; set; }
        public string? PlanDetail { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Status { get; set; } = "";
        public bool Auto { get; init; }
        public DateTime? ExitUtc { get; set; }    // 赤山町から線区の外へ出て消す時刻
        public bool Shunting { get; set; }        // 入換（引上げ）中
        public double ShuntStartHead { get; set; }
        public PlanStop? ShuntStation { get; set; }
        public bool ShuntMoved { get; set; }      // 引上線から番線へ、道筋が延びた
        public bool FromShunt { get; set; }       // 引上線から折り返した（ホームへの入換を待つ。発車時刻まで止まらずに番線へ入る）
        public bool MidLine { get; set; }         // 駅間など、番線でない所から走り出した（継続乗務の在線を引き継いだ）。最初の駅で発車待ちにしない
    }
}
