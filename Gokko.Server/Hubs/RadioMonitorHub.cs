using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>
/// 列車無線のモニター（受信だけ。radio-monitor.html からブラウザの SignalR・JSON で /radio-monitor に）。
/// 乗務タブレットと同じログインの合い札で参加する（役職は問わない）。
/// 聞こえるのは共通通話と一斉通告だけ（個別通話は届かない）。話すことはできない。
/// </summary>
public sealed class RadioMonitorHub(RadioMonitors monitors, RadioPublisher publisher, CrewRoster crew, ILogger<RadioMonitorHub> logger) : Hub
{
    public const string Path = "/radio-monitor";
    public const string Group = "monitors";

    /// <summary>参加する。引数はログインの合い札。</summary>
    public async Task<RadioMonitorJoinResult> Join(string? token)
    {
        if (crew.InfoOf(crew.UserOfToken(token)) is not { } me)
            return new RadioMonitorJoinResult(false, "ログインが切れました。社員番号とパスワードでログインし直してください", "", "");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group);
        monitors.Add(Context.ConnectionId, me.Id);
        logger.LogInformation("列車無線のモニター {ConnectionId} ID:{CrewId}", Context.ConnectionId, me.Id);
        await publisher.PublishMonitorsAsync();
        return new RadioMonitorJoinResult(true, "", me.Id, me.Name);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        monitors.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

public sealed record RadioMonitorJoinResult(bool Ok, string Message, string CrewId, string Name);

/// <summary>モニターへ送る状態（1秒ごとと、変わった瞬間）。</summary>
public sealed record RadioMonitorStatus(int DispatcherCount, int MemberCount, List<TrainRadioCall> Calls);
