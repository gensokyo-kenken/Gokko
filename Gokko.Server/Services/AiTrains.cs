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
/// ・速さは、次の停車駅の時刻に間に合うように決める（残り距離 ÷ 残り時分 × 1.1、35〜100km/h）。止まる所の手前で減速する
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
    private static readonly TimeSpan TerminalLinger = TimeSpan.FromSeconds(60);

    private readonly RailData _rail;
    private readonly PlayerRegistry _players;
    private readonly GameClock _clock;
    private readonly RouteEngine _routes;
    private readonly CentralControl _central;
    private readonly IncidentManager _log;
    private readonly ILogger<AiTrains>? _logger;

    private readonly Dictionary<string, HashSet<string>> _adj;
    private readonly Dictionary<string, List<RouteDef>> _routesBySignal;
    private readonly List<Dictionary<string, double>> _signalRuns = new();
    private readonly List<StopSpec> _stops = new();
    private readonly Dictionary<string, double> _circuitLengths = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, Train> _trains = new();
    private int _seq;

    public AiTrains(string dataDir, RailData rail, PlayerRegistry players, GameClock clock, RouteEngine routes, CentralControl central,
        IncidentManager log, ILogger<AiTrains>? logger = null)
    {
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

    public sealed record View(string Id, string Train, int Cars, string Where, double SpeedKmh, string Status);

    public List<View> List()
    {
        lock (_lock)
            return _trains.Values.OrderBy(t => t.Seq).Select(t => new View(t.Id, t.Shown ?? t.Number, t.Cars,
                _rail.Line.DescribeZones(t.Info.Zones) ?? string.Join(" ", t.Info.TrackCircuits), Math.Round(t.Speed * 3.6, 1), t.Status)).ToList();
    }

    /// <summary>ダイヤの列車を、これから発車する最初の駅（時刻が来ていない、進路を扱う駅）に出す。</summary>
    public (bool Ok, string Message) Add(string number, int cars, string? stationName)
    {
        number = RailData.NormalizeTrain(number);
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
            var track = start.Track ?? st.Routes[dir.Value].Depart.Keys.Concat(st.Routes[dir.Value].Arrive.Keys).Order().FirstOrDefault();
            if (!st.TrackCircuit.TryGetValue(track, out var platform)) return (false, $"{st.Name} {track}番線の軌道回路が分かりません");

            var occupied = _players.Snapshot().SelectMany(p => p.TrackCircuits).ToHashSet();
            if (occupied.Contains(platform)) return (false, $"{st.Name} {track}番線（{platform}）にほかの列車がいます");

            var train = new Train
            {
                Id = $"{PlayerRegistry.AiPrefix}{++_seq}", Seq = _seq, Number = number, Dia = dia, Dir = dir.Value, Cars = cars,
                Length = cars * CarLength, StartStation = st.Name
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
            _log.Note($"にぎやかし: {number}（{cars}両）を {st.Name} {track}番線に出しました");
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
                List<string> departures = new();
                lock (_lock)
                {
                    foreach (var t in _trains.Values.ToList())
                    {
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
        var next = t.Plan.Skip(t.PlanIndex).FirstOrDefault(p => p.Stops);
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
                t.Status = "走行中";
                UpdateInfo(t);
                return;
            }
            else
            {
                t.Status = ready ? $"{stopNow.Name} で出発の進路待ち" : $"{stopNow.Name} に停車中（{(depAt is { } d ? d.ToString(@"hh\:mm\:ss") : "")}発）";
                UpdateInfo(t);
                return;
            }
        }

        // 最初の駅: 発車時刻まで待つ
        if (t.PlanIndex == 0 && t.Speed == 0 && t.Plan.Count > 0 && Math.Abs(t.Head - t.StartHead) < 0.5)
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
                cruise = remain <= 10 ? 100 / 3.6 : Math.Clamp(dd / remain * 1.1, 35 / 3.6, 100 / 3.6);
        }

        var room = Math.Max(0, target - t.Head);
        var allowed = Math.Sqrt(2 * Decel * room);
        var v = Math.Min(Math.Min(t.Speed + Accel * dt, cruise), allowed);
        if (t.Speed > cruise) v = Math.Max(cruise, t.Speed - Decel * dt);
        v = Math.Min(v, allowed);
        var move = Math.Min(v * dt, room);
        t.Head += move;
        t.Speed = room - move < 0.05 && v < 1 ? 0 : v;

        // 駅に着いた
        if (next != null && stopX is { } at && t.Speed == 0 && Math.Abs(t.Head - at) < 0.5)
        {
            t.Dwelling = true;
            t.DwellIndex = t.Plan.IndexOf(next);
            t.DepartureAsked = false;
            t.ArrivedUtc = utcNow;
        }

        // 通り過ぎた駅（通過）は、次の停車駅までの一覧から外す
        while (t.PlanIndex < t.Plan.Count && !t.Plan[t.PlanIndex].Stops && Passed(t, t.Plan[t.PlanIndex])) t.PlanIndex++;

        t.Status = t.Speed > 0 ? "走行中" : t.Blocked ? $"{t.EndSignal} の手前で停止" : "停止";
        Release(t);
        UpdateInfo(t);
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
            if (candidates.Count == 0) return; // 線路の終わり
            // 道筋が十分先まであれば、それ以上は延ばさない
            if (last.End - t.Head > 3000) return;

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

            if (seg == null)
            {
                // 止める信号機: 自分の進路の信号機（並んで建つものから）を選ぶ
                t.EndSignal = candidates.FirstOrDefault(c => _routesBySignal.TryGetValue(c, out var ds) && ds.Any(d => Adjacent(lastCircuit, d.Circuits))) ?? candidates[0];
                return;
            }

            t.Path.Add(seg);
            t.Blocked = false;
        }
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
                        _signalRuns.Add(s.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()));
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
        public required string Number { get; init; }
        public required DiaTrain Dia { get; set; }
        public string? Shown { get; set; }
        public Dir Dir { get; init; }
        public int Cars { get; init; }
        public double Length { get; init; }
        public required string StartStation { get; init; }
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
        public DateTime? FinishedUtc { get; set; }
        public string Status { get; set; } = "";
    }
}
