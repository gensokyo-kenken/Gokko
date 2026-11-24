namespace Gokko.Common;

/// <summary>
/// 列車無線の音声の形式。マイクの音（16kHz・16bit・モノラル）を 40ms ずつ Opus で圧縮して送る（24kbps 前後）。
/// 圧縮と展開はクライアントだけで行い、サーバーは中身を見ずに中継する。
/// </summary>
public static class TrainRadioFormat
{
    public const int SampleRate = 16000;
    public const int FrameMilliseconds = 40;
    public const int FrameSamples = SampleRate * FrameMilliseconds / 1000; // 640
    public const int FrameBytes = FrameSamples * 2; // 展開した PCM16 の 40ms 分（1280 バイト）

    /// <summary>Opus の目標のビットレート（bps）。40ms で約120バイト。</summary>
    public const int Bitrate = 24000;

    /// <summary>1パケット（40ms）の Opus の大きさの上限。これより大きいものはサーバーが捨てる。</summary>
    public const int MaxPacketBytes = 400;

    /// <summary>1秒に送ってよいパケットの数（40ms ごとなら 25）。</summary>
    public const int PacketsPerSecond = 1000 / FrameMilliseconds;
}

/// <summary>司令員へ: 運転士・車掌からの呼出1件。</summary>
public sealed class TrainRadioRequest
{
    public long Id { get; set; }

    /// <summary>画面に出す名前（「1040列車（運転士）」など）。</summary>
    public string Label { get; set; } = "";

    public override string ToString() => Label;
}

/// <summary>通話に出ている人。</summary>
public sealed class TrainRadioSpeaker
{
    /// <summary>音声の宛先を見分けるための番号（サーバーの接続 ID）。画面には出さない。</summary>
    public string Id { get; set; } = "";

    /// <summary>人ごとの音量を覚えるための鍵（社員番号）。</summary>
    public string Key { get; set; } = "";

    /// <summary>画面に出す名前（「1040列車（運転士）」「司令員 ID:12345678」など）。</summary>
    public string Label { get; set; } = "";

    /// <summary>ユーザー名（勤怠管理の名簿の名前）。人ごとの音量の一覧に出す。</summary>
    public string Name { get; set; } = "";
}

/// <summary>通話1件（司令員1人と、運転士か車掌1人）。一斉通告なら、司令員1人が全員に話す（Caller は null）。</summary>
public sealed class TrainRadioCall
{
    public long Id { get; set; }

    /// <summary>個別通話（当事者の2人だけに聞こえる）。false なら共通通話（全員に聞こえる）。</summary>
    public bool IsPrivate { get; set; }

    public TrainRadioSpeaker Dispatcher { get; set; } = new();
    /// <summary>呼び出した運転士・車掌。一斉通告なら null。</summary>
    public TrainRadioSpeaker? Caller { get; set; }

    /// <summary>一斉通告に応答して話している運転士・車掌（何人でも。声は全員に聞こえる）。</summary>
    public List<TrainRadioSpeaker> Responders { get; set; } = new();

    /// <summary>一斉通告（司令員だけが話し、全員に聞こえる）。</summary>
    public bool IsBroadcast => Caller == null;

    /// <summary>個別通話に切り替えている間も含め、一斉通告として開始した通話。</summary>
    public bool BroadcastSession { get; set; }
}

/// <summary>列車無線の状態（その人から見たもの）。</summary>
public sealed class TrainRadioState
{
    /// <summary>新しい順に並べるための番号。古いものが後から届いたら捨てる。</summary>
    public long Revision { get; set; }

    /// <summary>自分の接続 ID（届いた音声が自分のものか見分ける）。</summary>
    public string SelfId { get; set; } = "";

    /// <summary>自分が司令員として参加しているか。</summary>
    public bool IsDispatcher { get; set; }

    /// <summary>参加している司令員の人数。0 なら呼び出せない。</summary>
    public int DispatcherCount { get; set; }

