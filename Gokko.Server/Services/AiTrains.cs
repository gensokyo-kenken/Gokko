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
    private static readonly TimeSpan TerminalLinger = TimeSpan.FromSeconds(60);

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

    private readonly object _lock = new();
    private readonly Dictionary<string, Train> _trains = new();
    private int _seq;

    // ---- 自動運転列車アリ/ナシ（アリなら、ダイヤの列車を時刻に合わせて自動で出す） ----
    private bool _enabled;
    private readonly HashSet<string> _spawned = new(); // 自動で出した・続けて走った列番（同じ列車を二度出さない）
    private HashSet<string> _userTrains = new();       // ユーザーの列車（出勤した行路の列車・入力した列番）
    private HashSet<string> _userInGame = new();       // 運転画面に出ているユーザーの列番
    private DateTime _lastSpawnUtc;
    private TimeSpan? _lastGame;

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

    private (bool Ok, string Message) Add(string number, int cars, string? stationName, bool auto)
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
                start = s;
                break;
            }

            if (start == null) return (false, stationName is { Length: > 0 } ? $"{number} は {stationName} を通りません" : $"{number}: これから発車する駅がありません（ロープレ時刻を確かめてください）");
            var st = _rail.Stations[start.StationId!];
            // 番線: ダイヤの番線。ほかの列車がいる・ほかの列車の進路がかかっていれば、同じ向きに出られるほかの番線
            // （ほかの列車の進路の上に出すと、その列車も自分も動けなくなる。RouteEngine も「出現待ち」にする）
            var others = _players.Snapshot();
            var occupied = others.SelectMany(p => p.TrackCircuits).ToHashSet();
            foreach (var p in others)
            foreach (var key in p.GameRoutes)
                if (_rail.Routes.TryGetValue(key, out var rd)) occupied.UnionWith(rd.Circuits);
            var options = new List<int>();
            if (start.Track is { } dt) options.Add(dt);
            // ダイヤ運転モードでは、ダイヤの番線だけ（番線を変えない。空くまで待つ）
            if (!_central.DiaMode || options.Count == 0) options.AddRange(st.Routes[dir.Value].Depart.Keys.Order().Where(k => !options.Contains(k)));
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
            var stopSpec = FindStop(st.Name, dir.Value, track);
            var before = stopSpec?.Before ?? 20;
            var len = Math.Max(LengthOf(platform), train.Length + before + 10);
            train.Path.Add(new Seg(null, null, 0, len, [new SegCircuit(platform, 0, len)]));
            train.Head = len - before;
            train.StartHead = train.Head;
            train.StartTrack = track;
            train.Info = new PlayerInfo
            {
                ConnectionId = train.Id, Mode = ShareMode.Shared, InGame = true, TrainCrewConnected = true, TrainNumber = number,
                TrainNumberTyped = true, JoinedUtc = DateTime.UtcNow, LastReportUtc = DateTime.UtcNow,
                Stops = train.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList()
            };
            train.Status = $"{st.Name} {track}番線で発車待ち";
            UpdateInfo(train);
            _trains[train.Id] = train;
            _spawned.Add(number);
            _log.Note($"にぎやかし: {number}（{cars}両）を {st.Name} {track}番線に出しました" + (auto ? "（自動）" : ""));
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
                lock (_lock)
                {
                    _userTrains = users;
                    _userInGame = inGame;
                    foreach (var t in _trains.Values.ToList())
                    {
                        // ユーザーがその列番で出てきたら、自動運転の列車は消して、ユーザーに運転してもらう（途中駅からでも）
                        if (inGame.Contains(t.Number))
                        {
                            _trains.Remove(t.Id);
                            _log.Note($"にぎやかし: {t.Number} はユーザーが運転を始めたので消しました");
                            continue;
                        }

                        Step(t, dt * _clock.Speed, now);
                        if (t.WantDeparture) { departures.Add(t.Id); t.WantDeparture = false; }
                        if (t.FinishedUtc is { } f && now - f > TerminalLinger)
                        {
                            _trains.Remove(t.Id);
                            _log.Note($"にぎやかし: {t.Number} は {t.Status}。在線を消しました");
                        }
                    }
                }

                // 出発要求（RouteEngine のロックの中から呼ばないように、外で）
                foreach (var id in departures) _routes.RequestDeparture(id);

                // 自動運転列車アリ: 時刻が近づいたダイヤの列車を出す（2秒ごと）
                if (now - _lastSpawnUtc > TimeSpan.FromSeconds(2))
                {
                    _lastSpawnUtc = now;
                    AutoSpawn(users);
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
                // ダイヤではまだ先へ行く（線区の外へ出ていく）: 発車時刻になったら消す
                if (t.Dia.Stops.LastOrDefault()?.StationId != stopNow.StationId)
                {
                    t.Status = ready ? $"{stopNow.Name} から線区の外へ" : $"{stopNow.Name} に停車中（{(depAt is { } d0 ? d0.ToString(@"hh\:mm\:ss") : "")}発。この先は線区の外）";
                    if (ready) t.FinishedUtc ??= utcNow - TerminalLinger;
                    UpdateInfo(t);
                    return;
                }

                // 折返が「入換」: 自動進路構成が開ける入換（引上げ）の進路で引上線へ入り、そこで折り返しの列番になる
                if (_rail.Turns.TurnOf(t.Dia) is { Kind: "折返", How: "入換", Number: { Length: > 0 } sn } && _rail.DiaOf(sn) != null)
                {
                    if (IsUserTrain(sn)) { HandOver(t, sn); return; }
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

                    t.Status = $"{stopNow.Name} で入換（引上げ）の進路待ち" + (t.PlanDetail is { } pd1 ? $"（{pd1}）" : "");
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
            if (ready && t.PathEnd - (t.Blocked ? SignalMargin : 2) > t.Head + 5)
            {
                // 次の周期から走る（この周期の「次に止まる駅」は今の駅なので、ここで動かすと、また着いたことになる）
                t.Dwelling = false;
                t.PlanIndex = t.DwellIndex + 1;
                t.FromShunt = false;
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
        if (t.PlanIndex == 0 && t.Speed == 0 && t.Plan.Count > 0 && Math.Abs(t.Head - t.StartHead) < 0.5 && !t.FromShunt)
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
        var atEnd = next != null && stopX == null && !t.Blocked && t.Speed == 0 && t.PathEnd - t.Head < 5 && !(t.FromShunt && !t.ShuntMoved); // 引上線で折り返した直後（まだ番線へ動いていない）は除く
        if (next != null && (stopX is { } at && t.Speed == 0 && Math.Abs(t.Head - at) < 0.5 || atEnd))
        {
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
    /// </summary>
    private void ShuntReverse(Train t)
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

        var before = FindStop(st.Name, dir, track)?.Before ?? 20;
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
                    seg = MakeSeg(name, open, Order(open.Circuits, lastCircuit), last.End);
                }
                else if (_rail.Signals.TryGetValue(name, out var sig) && sig.Protects.Count > 0)
                {
                    seg = MakeSeg(name, null, Order(sig.Protects, lastCircuit), last.End);
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
            return MakeSeg(d.Signal, d, Order(d.Circuits, lastCircuit), last.End);
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

    /// <summary>次の停車駅の停止位置（道筋の上で分かれば）。</summary>
    private double? StopX(Train t, PlanStop p)
    {
        var track = p.Track ?? 0;
        var spec = FindStop(p.Name, t.Dir, track);
        if (spec != null && BoundaryOf(t, spec.Signal) is { } b)
            return spec.After is { } after ? b + after : b - spec.Before;
        // 表にない番線: その駅の出発信号機の 20m 手前
        if (t.Blocked && t.EndSignal is { } e && e.StartsWith(p.Name) && e.Contains("出発")) return t.PathEnd - 20;
        // 終着で出発信号機がない: 番線の軌道回路の終わりの手前
        if (t.Blocked && _rail.Stations.Values.FirstOrDefault(s => s.Name == p.Name) is { } st && p.Track is { } tr && st.TrackCircuit.TryGetValue(tr, out var tc)
            && t.Path[^1].Circuits[^1].Name == tc)
            return t.PathEnd - 20;
        return null;
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

    /// <summary>
    /// ダイヤの列車のうち、進路を扱う駅の発車時刻が近い（2分前〜30秒過ぎ）ものを、その駅に出す。
    /// 出さないもの: ユーザーの列車、もう出した・走っている列番、走っている列車がこのあと続けて・折り返して走る列番。
    /// 番線にほかの列車がいるなどで出せなければ、時刻の範囲の間は2秒ごとに試し直す。
    /// </summary>
    private void AutoSpawn(HashSet<string> users)
    {
        var now = _clock.Now;
        List<(string Number, string Station)> todo = new();
        lock (_lock)
        {
            if (!_enabled || now is not { } n || !_clock.IsRunning) return;
            // ロープレ時刻を戻したら、出した記録を消す（もう一度出せるように）
            if (_lastGame is { } lg && n < lg - TimeSpan.FromMinutes(2)) _spawned.Clear();
            _lastGame = n;

            // 走っている列車が、このあと続けて・折り返して走る列番
            var covered = new HashSet<string>();
            foreach (var t in _trains.Values)
            {
                var d = t.Dia;
                for (var k = 0; k < 10 && d != null; k++)
                {
                    var nextNo = d.Next?.Number ?? (_rail.Turns.TurnOf(d) is { Kind: "折返", Number: { Length: > 0 } tn } ? tn : null);
                    if (nextNo == null) break;
                    covered.Add(RailData.NormalizeTrain(nextNo));
                    d = _rail.DiaOf(nextNo);
                }
            }

            foreach (var dia in _rail.Dia.Values)
            {
                var number = RailData.NormalizeTrain(dia.Number);
                if (_spawned.Contains(number) || users.Contains(number) || covered.Contains(number) || _trains.Values.Any(t => t.Number == number)) continue;
                var stops = dia.Stops.Where(s => s.StationId != null && _rail.Stations.TryGetValue(s.StationId, out var sd) && sd.RouteControl).ToList();
                if (stops.Count < 2) continue;
                // 時刻がまだ過ぎていない最初の駅（30秒の遅れまで）。終着には出さない
                var start = stops.Take(stops.Count - 1).FirstOrDefault(s => Time(s.Dep ?? s.Arr) is { } t && t >= n - TimeSpan.FromSeconds(30));
                if (start == null || Time(start.Dep ?? start.Arr) is not { } dep || dep > n + TimeSpan.FromMinutes(2)) continue;
                if (dep < n - TimeSpan.FromSeconds(30)) continue; // 時刻を過ぎた駅には出さない（遅れて出すと、ほかの列車との順番が崩れる）
                todo.Add((number, start.Name));
            }
        }

        foreach (var (number, station) in todo) Add(number, 6, station, true);
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
        p.LastReportUtc = DateTime.UtcNow;
    }

    // =========================================================================================
    // 距離の表
    // =========================================================================================

    private double LengthOf(string circuit) =>
        _circuitLengths.TryGetValue(circuit, out var l) ? l : circuit.StartsWith("TH") ? DefaultStationCircuit : DefaultLineCircuit;

    /// <summary>信号機 a から b までの距離（同じ走りの記録に両方あれば）。</summary>
    private double? Distance(string a, string b)
    {
        foreach (var run in _signalRuns)
            if (run.TryGetValue(a, out var x) && run.TryGetValue(b, out var y) && y >= x) return y - x;
        return null;
    }

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
                        s.TryGetProperty("before", out var b) ? b.GetDouble() : 0, s.TryGetProperty("after", out var a) ? a.GetDouble() : null));
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

    private static TimeSpan? Time(string? hms) =>
        hms != null && TimeSpan.TryParseExact(hms, [@"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var t) ? t : null;

    // =========================================================================================
    // 型
    // =========================================================================================

    private sealed record StopSpec(string Station, Dir Dir, int Track, string Signal, double Before, double? After);
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
        public int Cars { get; init; }
        public double Length { get; init; }
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
        public bool Shunting { get; set; }        // 入換（引上げ）中
        public double ShuntStartHead { get; set; }
        public PlanStop? ShuntStation { get; set; }
        public bool ShuntMoved { get; set; }      // 引上線から番線へ、道筋が延びた
        public bool FromShunt { get; set; }       // 引上線から折り返した（ホームへの入換を待つ。発車時刻まで止まらずに番線へ入る）
    }
}
