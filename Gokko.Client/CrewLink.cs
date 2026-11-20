using Gokko.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gokko.Client;

/// <summary>
/// 運転士と車掌の間の中継（/crew）との接続。配信（/hub）とは別の接続にする。
/// 切れたら3秒おきにつなぎ直し、そのたびに乗務に入り直す。列番が変わったら入り直す。
/// 受け取ったものはイベントで知らせる（SignalR のスレッドから呼ばれる）。
/// </summary>
public sealed class CrewLink
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile HubConnection? _conn;
    private volatile bool _joined;
    private string _password = "";
    private CrewRole _role;
    private volatile string _train = "";
    private string _joinedTrain = "";
    private readonly SemaphoreSlim _joinLock = new(1, 1);

    public event Action<DriverInput>? DriverInputReceived;
    public event Action<ConductorInput>? ConductorInputReceived;

    /// <summary>運転士へ: 車掌の乗務の申し込みが届いた。</summary>
    public event Action<ConductorRequest>? ConductorRequested;

    /// <summary>運転士へ: 申し込みが取り消された（引数は申し込みの番号）。</summary>
    public event Action<string>? RequestCancelled;

    /// <summary>車掌へ: 乗務の許可の状態が変わった。</summary>
    public event Action<CrewApproval>? ApprovalReceived;

    /// <summary>車掌: 乗務の許可の状態（列番を変えたら null に戻る）。</summary>
    public volatile CrewApproval? Approval;

    private string _crewToken = "";
    private volatile bool _allow;

    public volatile CrewStatus? Status;

    public bool IsJoined => _joined && _conn?.State == HubConnectionState.Connected;

    /// <summary>中継との往復の時間（ミリ秒。2秒ごとに測る。つながっていない・返事がなければ -1）。</summary>
    public int RttMs { get; private set; } = -1;

    public string Message { get; private set; } = "";

    public void Start(string url, string password, CrewRole role, string crewToken)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _password = password;
        _crewToken = crewToken;
        _role = role;
        var ct = _cts.Token;
        var hubUrl = NormalizeUrl(url);
        _loop = Task.Run(() => RunAsync(hubUrl, ct));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
            try { await _loop; } catch { }
        Status = null;
    }

    private long _rejectedMs;

    /// <summary>
    /// 乗る列番（空なら、まだ乗らない。毎周期呼ぶ）。変わったら入り直す。
    /// 断られていたら（その列番にもう同じ役割の人がいる）、5秒ごとに入り直す。
    /// </summary>
    public void SetTrain(string train)
    {
        train = (train ?? "").Trim();
        if (train == _train && !(_rejectedMs > 0 && Environment.TickCount64 - _rejectedMs > 5000)) return;
        _train = train;
        _rejectedMs = 0;
        _ = JoinAsync();
    }

    /// <summary>車掌: もう一度申し込む（［確定］を押し直した。断られていたら、また運転士に確かめる）。</summary>
    public void Rejoin() => _ = JoinAsync();

    /// <summary>運転士: 「車掌乗務を許可」の入・切。</summary>
    public void SetAllow(bool allow)
    {
        _allow = allow;
        var conn = _conn;
        if (conn != null && IsJoined) _ = SendQuietAsync(conn, CrewProtocol.SetConductorAllowed, allow);
    }

    /// <summary>運転士: 申し込みへの返事。</summary>
    public void Answer(string requestId, bool accept)
    {
        var conn = _conn;
        if (conn != null && IsJoined) _ = SendQuietAsync(conn, CrewProtocol.AnswerConductor, new ConductorAnswer { RequestId = requestId, Accept = accept });
    }

    public void SendDriverInput(DriverInput input)
    {
        var conn = _conn;
        if (conn == null || !IsJoined || _joinedTrain.Length == 0) return;
        _ = SendQuietAsync(conn, CrewProtocol.SendDriverInput, input);
    }

    public void SendConductorInput(ConductorInput input)
    {
        var conn = _conn;
        if (conn == null || !IsJoined || _joinedTrain.Length == 0) return;
        _ = SendQuietAsync(conn, CrewProtocol.SendConductorInput, input);
    }

    private static async Task SendQuietAsync(HubConnection conn, string method, object arg)
    {
        try { await conn.SendAsync(method, arg); }
        catch { /* 切断は接続ループ側で扱う */ }
    }

    private async Task JoinAsync()
    {
        var conn = _conn;
        if (conn == null || conn.State != HubConnectionState.Connected) return;
        await _joinLock.WaitAsync();
        try
        {
            var train = _train;
            if (train != _joinedTrain) Approval = null;
            var result = await conn.InvokeAsync<JoinResult>(CrewProtocol.Join,
                new CrewJoinRequest
                {
                    ProtocolVersion = Protocol.Version, Password = _password, Role = _role, TrainNumber = train, CrewToken = _crewToken, AllowConductor = _allow
                });
            _joined = true; // 合言葉・版が合っていれば、断られても中継にはつながっている
            if (result.Ok)
            {
                _joinedTrain = train;
                _rejectedMs = 0;
                Message = "";
            }
            else
            {
                _joinedTrain = ""; // 乗れなかった（前の列番からも抜けている）
                _rejectedMs = Environment.TickCount64;
                Message = result.Message;
            }
        }
        catch
        {
            // 切断は接続ループ側で扱う
        }
        finally
        {
            _joinLock.Release();
        }
    }

    private async Task RunAsync(string url, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var conn = new HubConnectionBuilder().WithUrl(url).Build();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.Closed += _ =>
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            };
            conn.On<DriverInput>(CrewProtocol.ReceiveDriverInput, i => DriverInputReceived?.Invoke(i));
            conn.On<ConductorInput>(CrewProtocol.ReceiveConductorInput, i => ConductorInputReceived?.Invoke(i));
            conn.On<CrewStatus>(CrewProtocol.ReceiveStatus, s => Status = s);
            conn.On<ConductorRequest>(CrewProtocol.ReceiveConductorRequest, r => ConductorRequested?.Invoke(r));
            conn.On<string>(CrewProtocol.ReceiveRequestCancelled, id => RequestCancelled?.Invoke(id));
            conn.On<CrewApproval>(CrewProtocol.ReceiveApproval, a =>
            {
                Approval = a;
                ApprovalReceived?.Invoke(a);
            });

            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await conn.StartAsync(ct);
                _conn = conn;
                _joinedTrain = "";
                await JoinAsync();
                _ = PingLoopAsync(conn, pingCts.Token);
                await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 切断ボタン
            }
            catch
            {
                Message = "中継につながりません";
            }
            finally
            {
                pingCts.Cancel();
                RttMs = -1;
                _joined = false;
                _conn = null;
                Status = null;
                Approval = null;
                try { await conn.DisposeAsync(); } catch { }
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>つながっている間、2秒ごとに中継との往復の時間を測る（返事が5秒なければ -1）。</summary>
    private async Task PingLoopAsync(HubConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && conn.State == HubConnectionState.Connected)
        {
            var started = Environment.TickCount64;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(5000);
                await conn.InvokeAsync<bool>(CrewProtocol.Ping, timeout.Token);
                if (!ct.IsCancellationRequested) RttMs = (int)(Environment.TickCount64 - started);
            }
            catch
            {
                if (!ct.IsCancellationRequested) RttMs = -1;
            }

            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>配信の接続先（…/hub）と同じサーバーの /crew にする。</summary>
    private static string NormalizeUrl(string url)
    {
        var hub = ServerLink.NormalizeUrl(url);
        return hub[..^Protocol.HubPath.Length] + CrewProtocol.HubPath;
    }
}
