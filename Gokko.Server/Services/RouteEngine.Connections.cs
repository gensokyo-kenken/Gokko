namespace Gokko.Server.Services;

// 接続待ち（ダイヤ運転中）。組は ConnectionTable（ダイヤの注記「(接)」）から。
public sealed partial class RouteEngine
{
    /// <summary>遅れ見込みの警告を一度でも出した組（ログに何度も書かないため）。</summary>
    private readonly HashSet<string> _connectionNoted = new();

    /// <summary>
    /// 接続待ち: この駅で接続を受ける列車（ダイヤの「(接)…着」）が、まだ着いていなければ出発を開けない。待つ上限はない（司令が組ごとに切る）。
    /// 相手が走っていなければ（参加している運転士がいない・まだ出てきていない）待たない。相手がこの駅まで来ない（打ち切り）ときも待たない。
    /// 詰まり: 相手の前を、この列車の番線に入る列車が走っていると、相手は着けない（前の列車は番線が空くのを待ち、相手はそれを追い越せない）。
    ///   司令員が出勤していなければ、その組を自動で切って出す。出勤していれば待ったままにし、司令用管理画面で知らせて司令員に切ってもらう。
    /// </summary>
    private string? ConnectionHold(TrainRun run, StationRun st)
    {
        if (!DiaMode || run.Dia == null) return null;
        foreach (var c in _connections.ForWaiter(st.St.Id, run.Dia.Number))
        {
            if (_connections.IsCut(c)) continue;
            if (ConnectionFeeder(c) is not { } feeder || FeederArrived(feeder, c.StationId) != false) continue;
            // 相手がこの列車のいる番線に着く（大道寺の直接折返で 3番線を使い、3番線に着く相手を待つ）: この列車が出るまで相手は着けない
            if (feeder.Stations.FirstOrDefault(s => s.St.Id == st.St.Id) is { Track: { } ft } && ft == st.Track)
            {
                if (!_crew.DispatcherOnDuty())
                {
                    _connections.SetCut(c.Key, true);
                    _log.Note($"接続: {c.StationName} {c.Waiter} の {c.Feeder} 待ちを自動で切りました（司令員不在。{c.Feeder} は {ft}番線に着くので、{run.Train} が出るまで着けないため）");
                    continue;
                }
                return $"接続待ち（{feeder.Train} の{c.StationName}着を待つ。{feeder.Train} はこの列車のいる {ft}番線に着くので着けません。接続を切るか、{feeder.Train} の番線を変えてください）";
            }
            if (FeederBlockedBy(run, st, feeder) is { } blocker)
            {
                if (!_crew.DispatcherOnDuty())
                {
                    _connections.SetCut(c.Key, true);
                    _log.Note($"接続: {c.StationName} {c.Waiter} の {c.Feeder} 待ちを自動で切りました（司令員不在。{blocker.Train} が番線に入れず、{c.Feeder} が着けないため）");
                    continue;
                }
                return $"接続待ち（{feeder.Train} の{c.StationName}着を待つ。{blocker.Train} が番線に入れないため着けません。接続を切ってください）";
            }
            return $"接続待ち（{feeder.Train} の{c.StationName}着を待つ）";
        }
        return null;
    }

    /// <summary>
    /// 接続を受ける列車 feeder がこの駅 st に着けない理由の列車。feeder のすぐ前を同じ向きで走り、st で run と同じ番線に入る列車（追い越せない）。
    /// どちらも次に止まる駅が st（途中に追い越せる駅がない）ときだけ。なければ null。
    /// </summary>
    private TrainRun? FeederBlockedBy(TrainRun run, StationRun st, TrainRun feeder)
    {
        if (!feeder.HasPosition || NextStation(feeder) is not { } fs || fs.St.Id != st.St.Id) return null;
        return _runs.Values.FirstOrDefault(p => p != run && p != feeder && SameTrackBehind(run, st, p)
            && p.Dir == feeder.Dir && NextStation(p)?.St.Id == st.St.Id && Behind(feeder, p, fs));

        static StationRun? NextStation(TrainRun r) => r.Stations.FirstOrDefault(s => !s.Done);
        // f が p の後ろか。同じ駅間（Front が同じ）にいれば、駅間の中の位置で比べる。f の場内がまだ p を待っていても後ろ
        static bool Behind(TrainRun f, TrainRun p, StationRun fs) =>
            (PlotY(f) - PlotY(p)) * p.Dir.Step() < 0 || fs.ArriveWait.Contains($"（{p.Train}）", StringComparison.Ordinal);
    }

