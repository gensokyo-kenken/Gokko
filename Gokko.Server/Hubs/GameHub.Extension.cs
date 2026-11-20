using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>
/// 内線電話（PC のクライアントの電話機）。司令員・運転士・車掌が使う。
/// 列車無線のファイルとぶつからないように、別のファイルに分け、使うものは接続ごとに取り出す。
/// </summary>
public sealed partial class GameHub
{
    private ExtensionExchange Exchange => Context.GetHttpContext()!.RequestServices.GetRequiredService<ExtensionExchange>();
    private ExtensionPublisher ExtensionPublisher => Context.GetHttpContext()!.RequestServices.GetRequiredService<ExtensionPublisher>();

    private async Task<ExtensionResult> ExtensionAsync(Func<ExtensionExchange, ExtensionResult> action)
    {
        var result = action(Exchange);
        await ExtensionPublisher.PublishAsync();
        return result;
    }

    [HubMethodName(ExtensionProtocol.Dial)]
    public Task<ExtensionResult> ExtDial(string? number) => ExtensionAsync(x => x.Dial(Context.ConnectionId, number));

    [HubMethodName(ExtensionProtocol.Answer)]
    public Task<ExtensionResult> ExtAnswer(long callId) => ExtensionAsync(x => x.Answer(Context.ConnectionId, callId));

    [HubMethodName(ExtensionProtocol.Hangup)]
    public Task<ExtensionResult> ExtHangup(long callId) => ExtensionAsync(x => x.Hangup(Context.ConnectionId, callId));

    [HubMethodName(ExtensionProtocol.Hold)]
    public Task<ExtensionResult> ExtHold(long callId, bool on) => ExtensionAsync(x => x.Hold(Context.ConnectionId, callId, on));

    [HubMethodName(ExtensionProtocol.Transfer)]
    public Task<ExtensionResult> ExtTransfer(long callId, string? number) => ExtensionAsync(x => x.Transfer(Context.ConnectionId, callId, number));

    [HubMethodName(ExtensionProtocol.Page)]
    public Task<ExtensionResult> ExtPage() => ExtensionAsync(x => x.Page(Context.ConnectionId));

    [HubMethodName(ExtensionProtocol.SendAudio)]
    public async Task ExtSendAudio(TrainRadioPacket packet)
    {
        if (packet == null) return;
        var exchange = Exchange;
        if (exchange.HasHandset(Context.ConnectionId)) return; // スマホを受話器にしているあいだは、PC のマイクの音は送らない
        await ExtensionPublisher.SendAudioAsync(exchange.Route(Context.ConnectionId, packet), packet);
    }
}
