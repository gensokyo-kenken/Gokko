using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// 駅の接近案内（乗務タブレットの tablet-arrivals.html）で、駅ごとに見る軌道回路（監視の範囲）と、駅からの近さ（回路の数）。
/// 範囲は「その駅の接近ブザーが鳴る範囲より、外へ 1 本（回路 1 つ）分広い範囲」。到着の順を入れ違えないため、ブザーより少し先から見る。
///  ・信号制御盤のある駅（wwwroot/relaypanel/stations/*.json）: 盤の接近ボタン（approachBells）の回路。when（ほかの駅の進路が出ているときだけ見る回路）も、
///    いつも含める（その駅へ向かう列車かどうかは、列車の行程で確かめるので、ほかの方面へ行く列車は出ない）
///  ・盤のない駅: 自動進路の接近の区間（Interlockings.json の場内信号機の approach）を、盤の駅と同じく外へ 2 本広げたものをブザーの範囲とみなす
///  そこから外へ 1 本広げる。「外」は、回路のつながり（TrackCircuits.json の adjacent）で、駅の回路から遠くなる向き。
/// </summary>
public sealed class ArrivalBoards
{
    public sealed record StationWatch(string Id, string Name, IReadOnlySet<string> Range, IReadOnlyDictionary<string, int> Distance, string Basis);

    private readonly Dictionary<string, StationWatch> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    /// <summary>盤のない駅で、接近の区間からブザーの範囲とみなすまでに広げる本数（盤の駅は接近の区間より 2 本外までブザーが鳴る）。</summary>
    private const int BellWiden = 2;

    /// <summary>駅のゾーンから、いくつ先のゾーン（駅=偶数、駅間=奇数）までの回路を範囲に入れるか（隣の駅の、さらに先の駅間まで）。</summary>
    private const int MaxZones = 3;

    /// <summary>ブザーの範囲から、さらに外へ見る本数。</summary>
    private const int WatchWiden = 1;

    public ArrivalBoards(string dataDir, IWebHostEnvironment env, RailData rail, ILogger<ArrivalBoards>? logger = null)
    {
        var adj = LoadAdjacency(Path.Combine(dataDir, "TrackCircuits.json"));
        var bells = LoadBells(Path.Combine(env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "relaypanel", "stations"), logger);

        foreach (var st in rail.ControlStations)
        {
            var zone = rail.Line.ZoneOfStation(st.Id);
            if (zone < 0) continue;
            var inside = rail.Line.CircuitsOfZone(zone).ToHashSet();
            if (inside.Count == 0) continue;
            var dist = Distances(adj, inside);

            // 盤のない駅: 駅から離れすぎた回路（隣の駅のさらに先）は入れない（接近の区間のデータの誤り。浜園の 1R に新野崎の TH67_SST があるなど）
            bool Far(string c) => rail.Line.ZoneOfCircuit(c) is { } z && Math.Abs(z - zone) > MaxZones;
            HashSet<string> range;
            string basis;
            if (bells.TryGetValue(st.Id, out var b))
            {
                range = b.Where(c => !inside.Contains(c)).ToHashSet();
                basis = "信号制御盤の接近ボタンの回路＋外へ1本";
            }
            else
            {
                range = rail.Routes.Values.Where(r => r.StationId == st.Id && r.Kind.Contains("場内"))
                    .SelectMany(r => r.Approach).Where(c => !inside.Contains(c) && !Far(c)).ToHashSet();
                for (var i = 0; i < BellWiden; i++) Widen(range, adj, dist, inside);
                range.RemoveWhere(Far);
                basis = "場内信号機の接近の区間＋外へ2本（ブザー相当）＋外へ1本";
            }

            if (range.Count == 0) continue;
            var panel = b != null;
            for (var i = 0; i < WatchWiden; i++) Widen(range, adj, dist, inside);
            if (!panel) range.RemoveWhere(Far); // 盤の駅は、盤に書いた回路をそのまま使う
            _stations[st.Id] = new StationWatch(st.Id, st.Name, range, dist, basis);
            _order.Add(st.Id);
        }

        logger?.LogInformation("接近案内: {Count}駅", _stations.Count);
    }

    public StationWatch? Get(string? id) => id != null ? _stations.GetValueOrDefault(id) : null;
    public IEnumerable<StationWatch> All => _order.Select(id => _stations[id]);

    /// <summary>範囲の回路のうち、駅から遠くなる向きの隣の回路を 1 つずつ足す。</summary>
    private static void Widen(HashSet<string> range, Dictionary<string, HashSet<string>> adj, Dictionary<string, int> dist, HashSet<string> inside)
    {
        var add = new List<string>();
        foreach (var c in range)
        {
            if (!adj.TryGetValue(c, out var ns) || !dist.TryGetValue(c, out var dc)) continue;
            add.AddRange(ns.Where(n => !range.Contains(n) && !inside.Contains(n) && dist.TryGetValue(n, out var dn) && dn > dc));
        }

        range.UnionWith(add);
    }

    /// <summary>駅の回路からの近さ（回路の数。駅の回路は 0）。</summary>
    private static Dictionary<string, int> Distances(Dictionary<string, HashSet<string>> adj, HashSet<string> inside)
    {
        var dist = inside.ToDictionary(c => c, _ => 0);
        var q = new Queue<string>(inside);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            if (dist[c] >= 60 || !adj.TryGetValue(c, out var ns)) continue;
            foreach (var n in ns.Where(n => !dist.ContainsKey(n)))
            {
                dist[n] = dist[c] + 1;
                q.Enqueue(n);
            }
        }

        return dist;
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
                Of(name).Add(other); // つながりは両向きにする（片方にしか書いていない所もあるため）
                Of(other).Add(name);
            }
        }

        return adj;
    }

    /// <summary>信号制御盤の駅ごとの、接近ボタンの回路（when の回路も含める）。</summary>
    private static Dictionary<string, HashSet<string>> LoadBells(string dir, ILogger? logger)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return map;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var data = JsonSerializer.Deserialize<RelayStationData>(File.ReadAllText(file), options);
                if (data == null || string.IsNullOrEmpty(data.Id) || data.ApproachBells.Count == 0) continue;
                map[data.Id] = data.ApproachBells.SelectMany(b => b.Circuits.Concat(b.When.SelectMany(w => w.Circuits))).ToHashSet();
            }
            catch (Exception e)
            {
                logger?.LogWarning("接近案内: 信号制御盤のデータを読めません {File}: {Message}", Path.GetFileName(file), e.Message);
            }
        }

        return map;
    }
}
