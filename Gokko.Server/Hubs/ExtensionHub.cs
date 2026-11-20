using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>
/// ブラウザの内線電話の接続口（/extension）。
/// ・station: 信号制御盤（relaypanel.html）の電話機。ログインした人なら誰でも（役職は問わない）。その駅の回線に出る
/// ・handset: スマホ（radio.html）の携帯電話。同じ社員番号で PC から参加している本人の受話器になり、PC の接続がしたものとして扱う
/// </summary>
public sealed class ExtensionHub(ExtensionExchange exchange, ExtensionPublisher publisher, CrewRoster crew, ILogger<ExtensionHub> logger) : Hub
{
    private static readonly ExtensionResult NotJoined = new() { Message = "電話機として参加していません" };

    [HubMethodName(ExtensionProtocol.Join)]
    public async Task<ExtensionResult> Join(string? token, string? kind, string? station)
    {
        if (crew.InfoOf(crew.UserOfToken(token)) is not { } me)
            return new ExtensionResult { Message = "ログインが切れました。社員番号とパスワードでログインし直してください" };

        if (kind == "station")
        {
            if (exchange.AddStation(Context.ConnectionId, (station ?? "").Trim(), me.Id) is { } error) return new ExtensionResult { Message = error };
            logger.LogInformation("内線: 駅の電話機 {ConnectionId} {Station} ID:{CrewId}", Context.ConnectionId, station, me.Id);
        }
        else if (kind == "handset")
        {
            exchange.AddHandset(Context.ConnectionId, me.Id);
            logger.LogInformation("内線: スマホの携帯電話 {ConnectionId} ID:{CrewId}", Context.ConnectionId, me.Id);
        }
        else return new ExtensionResult { Message = "電話機の種類が違います" };

        await publisher.PublishAsync();
        return new ExtensionResult { Ok = true, Message = me.Name };
    }

    /// <summary>この接続が操作する電話機（盤ならこの接続、スマホなら結んでいる PC の接続）。</summary>
    private string? Self()
    {
        if (!exchange.IsBrowser(Context.ConnectionId)) return null;
        return exchange.IsHandset(Context.ConnectionId) ? exchange.PcOfHandset(Context.ConnectionId) : Context.ConnectionId;
    }

    private async Task<ExtensionResult> Do(Func<string, ExtensionResult> action)
    {
        if (Self() is not { } self) return NotJoined;
        var result = action(self);
        await publisher.PublishAsync();
        return result;
    }

    [HubMethodName(ExtensionProtocol.Dial)]
    public Task<ExtensionResult> Dial(string? number) => Do(self => exchange.Dial(self, number));

    [HubMethodName(ExtensionProtocol.Answer)]
    public Task<ExtensionResult> Answer(long callId) => Do(self => exchange.Answer(self, callId));

    [HubMethodName(ExtensionProtocol.Hangup)]
    public Task<ExtensionResult> Hangup(long callId) => Do(self => exchange.Hangup(self, callId));

    [HubMethodName(ExtensionProtocol.Hold)]
    public Task<ExtensionResult> Hold(long callId, bool on) => Do(self => exchange.Hold(self, callId, on));

    [HubMethodName(ExtensionProtocol.Transfer)]
    public Task<ExtensionResult> Transfer(long callId, string? number) => Do(self => exchange.Transfer(self, callId, number));

    [HubMethodName(ExtensionProtocol.Page)]
    public Task<ExtensionResult> Page() => Do(self => exchange.Page(self));

    [HubMethodName(ExtensionProtocol.SendAudio)]
    public async Task SendAudio(TrainRadioPacket packet)
    {
        if (packet == null || Self() is not { } self) return;
        await publisher.SendAudioAsync(exchange.Route(self, packet), packet);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exchange.IsBrowser(Context.ConnectionId))
        {
            exchange.RemoveBrowser(Context.ConnectionId);
            _ = publisher.PublishAsync();
        }

        await base.OnDisconnectedAsync(exception);
    }
}