    private TrainRun? ConnectionFeeder(ConnectionTable.Connection c) =>
        _runs.Values.FirstOrDefault(r => r.HasPosition && r.Dia?.Number == c.Feeder);

    /// <summary>接続を受ける列車が、その駅に着いたか。true: 着いた（始発でそこにいる・もう過ぎたを含む）、false: まだ、null: その駅まで来ない。</summary>
    private bool? FeederArrived(TrainRun feeder, string stationId)
    {
        var fs = feeder.Stations.FirstOrDefault(s => s.St.Id == stationId);
        if (fs == null || IsCutOff(feeder, fs)) return null;
        if (fs.IsStart || fs.Done || feeder.ActualArr.ContainsKey(stationId)) return true;
        return false;
    }

    /// <summary>接続を受ける列車がその駅に着く見込み（運転日の秒）。始発駅で発車を待っていれば、ダイヤの発車時刻から数える。</summary>
    private double? FeederEta(TrainRun feeder, string stationId)
    {
        if (ReadyAt(feeder, stationId) is not { } secs) return null;
        var from = NowSec();
        if (feeder.Stations.FirstOrDefault(s => s.IsStart && !s.Done && s.St.Id != stationId) is { } start
            && DiaSec(feeder, start.St.Id, depart: true) is { } dep && dep > from)
            from = dep;
        return from + secs;
    }

    /// <summary>
    /// 司令用管理画面の接続待ちの一覧。待つ列車か接続を受ける列車が走っていて、待つ列車がまだその駅を出ていない組だけ。
    /// alert: 接続を受ける列車の着く見込みが、待つ列車のダイヤの発車時刻を過ぎる（接続で待つ列車が遅れる見込み）。
    /// </summary>
    public object ConnectionsView()
    {
        lock (_lock)
        {
            var now = NowSec();
            var rows = new List<object>();
            foreach (var c in _connections.All)
            {
                var waiter = _runs.Values.FirstOrDefault(r => r.HasPosition && r.Dia?.Number == c.Waiter);
                var ws = waiter?.Stations.FirstOrDefault(s => s.St.Id == c.StationId);
                if (waiter != null && (ws == null || ws.Done || IsCutOff(waiter, ws))) continue; // もう出た・来ない
                var feeder = ConnectionFeeder(c);
                if (waiter == null && feeder == null) continue;

                var arrived = feeder == null ? (bool?)null : FeederArrived(feeder, c.StationId);
                var eta = feeder != null && arrived == false ? FeederEta(feeder, c.StationId) : null;
                var cut = _connections.IsCut(c);
                var delay = eta is { } e ? e - c.WaiterDep : (double?)null;
                var waiting = ws != null && ws.DepartWait.StartsWith("接続待ち", StringComparison.Ordinal) && ws.Depart != GroupState.Requested;
                // 詰まり: 接続を受ける列車の前の列車が、待つ列車の番線に入れず、接続を受ける列車が着けない（切らないとお互いに動けない）
                var blocked = waiter != null && ws != null && feeder != null && !cut && arrived == false ? FeederBlockedBy(waiter, ws, feeder)?.Train : null;
                var alert = waiter != null && !cut && arrived == false && (delay > 0 || waiting && now > c.WaiterDep) || blocked != null;
                if (alert && _connectionNoted.Add(c.Key))
                    _log.Note($"接続の遅れ見込み: {c.StationName} {c.Waiter}（{FormatDiaTime(c.WaiterDep)}発）が {c.Feeder} を待って"
                              + (delay is { } dl && dl > 0 ? $"約{Math.Ceiling(dl / 60)}分遅れる見込み" : "遅れています"));

                rows.Add(new
                {
                    key = c.Key,
                    station = c.StationName,
                    waiter = c.Waiter,
                    waiterTrack = c.WaiterTrack,
                    waiterDep = FormatDiaTime(c.WaiterDep),
                    waiterRunning = waiter != null,
                    feeder = c.Feeder,
                    feederTrack = c.FeederTrack,
                    feederArr = FormatDiaTime(c.FeederArr),
                    // 接続を受ける列車: 走っていない（待たない）・着いた・来ない・見込み
                    feederState = feeder == null ? "走っていない" : arrived switch { true => "到着", null => "この駅に来ない", _ => "接近中" },
                    feederEta = eta is { } x ? FormatDiaTime(x) : null,
                    delaySeconds = delay is { } d2 ? (int)Math.Round(Math.Max(0, d2)) : (int?)null,
                    waiting,
                    blocked,
                    cut,
                    alert
                });
            }
            return new { total = _connections.All.Count, rows };
        }
    }
}
