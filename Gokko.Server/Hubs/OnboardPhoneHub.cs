using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>
/// 乗務タブレット（スマホ）のマイクで放送装置に話す接続口（/onboard-phone。ブラウザの SignalR・JSON で）。
/// ログインの合い札で参加し、同じ社員番号で PC のクライアントから運転士・車掌として乗っている人の声として送る。
/// スマホのマイクを入にしている間は、PC のマイクからの放送装置の声は捨てる（声が二重にならないように）。
/// 列車無線のスマホの受話器（/phone）とは別（こちらは列車無線を PC から外さない）。
/// </summary>
public sealed class OnboardPhoneHub(OnboardAudio onboard, CrewRoster crew, ILogger<OnboardPhoneHub> logger) : Hub
{
    public const string Path = "/onboard-phone";
    private const string CrewKey = "crew";

    /// <summary>参加する。引数はログインの合い札。</summary>
    public TrainRadioResult Join(string? token)
    {
        if (crew.UserOfToken(token) is not { } id) return new TrainRadioResult { Message = "ログインが切れました。ログインし直してください" };
        Context.Items[CrewKey] = id;
        return new TrainRadioResult { Ok = true, Message = "" };
    }

    private string? CrewId => Context.Items.TryGetValue(CrewKey, out var v) ? v as string : null;

    /// <summary>スマホのマイクの入・切。</summary>
    public async Task<TrainRadioResult> SetMic(bool on)
    {
        if (CrewId is not { } id) return new TrainRadioResult { Message = "参加していません" };
        onboard.SetPhoneMic(id, Context.ConnectionId, on);
        logger.LogInformation("放送装置のスマホのマイク ID:{CrewId} {On}", id, on ? "入" : "切");
        await onboard.PublishAsync(); // PC に「スマホのマイクで話しています」と出す
        return new TrainRadioResult { Ok = true, Message = on ? "スマホのマイクで話します" : "PC のマイクに戻しました" };
    }

    /// <summary>音声 40ms 分（話す種類は、そのとき選んでいる車内・車外・連絡）。</summary>
    public async Task SendAudio(OnboardPacket packet)
    {
        if (packet == null || CrewId is not { } id || !onboard.IsPhoneMic(id, Context.ConnectionId) || onboard.TalkerOf(id) is not { } pc) return;
        await onboard.SendAudioAsync(onboard.Route(pc, packet, fromPhone: true), packet); // 話した人は PC の接続（Route が SenderId を上書きする）
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (onboard.RemovePhone(Context.ConnectionId)) _ = onboard.PublishAsync();
        await base.OnDisconnectedAsync(exception);
    }
}
