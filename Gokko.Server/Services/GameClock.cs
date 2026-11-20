namespace Gokko.Server.Services;

/// <summary>
/// ロープレ上の時刻。管理画面で「11:00」と設定すると、その時点から実時間と同じ速さで進む。
/// 今後、発車時刻による進路の開通（ダイヤ）の基準に使う。
/// </summary>
public sealed class GameClock
{
    private readonly object _lock = new();
    private TimeSpan? _base;
    private DateTime _setUtc;
    private bool _running;
    private double _speed = 1;

    /// <summary>
    /// 進む速さ（1 = 実時間と同じ）。早回しのシミュレーションのときだけ変える（管理画面の API で。本番は 1 のまま）。
    /// </summary>
    public double Speed
    {
        get { lock (_lock) return _speed; }
    }

    public void SetSpeed(double speed)
    {
        lock (_lock)
        {
            if (NowCore() is { } now) { _base = now; _setUtc = DateTime.UtcNow; }
            _speed = Math.Clamp(speed, 0.1, 20);
        }
    }

    public void Set(TimeSpan time, bool running)
    {
        lock (_lock)
        {
            _base = time;
            _setUtc = DateTime.UtcNow;
            _running = running;
        }
    }

    /// <summary>時刻を保ったまま、進める・止めるを切り替える。</summary>
    public void SetRunning(bool running)
    {
        lock (_lock)
        {
            if (NowCore() is not { } now) return;
            _base = now;
            _setUtc = DateTime.UtcNow;
            _running = running;
        }
    }

    public void Clear()
    {
        lock (_lock) _base = null;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _base != null && _running;
        }
    }

    public TimeSpan? Now
    {
        get
        {
            lock (_lock) return NowCore();
        }
    }

    public string? NowText => Now?.ToString(@"hh\:mm\:ss");

    /// <summary>
    /// 実際の時刻（UTC）を、画面に出す時刻の文字（"HH:mm:ss"）にする。ロープレ時刻を設定していれば、そのときのロープレ時刻
    /// （今のロープレ時刻から、進む速さで前後にずらす）。なければ日本時間（この PC の時刻）。
    /// </summary>
    public string TextAt(DateTime utc)
    {
        double speed;
        TimeSpan? now;
        lock (_lock) { now = NowCore(); speed = _speed; }
        if (now is not { } n) return utc.ToLocalTime().ToString("HH:mm:ss");
        var ticks = (n + TimeSpan.FromTicks((long)((utc - DateTime.UtcNow).Ticks * speed))).Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks).ToString(@"hh\:mm\:ss");
    }

    private TimeSpan? NowCore()
    {
        if (_base is not { } b) return null;
        var t = _running ? b + TimeSpan.FromTicks((long)((DateTime.UtcNow - _setUtc).Ticks * _speed)) : b;
        var ticks = t.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks);
    }
}
