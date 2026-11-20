using Gokko.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gokko.Client;

/// <summary>列車ごとの放送装置（車内放送・車外放送・インターホン）。状態と音声の受け取り、操作、音声の送り出し。</summary>
public sealed partial class ServerLink
{
    private volatile OnboardState? _onboard;
    private HubConnection? _onboardConn;

    /// <summary>添乗: 乗客として乗る（参加と報告で知らせる）。</summary>
    public volatile bool Passenger;

    /// <summary>つないでいるサーバーが、添乗と放送装置を扱えるか（古いサーバーなら false）。</summary>
    public bool OnboardSupported { get; private set; }

    /// <summary>放送装置の最新の状態（未接続の間・列番がない間は null）。</summary>
    public OnboardState? Onboard => IsJoined && _onboardConn == _conn ? _onboard : null;

    /// <summary>放送装置の音声が届いた（受信のスレッドから呼ばれる）。</summary>
    public event Action<OnboardPacket>? OnboardAudioReceived;

    private void RegisterOnboard(HubConnection conn)
    {
        _onboard = null;
        _onboardConn = conn;
        OnboardSupported = false;
        conn.On<OnboardState>(Protocol.ReceiveOnboardState, s => _onboard = s);
        conn.On<OnboardPacket>(Protocol.ReceiveOnboardAudio, packet =>
        {
            if (_joined) OnboardAudioReceived?.Invoke(packet);
        });
    }

    /// <summary>放送を流す・止める、インターホンの入・切。返事がなければ、失敗として返す。</summary>
    public async Task<TrainRadioResult> OnboardCommandAsync(string method, params object?[] args)
    {
        var conn = _conn;
        if (conn == null || !IsJoined) return new TrainRadioResult { Message = "サーバーに接続していません" };
        if (!OnboardSupported) return new TrainRadioResult { Message = "サーバーが古いため、放送装置は使えません" };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await conn.InvokeCoreAsync<TrainRadioResult>(method, args, timeout.Token);
        }
        catch
        {
            return new TrainRadioResult { Message = "操作を確かめられませんでした。接続を確かめてください" };
        }
    }

    /// <summary>放送装置の音声 40ms 分を送る（返事は待たない）。</summary>
    public Task SendOnboardAudioAsync(OnboardPacket packet, CancellationToken ct)
    {
        var conn = _conn;
        if (conn == null || !IsJoined || !OnboardSupported) return Task.CompletedTask;
        return conn.SendAsync(Protocol.SendOnboardAudio, packet, ct);
    }
}
