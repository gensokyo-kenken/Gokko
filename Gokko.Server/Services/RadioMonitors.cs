namespace Gokko.Server.Services;

/// <summary>列車無線のモニター（受信だけ）の接続。音声の中継で、いなければ手間を省く。</summary>
public sealed class RadioMonitors
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _monitors = new(); // 接続ID → 社員番号

    public bool IsEmpty
    {
        get { lock (_gate) return _monitors.Count == 0; }
    }

    public void Add(string connectionId, string crewId)
    {
        lock (_gate) _monitors[connectionId] = crewId;
    }

    public void Remove(string connectionId)
    {
        lock (_gate) _monitors.Remove(connectionId);
    }
}
