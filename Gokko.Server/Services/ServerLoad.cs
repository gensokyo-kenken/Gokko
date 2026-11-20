using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gokko.Server.Services;

/// <summary>
/// サーバーの負荷を測る（管理画面の「サーバーの負荷」と logs/load-yyyyMMdd.log）。
///
/// 5秒ごとに、このプロセスの CPU（全コアで 100%）・メモリ、マシン全体のメモリとロードアベレージ（Linux だけ）、
/// 配信1回（進路の処理と全員への状態の配信）にかかった時間、参加者とにぎやかしの列車の数を測る。
/// 直近1時間ぶんを画面用に持ち、1分ごとにまとめた1行（平均と最大）をファイルに書く。1行 150 バイトほどで、1日で 200KB くらい。
/// </summary>
public sealed class ServerLoad(PlayerRegistry players) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private const int HistoryCount = 720; // 5秒 × 720 = 1時間
    private const long MaxBytesPerDay = 10L * 1024 * 1024;

    public sealed record Sample(
        DateTime Utc, double CpuPercent, double ProcessMb, double GcHeapMb, int Threads,
        double? MachineTotalMb, double? MachineAvailableMb, double? LoadAverage1,
        double BroadcastAvgMs, double BroadcastMaxMs, int Broadcasts, int Players, int AiTrains);

    private readonly object _lock = new();
    private readonly List<Sample> _history = new();
    private readonly List<Sample> _minute = new();
    private double _bcSumMs, _bcMaxMs;
    private int _bcCount;
    private string _fileDay = "";
    private long _fileBytes;
    private bool _full;

    /// <summary>配信1回にかかった時間（StateBroadcaster から呼ぶ）</summary>
    public void RecordBroadcast(double ms)
    {
        lock (_lock)
        {
            _bcSumMs += ms;
            _bcCount++;
            if (ms > _bcMaxMs) _bcMaxMs = ms;
        }
    }

    public object View()
    {
        lock (_lock)
            return new
            {
                cores = Environment.ProcessorCount,
                intervalSeconds = (int)SampleInterval.TotalSeconds,
                current = _history.Count > 0 ? _history[^1] : null,
                history = _history.ToList(),
            };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastWall = Stopwatch.GetTimestamp();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(SampleInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                proc.Refresh();
                var cpu = proc.TotalProcessorTime;
                var wall = Stopwatch.GetTimestamp();
                var wallSec = Stopwatch.GetElapsedTime(lastWall, wall).TotalSeconds;
                var cpuPercent = wallSec > 0 ? (cpu - lastCpu).TotalSeconds / wallSec / Environment.ProcessorCount * 100 : 0;
                lastCpu = cpu;
                lastWall = wall;

                var (total, avail) = MachineMemory();
                var list = players.Snapshot();
                double bcAvg, bcMax;
                int bcCount;
                lock (_lock)
                {
                    bcAvg = _bcCount > 0 ? _bcSumMs / _bcCount : 0;
                    bcMax = _bcMaxMs;
                    bcCount = _bcCount;
                    _bcSumMs = _bcMaxMs = 0;
                    _bcCount = 0;
                }

                var s = new Sample(DateTime.UtcNow, Math.Round(cpuPercent, 1), Mb(proc.WorkingSet64), Mb(GC.GetTotalMemory(false)), proc.Threads.Count,
                    total, avail, LoadAverage(), Math.Round(bcAvg, 2), Math.Round(bcMax, 2), bcCount,
                    list.Count(p => !PlayerRegistry.IsVirtual(p.ConnectionId)), list.Count(p => p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix)));

                List<Sample>? minute = null;
                lock (_lock)
                {
                    _history.Add(s);
                    if (_history.Count > HistoryCount) _history.RemoveAt(0);
                    if (_minute.Count > 0 && _minute[0].Utc.ToLocalTime().Minute != s.Utc.ToLocalTime().Minute)
                    {
                        minute = _minute.ToList();
                        _minute.Clear();
                    }
                    _minute.Add(s);
                }
                if (minute != null) Write(minute);
            }
            catch
            {
                // 測れなくてもサーバーは止めない
            }
        }
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1024.0 / 1024.0, 1);

    /// <summary>マシン全体のメモリ（MB）。Linux は /proc/meminfo、ほかは .NET が知っている全体の量だけ。</summary>
    private static (double? Total, double? Available) MachineMemory()
    {
        try
        {
            if (File.Exists("/proc/meminfo"))
            {
                double? total = null, avail = null;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:")) total = Kb(line);
                    else if (line.StartsWith("MemAvailable:")) avail = Kb(line);
                    if (total != null && avail != null) break;
                }
                return (total, avail);
            }
            var info = GC.GetGCMemoryInfo();
            return (Mb(info.TotalAvailableMemoryBytes), null);
        }
        catch { return (null, null); }

        static double? Kb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var kb) ? Math.Round(kb / 1024, 1) : null;
        }
    }

    /// <summary>1分のロードアベレージ（Linux の /proc/loadavg。ないときは null）</summary>
    private static double? LoadAverage()
    {
        try
        {
            if (!File.Exists("/proc/loadavg")) return null;
            var first = File.ReadAllText("/proc/loadavg").Split(' ')[0];
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>1分ぶんをまとめて1行書く（平均/最大）</summary>
    private void Write(List<Sample> m)
    {
        try
        {
            var at = m[0].Utc.ToLocalTime();
            var day = at.ToString("yyyyMMdd");
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            var path = Path.Combine(dir, $"load-{day}.log");
            if (day != _fileDay)
            {
                Directory.CreateDirectory(dir);
                _fileDay = day;
                _fileBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                _full = false;
                if (_fileBytes == 0)
                    Append(path, "# 時刻 CPU%(平均/最大) プロセスMB(平均/最大) GCヒープMB マシンの空きMB/全体MB ロードアベレージ 配信ms(平均/最大) 参加者 にぎやかし" + Environment.NewLine);
            }
            if (_full) return;

            var last = m[^1];
            var bcCount = m.Sum(x => x.Broadcasts);
            var bcAvg = bcCount > 0 ? m.Sum(x => x.BroadcastAvgMs * x.Broadcasts) / bcCount : 0;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{at:yyyy-MM-dd HH:mm} cpu {m.Average(x => x.CpuPercent):0.0}/{m.Max(x => x.CpuPercent):0.0} " +
                $"mem {m.Average(x => x.ProcessMb):0}/{m.Max(x => x.ProcessMb):0} gc {last.GcHeapMb:0} " +
                $"machine {Num(m.Min(x => x.MachineAvailableMb))}/{Num(last.MachineTotalMb)} load {Num(last.LoadAverage1, "0.00")} " +
                $"bc {bcAvg:0.0}/{m.Max(x => x.BroadcastMaxMs):0.0} players {m.Max(x => x.Players)} ai {m.Max(x => x.AiTrains)}{Environment.NewLine}");
            if (_fileBytes > MaxBytesPerDay)
            {
                _full = true;
                line = $"{at:yyyy-MM-dd HH:mm} この日の負荷の記録が {MaxBytesPerDay / 1024 / 1024}MB を超えたので、ここで書くのをやめます{Environment.NewLine}";
            }
            Append(path, line);
        }
        catch
        {
            // 書けなくても測るのは続ける
        }

        static string Num(double? v, string f = "0") => v is { } x ? x.ToString(f, CultureInfo.InvariantCulture) : "-";
    }

    private void Append(string path, string text)
    {
        File.AppendAllText(path, text);
        _fileBytes += Encoding.UTF8.GetByteCount(text);
    }
}
