using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>
/// スマホの列車無線の接続口（/phone。radio.html からブラウザの SignalR・JSON で）。
/// 社員番号とパスワードでログインした合い札で参加し、同じ社員番号で PC から参加している本人の受話器になる。
/// 呼出・取消・終了・音声は、結んでいる PC の接続が行ったものとして扱う。
/// </summary>
public sealed class PhoneHub(RadioPhones phones, TrainRadio trainRadio, RadioPublisher publisher, CrewRoster crew, ILogger<PhoneHub> logger) : Hub
{
    private static readonly TrainRadioResult NotLinked = new() { Message = "PC のクライアントで参加していません" };

    [HubMethodName(Protocol.PhoneJoin)]
    public async Task<PhoneJoinResult> Join(string? token)
    {
        if (crew.InfoOf(crew.UserOfToken(token)) is not { } me)
            return new PhoneJoinResult { Message = "ログインが切れました。社員番号とパスワードでログインし直してください" };
        var old = phones.Add(Context.ConnectionId, me.Id);
        logger.LogInformation("スマホの受話器 {ConnectionId} ID:{CrewId}{Old}", Context.ConnectionId, me.Id, old != null ? "（前のスマホと入れ替え）" : "");
        await publisher.PublishAsync(); // PC の無線機に「スマホ」と出す。スマホにも今の状態を送る
        return new PhoneJoinResult { Ok = true, CrewId = me.Id, Name = me.Name };
    }

    private async Task<TrainRadioResult> AsPcAsync(Func<string, TrainRadioResult> action)
    {
        if (phones.PcOf(Context.ConnectionId) is not { } pc) return NotLinked;
        var result = action(pc);
        await publisher.PublishAsync();
        return result;
    }

    [HubMethodName(Protocol.PhoneRequestCall)]
    public Task<TrainRadioResult> RequestCall() => AsPcAsync(trainRadio.RequestCall);

    [HubMethodName(Protocol.PhoneCancelCall)]
    public Task<TrainRadioResult> CancelCall() => AsPcAsync(trainRadio.CancelRequest);

    [HubMethodName(Protocol.PhoneEndCall)]
    public Task<TrainRadioResult> EndCall(long callId) => AsPcAsync(pc => trainRadio.End(pc, callId));

    [HubMethodName(Protocol.PhoneRespondBroadcast)]
    public Task<TrainRadioResult> RespondBroadcast(long callId) => AsPcAsync(pc => trainRadio.RespondBroadcast(pc, callId));

    [HubMethodName(Protocol.PhoneSendAudio)]
    public async Task SendAudio(TrainRadioPacket packet)
    {
        if (packet == null || phones.PcOf(Context.ConnectionId) is not { } pc) return;
        await publisher.SendAudioAsync(trainRadio.Route(pc, packet, out var toMonitors), packet, toMonitors); // 話した人は PC の接続（Route が SenderId を上書きする）
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (phones.IsPhone(Context.ConnectionId))
        {
            phones.Remove(Context.ConnectionId);
            logger.LogInformation("スマホの受話器を閉じました {ConnectionId}", Context.ConnectionId);
            _ = publisher.PublishAsync(); // PC の無線機を元に戻す
        }

        await base.OnDisconnectedAsync(exception);
    }
}
