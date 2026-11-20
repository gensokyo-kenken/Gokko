namespace Gokko.Server.Services;

/// <summary>
/// スマホの列車無線（受話器）。スマホの画面でログインした社員番号と同じ番号で、PC のクライアントから
/// 運転士・車掌として参加している接続を探して結ぶ。
/// ・同じ社員番号で PC が2つ参加しているときは、新しく参加した方に結ぶ
/// ・同じ社員番号でスマホを2つ開いたときは、新しく開いた方を使う（前のスマホには「つなぎ替えました」と出す）
/// ・司令員は対象外
/// 結びつきは覚えず、PC の参加が変わるたびに探し直す（PC が抜ければ、そのまま「参加していません」になる）。
/// </summary>
public sealed class RadioPhones(PlayerRegistry players, CrewRelay relay)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _phones = new(); // スマホの接続ID → 社員番号
    private readonly Dictionary<string, string> _current = new(); // 社員番号 → いま使うスマホの接続ID

    /// <summary>スマホがひとつもないか（音声の中継で、探す手間を省く）。</summary>
    public bool IsEmpty
    {
        get { lock (_gate) return _phones.Count == 0; }
    }

    /// <summary>スマホを加える。同じ社員番号の前のスマホがあれば、その接続ID を返す。</summary>
    public string? Add(string phoneId, string crewId)
    {
        lock (_gate)
        {
            _phones[phoneId] = crewId;
            var old = _current.GetValueOrDefault(crewId);
            _current[crewId] = phoneId;
            return old != null && old != phoneId ? old : null;
        }
    }

    public void Remove(string phoneId)
    {
        lock (_gate)
        {
            if (!_phones.Remove(phoneId, out var crewId)) return;
            if (_current.GetValueOrDefault(crewId) == phoneId) _current.Remove(crewId);
        }
    }

    public bool IsPhone(string phoneId)
    {
        lock (_gate) return _phones.ContainsKey(phoneId);
    }

    /// <summary>参加しているスマホ（接続ID → 社員番号）。</summary>
    public Dictionary<string, string> Phones()
    {
        lock (_gate) return new Dictionary<string, string>(_phones);
    }

    /// <summary>同じ社員番号のスマホに置き換えられたか。</summary>
    public bool IsReplaced(string phoneId)
    {
        lock (_gate) return _phones.TryGetValue(phoneId, out var crewId) && _current.GetValueOrDefault(crewId) != phoneId;
    }

    /// <summary>社員番号ごとの、PC の参加（運転士・車掌）のうち新しいもの。</summary>
    private Dictionary<string, string> Pcs()
    {
        var best = new Dictionary<string, (string Id, DateTime At)>();
        void Consider(string crewId, string id, DateTime at)
        {
            if (crewId.Length == 0) return;
            if (!best.TryGetValue(crewId, out var b) || at > b.At) best[crewId] = (id, at);
        }

        foreach (var p in players.Snapshot())
            if (!p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix)) Consider(p.CrewId, p.ConnectionId, p.JoinedUtc);
        foreach (var c in relay.Conductors()) Consider(c.CrewId, c.ConnectionId, c.JoinedUtc);
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
    }

    /// <summary>いま結んでいる組（PC の接続ID → スマホの接続ID）。スマホがなければ空。</summary>
    public Dictionary<string, string> Links()
    {
        Dictionary<string, string> current;
        lock (_gate)
        {
            if (_current.Count == 0) return new();
            current = new Dictionary<string, string>(_current);
        }

        var pcs = Pcs();
        var links = new Dictionary<string, string>();
        foreach (var (crewId, phone) in current)
            if (pcs.TryGetValue(crewId, out var pc)) links[pc] = phone;
        return links;
    }

    /// <summary>このスマホに結んでいる PC の接続ID。なければ null。</summary>
    public string? PcOf(string phoneId)
    {
        string crewId;
        lock (_gate)
        {
            if (!_phones.TryGetValue(phoneId, out var id) || _current.GetValueOrDefault(id) != phoneId) return null;
            crewId = id;
        }

        return Pcs().GetValueOrDefault(crewId);
    }

    /// <summary>この PC にスマホを結んでいるか（そのあいだ、PC のマイクからの音声は捨てる）。</summary>
    public bool HasPhone(string pcId) => !IsEmpty && Links().ContainsKey(pcId);
}
