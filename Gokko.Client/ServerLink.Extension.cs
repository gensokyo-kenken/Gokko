using Gokko.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gokko.Client;

/// <summary>内線電話（列車無線とは別の回線）。状態と音声の受け取り、操作、音声の送り出し。</summary>
public sealed partial class ServerLink
{
    private volatile ExtensionState? _extension;
    private HubConnection? _extensionConn;

    /// <summary>内線の最新の状態（未接続の間は null）。</summary>
    public ExtensionState? Extension => IsJoined && _extensionConn == _conn ? _extension : null;

    /// <summary>内線の音声が届いた（受信のスレッドから呼ばれる）。</summary>
    public event Action<TrainRadioPacket>? ExtensionAudioReceived;

    private void RegisterExtension(HubConnection conn)
    {
        _extension = null;
        _extensionConn = conn;
        conn.On<ExtensionState>(ExtensionProtocol.ReceiveState, s => { if (s != null) _extension = s; });
        conn.On<TrainRadioPacket>(ExtensionProtocol.ReceiveAudio, packet =>
        {
            if (_joined) ExtensionAudioReceived?.Invoke(packet);
        });
    }

    /// <summary>内線の操作（かける・出る・切る・保留・転送・一斉呼出）。返事がなければ、失敗として返す。</summary>
    public async Task<ExtensionResult> ExtensionCommandAsync(string method, params object?[] args)
    {
        var conn = _conn;
        if (conn == null || !IsJoined) return new ExtensionResult { Message = "サーバーに接続していません" };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await conn.InvokeCoreAsync<ExtensionResult>(method, args, timeout.Token);
        }
        catch
        {
            return new ExtensionResult { Message = "操作を確かめられませんでした（サーバーが古いか、接続が切れています）" };
        }
    }

    /// <summary>内線の音声 40ms 分を送る（返事は待たない）。</summary>
    public Task SendExtensionAudioAsync(TrainRadioPacket packet, CancellationToken ct)
    {
        var conn = _conn;
        if (conn == null || !IsJoined) return Task.CompletedTask;
        return conn.SendAsync(ExtensionProtocol.SendAudio, packet, ct);
    }
}
