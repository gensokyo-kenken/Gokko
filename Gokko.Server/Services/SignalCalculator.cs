using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 信号の連動。他の参加者の列車がいる軌道回路から、停止にする信号機を求める。
/// 防護区間（次の信号機までの軌道回路）に他の列車がいる信号機だけを R にして送る。手前の信号機の Y・YG は送らない。
/// 防護区間は、同じ駅のほかの進路だけに入っている回路を除いて見る（SignalDef.StopProtects。もう一方の番線の列車で場内を落とさない）。
/// 行き先が何通りもある信号機は、受け取る人のゲームに開いている進路で通る回路だけを見る（SignalDef.StopProtectsFor）。
/// TRAIN CREW は、上限で R にした信号機の手前を、灯式に従って自分で下げる（実機で確認: 下り閉塞49 を R にすると、
/// 0.5秒以内に 55＝Y、59＝YG になり、None で戻すと 0.5秒以内に G に戻った）。
/// ゲームは開いている進路を知っているので、分岐の手前の信号機も、実際に開いている進路の先に合わせて下がる。
/// 手前の現示をサーバーで計算して送っていたころは、ほかの列車が動いたあと古い Y・YG の上限が残り、本来より低い現示が出ることがあった。
/// ゲームは「今より低い現示」しか受け付けないので、ゲーム自身の判断（自分の列車・進路）と低い方が表示される。
/// </summary>
public sealed class SignalCalculator(RailData data)
{
    /// <summary>
    /// 他の列車がいる軌道回路から、停止（R）にする信号機を求める。gameRoutes は受け取る人のゲームに開いている進路
    /// （行き先が何通りもある信号機は、開いている進路で通る回路だけを見る）。
    /// </summary>
    public List<SignalCommand> Calculate(IReadOnlySet<string> occupiedByOthers, IReadOnlySet<(string Interlock, string Route)>? gameRoutes = null)
    {
        if (occupiedByOthers.Count == 0) return new List<SignalCommand>();
        Func<(string, string), bool> isSet = k => gameRoutes != null && gameRoutes.Contains(k);

        return data.Signals.Values
            .Where(s => !s.IsRepeater && s.StopProtects.Count > 0 && s.StopProtectsFor(isSet).Any(occupiedByOthers.Contains))
            .Select(s => new SignalCommand { Name = s.Name, Phase = "R" })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }
}
