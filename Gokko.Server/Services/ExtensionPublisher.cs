using Gokko.Common;
using Gokko.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>
/// 内線の状態と音声を届ける。PC の電話機は /hub、信号制御盤とスマホの受話器は /extension。
/// スマホを受話器にしている人には、音声を PC ではなくスマホへ送る。
/// </summary>
public sealed class ExtensionPublisher(
    IHubContext<GameHub> hub, IHubContext<ExtensionHub> browserHub, ExtensionExchange exchange, ILogger<ExtensionPublisher> logger)
{
    public async Task PublishAsync()
    {
        try
        {
            var views = exchange.Views();
            var sends = new List<Task>();
            foreach (var (id, view) in views)
            {
                if (exchange.IsBrowser(id)) sends.Add(browserHub.Clients.Client(id).SendAsync(ExtensionProtocol.ReceiveState, view));
                else sends.Add(hub.Clients.Client(id).SendAsync(ExtensionProtocol.ReceiveState, view));
            }

            foreach (var (phone, pc) in exchange.Handsets())
                sends.Add(browserHub.Clients.Client(phone).SendAsync(ExtensionProtocol.ReceiveState, HandsetView(phone, pc, views)));
            await Task.WhenAll(sends);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "内線の状態を送れませんでした");
        }
    }

    private ExtensionState HandsetView(string phone, string? pc, Dictionary<string, ExtensionState> views)
    {
        if (exchange.IsReplacedHandset(phone))
            return new ExtensionState { Reason = "別のスマホで、同じ社員番号の携帯電話が開かれました。こちらは切りました", Kind = "replaced" };
        if (pc == null || !views.TryGetValue(pc, out var view))
            return new ExtensionState { Reason = "PC のクライアントで参加していません（運転士・車掌・司令員として参加すると、この携帯電話につながります）" };
        return view;
    }

    /// <summary>音声を届ける。スマホを結んでいる人には、PC ではなくスマホへ。盤の電話機はブラウザへ。</summary>
    public async Task SendAudioAsync(IReadOnlyList<string> recipients, TrainRadioPacket packet)
    {
        if (recipients.Count == 0) return;
        var links = exchange.Links();
        var toPc = new List<string>();
        var toBrowser = new List<string>();
        foreach (var id in recipients)
        {
            if (links.TryGetValue(id, out var phone)) toBrowser.Add(phone);
            else if (exchange.IsBrowser(id)) toBrowser.Add(id);
            else toPc.Add(id);
        }

        try
        {
            var sends = new List<Task>();
            if (toPc.Count > 0) sends.Add(hub.Clients.Clients(toPc).SendAsync(ExtensionProtocol.ReceiveAudio, packet));
            if (toBrowser.Count > 0) sends.Add(browserHub.Clients.Clients(toBrowser).SendAsync(ExtensionProtocol.ReceiveAudio, packet));
            await Task.WhenAll(sends);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "内線の音声を送れませんでした");
        }
    }
}

/// <summary>1秒ごとに内線の状態を送る（参加・列番・速度の変化や、呼出の期限切れも届くように）。</summary>
public sealed class ExtensionTicker(ExtensionPublisher publisher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await publisher.PublishAsync();
    }
}
