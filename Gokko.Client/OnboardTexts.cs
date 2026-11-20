namespace Gokko.Client;

/// <summary>
/// 添乗と放送装置（車内・車外・連絡）の画面に出す説明の文言。ここだけ直せば、画面の説明が変わる。
/// 改行は \n（空行を入れるなら \n\n）。長い文は + でつないでよい。
/// </summary>
internal static class OnboardTexts
{
    // ---- 起動時のモード選択（ModeDialog） ----

    public const string RiderChoiceTitle = "乗客・添乗（在線共有）";

    public const string RiderChoiceBody =
        "同じ列番の運転士の列車に乗って、運転士の操作を映すだけの乗り方です。" +
        "「乗客として乗る」にすると、聞こえるのは車内・車外放送だけになります。";

    // ---- 本体の「車内放送」タブ ----

    /// <summary>運転士・車掌に出す説明。</summary>
    public const string CrewHelp =
        "車内・車外・連絡は、どれか1つだけ選べます。押すと入、もう一度押すと切です。" +
        "ほかの2つは自動で切れます。\n\n" +
        "車内・車外（放送）: 流している間は、声を出すと自動で送ります。列車ごとに1人しか流せません。\n\n" +
        "連絡 : 入にしている間は運転士と車掌がつながり、声を出すと自動で送ります。受ける側は入にしなくても聞こえます。\n\n" +
        "［キー設定］で、車内・車外・連絡にキーやコントローラーのボタンを当てられます（ゲームを前面にしたままでも効きます）。" ;

    /// <summary>添乗に出す説明。</summary>
    public const string RiderHelp =
        "添乗は連絡（インターホン）と列車無線も聞こえます。\n" +
        "乗客として乗ると、聞こえるのは車内・車外放送だけになります。";

    /// <summary>添乗の「乗客として乗る」のチェック。</summary>
    public const string PassengerCheck = "乗客として乗る";

    /// <summary>「乗客として乗る」を入れたとき・切ったときのお知らせ。</summary>
    public const string PassengerOn = "乗客として乗車";
    public const string PassengerOff = "添乗として乗車";

    /// <summary>モニターのチェック（タブ）。</summary>
    public const string MonitorCheck = "自分が話した声を自分でも聞く。音量の確認用。";

    // ---- 放送装置のポップ ----

    /// <summary>モニターのチェック（ポップ。幅が狭いので短く）。</summary>
    public const string MonitorCheckShort = "モニター";

    // ---- キー設定（OnboardKeyDialog） ----

    public const string KeyDialogNote =
        "押すたびに入・切します。ゲームを前面にしたままでも効きます。" +
        "ゲームで使っているキーと重ならないものを選んでください。キーとボタンを1つずつ登録できます。";
}
