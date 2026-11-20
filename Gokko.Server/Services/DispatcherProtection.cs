using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 司令員からの区間防護。開始駅から終了駅まで（両端の駅構内と、その間の全駅・駅間）にいる上下列車に、防護無線の警報を鳴らさせる。
/// ・運転士の F5 の発報（<see cref="ProtectionRadio"/>）とは別に管理する。どちらかを解除しても、もう片方は続く
/// ・司令員なら誰でも発報・解除できる。区間はいくつ同時にあってもよい
/// ・発報した司令員が抜けたら、ほかの司令員に引き継ぐ。司令員が誰もいなくなったら解除する
/// </summary>
public sealed class DispatcherProtection(GameData data, TrainRadio trainRadio, IncidentManager log)
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(5);

    private sealed class Range
    {
        public required string FirstStation;
        public required string LastStation;
        public int First;
        public int Last;
        public required string Owner; // 接続ID
        public required string By; // 「司令員 ID:…」
        public DateTime StartedUtc;
        public string Label(LineMap line) => FirstStation == LastStation
            ? line.StationName(FirstStation)
            : $"{line.StationName(FirstStation)}～{line.StationName(LastStation)}";
    }

    private readonly object _gate = new();
    private readonly List<Range> _ranges = new();

    public TrainRadioResult Set(string id, bool active, string? firstStation, string? lastStation)
    {
        var dispatchers = trainRadio.Dispatchers();
        if (!dispatchers.TryGetValue(id, out var crewId)) return new() { Message = "区間防護を扱えるのは司令員だけです" };
        var first = data.Line.ZoneOfStation(firstStation ?? "");
        var last = data.Line.ZoneOfStation(lastStation ?? "");
        if (first < 0 || last < 0) return new() { Message = "開始駅と終了駅を選んでください" };
        if (first > last)
        {
            (first, last) = (last, first);
            (firstStation, lastStation) = (lastStation, firstStation);
        }

        lock (_gate)
        {
            var same = _ranges.FirstOrDefault(r => r.First == first && r.Last == last);
            if (!active)
            {
                if (same == null) return new() { Message = "その区間は発報していません" };
                _ranges.Remove(same);
                log.Note($"区間防護: {same.Label(data.Line)} を {TrainRadio.DispatcherLabel(crewId)} が解除");
                return new() { Ok = true, Message = $"{same.Label(data.Line)} の区間防護を解除しました" };
            }

            if (same != null) return new() { Ok = true, Message = $"{same.Label(data.Line)} はもう発報しています" };
            var range = new Range
            {
                FirstStation = firstStation!, LastStation = lastStation!, First = first, Last = last,
                Owner = id, By = TrainRadio.DispatcherLabel(crewId), StartedUtc = DateTime.UtcNow
            };
            _ranges.Add(range);
            log.Note($"区間防護: {range.Label(data.Line)} を {range.By} が発報");
            return new() { Ok = true, Message = $"{range.Label(data.Line)} の上下列車に防護無線を発報しています" };
        }
    }

    /// <summary>司令員が抜けた（その人の区間は、ほかの司令員に引き継ぐ。誰もいなければ解除）。</summary>
    public void Remove(string id)
    {
        var next = trainRadio.Dispatchers().FirstOrDefault(d => d.Key != id);
        lock (_gate)
            foreach (var r in _ranges.Where(r => r.Owner == id).ToList())
            {
                if (next.Key != null)
                {
                    r.Owner = next.Key;
                    continue;
                }

                _ranges.Remove(r);
                log.Note($"区間防護: {r.Label(data.Line)} を解除（司令員がいなくなった）");
            }
    }

    /// <summary>管理画面からの解除。</summary>
    public bool Clear(string label)
    {
        lock (_gate)
        {
            var r = _ranges.FirstOrDefault(x => x.Label(data.Line) == label);
            if (r == null) return false;
            _ranges.Remove(r);
            log.Note($"区間防護: {label} を管理画面から解除");
            return true;
        }
    }

    /// <summary>受信している参加者（接続ID）。報告が新しく、運転画面にいる列車だけ。</summary>
    public HashSet<string> Receivers(IReadOnlyList<PlayerInfo> snapshot, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        List<(int First, int Last)> ranges;
        lock (_gate)
        {
            if (_ranges.Count == 0) return [];
            ranges = _ranges.Select(r => (r.First, r.Last)).ToList();
        }

        return snapshot.Where(p => !PlayerRegistry.IsVirtual(p.ConnectionId)
                                   && p.InGame && p.TrainCrewConnected && now - p.LastReportUtc <= Freshness
                                   && data.Line.ZonesOf(p.TrackCircuits).Any(z => ranges.Any(r => z >= r.First && z <= r.Last)))
            .Select(p => p.ConnectionId).ToHashSet();
    }

    /// <summary>司令員へ配る状態。</summary>
    public DispatcherProtectionState View()
    {
        lock (_gate)
            return new DispatcherProtectionState
            {
                Stations = data.Line.StationIds.Select(s => new ProtectionStation { Id = s, Name = data.Line.StationName(s) }).ToList(),
                Active = _ranges.OrderBy(r => r.StartedUtc).Select(r => new DispatcherProtectionRange
                {
                    FirstStationId = r.FirstStation, LastStationId = r.LastStation, Label = r.Label(data.Line), By = r.By
                }).ToList()
            };
    }

    /// <summary>発報中の一覧（管理画面用）。</summary>
    public List<object> AdminView()
    {
        lock (_gate)
            return _ranges.OrderBy(r => r.StartedUtc).Select(r => (object)new { range = r.Label(data.Line), by = r.By, started = r.StartedUtc }).ToList();
    }
}
