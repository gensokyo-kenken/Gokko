using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// ほかの参加者の列車の進路（ClientState.OtherRoutes）を、自分のゲームにも設定する（在線共有モード）。
/// 自分のゲームでも対向列車・待避列車の出発や場内が開くのが見えるようにするため。
/// ・サーバーの一覧にあって、ゲームにない進路は SetRoute で送る（列車名は自分の列車のダイヤ名。ゲームが受け付ける列車名にするため）
/// ・自分が設定した進路が一覧からなくなったら、DeleteRoute2 で取り消す（同じ連動装置へは1回に1つ）。
///   自分のゲームにはその列車がいないので、ゲームの自動解除はあてにしない
/// ・届かなければ3秒おきに送り直す。シナリオを閉じた・ゲームとつなぎ直したときは記録を捨てる
/// </summary>
public sealed class OtherRouteApplier
{
    private static readonly TimeSpan Resend = TimeSpan.FromSeconds(3);

    /// <summary>自分が設定した、ほかの列車の進路（連動装置・進路名）。</summary>
    private readonly HashSet<(string Interlock, string Route)> _mine = new();
    private readonly Dictionary<(string, string), DateTime> _sentAt = new();
    private readonly Dictionary<(string, string), DateTime> _deleteSentAt = new();

    /// <summary>自分のゲームに設定した、ほかの列車の進路（RouteApplier が自分の進路と取り違えないように）。</summary>
    public IReadOnlySet<(string Interlock, string Route)> Mirrored => _mine;

    public void Reset()
    {
        _mine.Clear();
        _sentAt.Clear();
        _deleteSentAt.Clear();
    }

    public async Task TickAsync(GameLink link, ClientState? state)
    {
        var game = link.Latest;
        if (state == null || !link.IsConnected || game is not { InGame: true } || game.Routes == null) return;

        var present = game.Routes.Select(r => (r.Interlock, r.Route)).ToHashSet();
        var own = state.Routes.Select(r => (r.Interlocking, r.RouteName)).ToHashSet();
        var wanted = state.OtherRoutes.Where(r => !own.Contains((r.Interlocking, r.RouteName))).ToList();
        var wantedKeys = wanted.Select(r => (r.Interlocking, r.RouteName)).ToHashSet();
        var now = DateTime.UtcNow;

        // 設定する
        foreach (var r in wanted)
        {
            var key = (r.Interlocking, r.RouteName);
            if (present.Contains(key)) { _mine.Add(key); continue; }
            if (_sentAt.TryGetValue(key, out var at) && now - at < Resend) continue;
            try
            {
                var train = game.DiaName.Length > 0 ? game.DiaName : r.TrainNumber;
                // 出発信号機の列選は、自分の列車の種別（サーバーが入れる。なければゲームの種別から）。
                // ゲームには自分の列車名で設定するので、ほかの列車の種別を入れると運転台の列選の表示に出てしまうため
                var indicator = r.UseTrainType && r.Indicator.Length == 0 ? RouteApplier.TrainTypeIndicator(game.TrainClass) : r.Indicator;
                await link.SendCommandAsync("SetRoute", [r.Interlocking, r.RouteName, indicator, train, r.StopType]);
                _sentAt[key] = now;
                _mine.Add(key);
            }
            catch
            {
                return;
            }
        }

        // 一覧からなくなった進路を取り消す。
        // 自分の列車の進路になったものも取り消す（前の列車の進路が解除されたのと同じ周期に、同じ進路が自分の列車に来ることがある。
        // 写した進路は列選・停車の扱いがその列車のものなので、残すと自分の出発信号機にその列車の種別が出る。
        // 取り消したあと、RouteApplier が自分の列車の列選で設定し直す）
        foreach (var g in _mine.Where(k => !wantedKeys.Contains(k)).ToList().GroupBy(k => k.Interlock).Select(x => x.First()))
        {
            if (!present.Contains(g))
            {
                _mine.Remove(g);
                _deleteSentAt.Remove(g);
                continue;
            }

            if (_deleteSentAt.TryGetValue(g, out var at) && now - at < Resend) continue;
            try
            {
                await link.SendCommandAsync("DeleteRoute2", [g.Interlock, g.Route]);
                _deleteSentAt[g] = now;
            }
            catch
            {
                return;
            }
        }

        foreach (var k in _sentAt.Keys.Where(k => !wantedKeys.Contains(k)).ToList()) _sentAt.Remove(k);
    }

    /// <summary>自分が設定した、ほかの列車の進路をすべて取り消す（在線共有モードをやめるとき・終了時・ゲームの手動切断）。</summary>
    public async Task ClearAllAsync(GameLink link)
    {
        var game = link.Latest;
        if (!link.IsConnected || game?.Routes == null) { Reset(); return; }
        var present = game.Routes.Select(r => (r.Interlock, r.Route)).ToHashSet();
        foreach (var k in _mine.Where(present.Contains).ToList())
        {
            try
            {
                await link.SendCommandAsync("DeleteRoute2", [k.Interlock, k.Route]);
                await Task.Delay(100);
            }
            catch
            {
                break;
            }
        }

        Reset();
    }
}