    /// <summary>自分が当事者の通話（マイクを開いてよい）。なければ null。</summary>
    public TrainRadioCall? MyCall { get; set; }

    /// <summary>自分に聞こえる通話（自分の通話と、ほかの人の共通通話）。</summary>
    public List<TrainRadioCall> Audible { get; set; } = new();

    /// <summary>自分が司令を呼び出し中か。</summary>
    public bool OwnRequestPending { get; set; }

    /// <summary>
    /// スマホを受話器として結んでいる（同じ社員番号でスマホの列車無線の画面を開いている）。
    /// このあいだ、音声はスマホに届き、PC のマイクからは送らない（サーバーも捨てる）。
    /// </summary>
    public bool PhoneLinked { get; set; }

    /// <summary>司令員へ: 応答を待っている呼出（古い順）。</summary>
    public List<TrainRadioRequest> Requests { get; set; } = new();

    /// <summary>運転士・車掌へ: 司令員からの個別呼出（応答を待っている）。なければ null。</summary>
    public TrainRadioOffer? Incoming { get; set; }

    /// <summary>司令員へ: 自分が出している個別呼出。なければ null。</summary>
    public TrainRadioOffer? Outgoing { get; set; }

    /// <summary>司令員へ: 個別に呼び出せる相手。</summary>
    public List<TrainRadioTarget> Targets { get; set; } = new();
}

/// <summary>列車無線の操作の結果。</summary>
public sealed class TrainRadioResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>音声 40ms 分（Opus で圧縮したもの）。</summary>
public sealed class TrainRadioPacket
{
    public long SessionId { get; set; }

    /// <summary>話した人の接続 ID。サーバーが上書きする（なりすましを許さない）。</summary>
    public string SenderId { get; set; } = "";

    /// <summary>Opus のパケット1つ（40ms 分）。</summary>
    public byte[] Opus { get; set; } = [];
}

/// <summary>司令員から列車を指定した個別呼出（応答を待っている間）。</summary>
public sealed class TrainRadioOffer
{
    public long Id { get; set; }

    /// <summary>相手の名前（呼ばれた側には「司令員 ID:…」、呼んだ司令員には「1040列車（運転士）」）。</summary>
    public string Label { get; set; } = "";
}

/// <summary>司令員へ: 個別に呼び出せる相手（運転士・車掌）。</summary>
public sealed class TrainRadioTarget
{
    /// <summary>接続 ID（画面には出さない）。</summary>
    public string Id { get; set; } = "";

    /// <summary>「1040列車（運転士）　山田」など。</summary>
    public string Label { get; set; } = "";

    public override string ToString() => Label;
}

/// <summary>司令員へ: 運転士の F5（防護無線）の発報1件。</summary>
public sealed class ProtectionSource
{
    /// <summary>「1040列車」など。</summary>
    public string Train { get; set; } = "";

    /// <summary>発報した場所（「水越〜藤江」など。発報した時点のまま）。</summary>
    public string Position { get; set; } = "";

    public override string ToString() => $"{Train} / {Position}";
}

/// <summary>スマホの列車無線の画面へ: 結んでいる相手と、その人から見た列車無線の状態。</summary>
public sealed class PhoneRadioStatus
{
    /// <summary>PC のクライアントの参加と結べているか。</summary>
    public bool Linked { get; set; }

    /// <summary>別のスマホで同じ社員番号の受話器が開かれた（この画面はもう使わない）。</summary>
    public bool Replaced { get; set; }

    /// <summary>結べていないときのわけ（「参加していません」など）。</summary>
    public string Message { get; set; } = "";

    /// <summary>結んでいる参加の名前（「1040列車（運転士）」など）。</summary>
    public string Label { get; set; } = "";

    /// <summary>結んでいる人の列車無線の状態（Linked のときだけ）。</summary>
    public TrainRadioState? Radio { get; set; }
}

/// <summary>スマホの列車無線の画面の参加の結果。</summary>
public sealed class PhoneJoinResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string CrewId { get; set; } = "";
    public string Name { get; set; } = "";
}
