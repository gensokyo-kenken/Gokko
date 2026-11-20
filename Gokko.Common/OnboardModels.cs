namespace Gokko.Common;

/// <summary>
/// 列車ごとの放送装置の音の種類。音声の形式は列車無線と同じ（16kHz・40ms ずつの Opus、<see cref="TrainRadioFormat"/>）。
/// 届くのは同じ列番の運転士・車掌・添乗の中だけ（ほかの列車・司令には届かない）。
/// </summary>
public enum OnboardChannel
{
    /// <summary>流していない。</summary>
    None = 0,

    /// <summary>車内放送（運転士・車掌・添乗の全員に聞こえる）。</summary>
    Inside = 1,

    /// <summary>車外放送（全員に聞こえる。ホームに響くような音で流す）。</summary>
    Outside = 2,

    /// <summary>インターホン（運転士と車掌の打ち合わせ。入にしている運転士・車掌と、乗客でない添乗に聞こえる）。</summary>
    Intercom = 3
}

/// <summary>放送装置の音声 40ms 分。</summary>
public sealed class OnboardPacket
{
    public OnboardChannel Channel { get; set; }

    /// <summary>話した人の接続 ID。サーバーが上書きする（なりすましを許さない）。</summary>
    public string SenderId { get; set; } = "";

    /// <summary>Opus のパケット1つ（40ms 分）。</summary>
    public byte[] Opus { get; set; } = [];
}

/// <summary>同じ列車に乗っている人1人。</summary>
public sealed class OnboardMember
{
    /// <summary>接続 ID（届いた音声の話し手を見分ける）。</summary>
    public string Id { get; set; } = "";

    public CrewRole Role { get; set; }

    /// <summary>人ごとの音量を覚えるための鍵（社員番号）。</summary>
    public string Key { get; set; } = "";

    /// <summary>ユーザー名。</summary>
    public string Name { get; set; } = "";

    /// <summary>運転士・車掌: インターホンを入にしているか。</summary>
    public bool Intercom { get; set; }

    /// <summary>添乗: 乗客として乗っているか。</summary>
    public bool Passenger { get; set; }

    /// <summary>「運転士　山田」など。</summary>
    public string Label => (Role switch { CrewRole.Driver => "運転士", CrewRole.Conductor => "車掌", _ => Passenger ? "乗客" : "添乗" })
                           + (Name.Length > 0 ? $"　{Name}" : Key.Length > 0 ? $"　ID:{Key}" : "");
}

/// <summary>列車ごとの放送装置の状態（その人から見たもの）。</summary>
public sealed class OnboardState
{
    public string TrainNumber { get; set; } = "";

    /// <summary>自分の接続 ID。</summary>
    public string SelfId { get; set; } = "";

    /// <summary>自分の役割（運転士・車掌は話せる。添乗は聞くだけ）。</summary>
    public CrewRole Role { get; set; }

    /// <summary>添乗: 乗客として乗っている（放送だけが聞こえる）。</summary>
    public bool Passenger { get; set; }

    /// <summary>自分がインターホンを入にしているか（運転士・車掌）。</summary>
    public bool Intercom { get; set; }

    /// <summary>いま流している放送（車内・車外。流していなければ None）。同時に1人・1種類だけ。</summary>
    public OnboardChannel Broadcast { get; set; }

    /// <summary>放送を流している人の接続 ID（流していなければ空）。</summary>
    public string BroadcasterId { get; set; } = "";

    /// <summary>自分が乗務タブレット（スマホ）のマイクで話している（この間、PC のマイクからは送らない）。</summary>
    public bool PhoneMic { get; set; }

    /// <summary>同じ列車に乗っている人（自分も入る）。</summary>
    public List<OnboardMember> Members { get; set; } = new();

    /// <summary>自分が話せるか（運転士と、乗務を許可された車掌）。</summary>
    public bool CanTalk => Role is CrewRole.Driver or CrewRole.Conductor;

    /// <summary>
    /// 自分にインターホンの声が届くか。運転士・車掌は、自分の［連絡］が切でも聞こえる（［連絡］は自分の声を送るボタン）。
    /// 添乗は聞くだけ。乗客には聞かせない。
    /// </summary>
    public bool HearsIntercom => CanTalk || !Passenger;
}
