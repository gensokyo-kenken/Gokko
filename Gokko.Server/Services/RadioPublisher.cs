using Gokko.Common;
using Gokko.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>
/// 列車無線の状態と音声を届ける。
/// ・状態: 配信の0.5秒を待たずに全員へ送る（呼出・応答・終了の直後）。スマホの受話器にも、結んでいる人の状態を送る
/// ・音声: スマホを結んでいる人には、PC ではなくスマホへ送る
/// ・モニター（受信だけ。<see cref="RadioMonitorHub"/>）には、共通通話・一斉通告の状態と音声だけを送る
/// </summary>
public sealed class RadioPublisher(
    IHubContext<GameHub> hub, IHubContext<PhoneHub> phoneHub, IHubContext<RadioMonitorHub> monitorHub, TrainRadio radio, RadioPhones phones,
    RadioMonitors monitors, ILogger<RadioPublisher> logger)
{
    public async Task PublishAsync()
    {
        try
        {
            var views = radio.Views();
            var sends = views.Select(kv => hub.Clients.Client(kv.Key).SendAsync(Protocol.ReceiveRadioState, kv.Value)).ToList();
            if (!phones.IsEmpty) sends.Add(PublishPhonesAsync(views));
            if (!monitors.IsEmpty) sends.Add(PublishMonitorsAsync());
            await Task.WhenAll(sends);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "列車無線の状態を送れませんでした");
        }
    }

    /// <summary>モニターへ、共通通話・一斉通告の状態を送る（1秒ごとと、変わった瞬間）。</summary>
    public async Task PublishMonitorsAsync()
    {
        try
        {
            if (monitors.IsEmpty) return;
            var (dispatchers, members, calls) = radio.PublicView();
            await monitorHub.Clients.Group(RadioMonitorHub.Group).SendAsync("ReceiveMonitorStatus", new RadioMonitorStatus(dispatchers, members, calls));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "列車無線のモニターへ状態を送れませんでした");
        }
    }

    /// <summary>スマホの受話器へ、結んでいる人の状態を送る（1秒ごとと、変わった瞬間）。</summary>
    public async Task PublishPhonesAsync(Dictionary<string, TrainRadioState>? views = null)
    {
        try
        {
            var all = phones.Phones();
            if (all.Count == 0) return;
            views ??= radio.Views();
            var byPhone = phones.Links().ToDictionary(kv => kv.Value, kv => kv.Key); // スマホ → PC
            await Task.WhenAll(all.Keys.Select(phone => phoneHub.Clients.Client(phone).SendAsync(Protocol.ReceivePhoneStatus, StatusOf(phone, byPhone, views))));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "スマホへ列車無線の状態を送れませんでした");
        }
    }

    private PhoneRadioStatus StatusOf(string phone, Dictionary<string, string> byPhone, Dictionary<string, TrainRadioState> views)
    {
        if (phones.IsReplaced(phone))
            return new PhoneRadioStatus { Replaced = true, Message = "別のスマホで、同じ社員番号の受話器が開かれました。こちらは切りました" };
        if (!byPhone.TryGetValue(phone, out var pc) || !views.TryGetValue(pc, out var state))
            return new PhoneRadioStatus { Message = "PC のクライアントで参加していません（運転士・車掌として参加すると、この受話器につながります）" };
        return new PhoneRadioStatus { Linked = true, Label = radio.LabelOf(pc), Radio = state };
    }

    /// <summary>音声を届ける。スマホを結んでいる人には、PC ではなくスマホへ。</summary>
    /// <param name="toMonitors">モニターにも聞かせる（共通通話・一斉通告。TrainRadio.Route が決める）。</param>
    public async Task SendAudioAsync(IReadOnlyList<string> recipients, TrainRadioPacket packet, bool toMonitors = false)
    {
        if (toMonitors && !monitors.IsEmpty)
        {
            try
            {
                await monitorHub.Clients.Group(RadioMonitorHub.Group).SendAsync(Protocol.ReceiveRadioAudio, packet);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "列車無線のモニターへ音声を送れませんでした");
            }
        }

        if (recipients.Count == 0) return;
        var links = phones.IsEmpty ? null : phones.Links();
        var toPc = new List<string>();
        var toPhone = new List<string>();
        foreach (var id in recipients)
            if (links != null && links.TryGetValue(id, out var phone)) toPhone.Add(phone);
            else toPc.Add(id);

        try
        {
            var sends = new List<Task>();
            if (toPc.Count > 0) sends.Add(hub.Clients.Clients(toPc).SendAsync(Protocol.ReceiveRadioAudio, packet));
            if (toPhone.Count > 0) sends.Add(phoneHub.Clients.Clients(toPhone).SendAsync(Protocol.PhoneReceiveAudio, packet));
            await Task.WhenAll(sends);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "列車無線の音声を送れませんでした");
        }
    }
}

/// <summary>スマホの受話器とモニターへ、1秒ごとに状態を送る（列番が変わった、PC が参加した・抜けた、なども届くように）。</summary>
public sealed class PhoneRadioTicker(RadioPublisher publisher, RadioPhones phones, RadioMonitors monitors) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!phones.IsEmpty) await publisher.PublishPhonesAsync();
            if (!monitors.IsEmpty) await publisher.PublishMonitorsAsync(); // モニターにも（列番が変わった、なども届くように）
        }
    }
}
