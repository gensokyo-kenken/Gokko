using Concentus;
using Concentus.Enums;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 列車無線の音声の圧縮と展開（Opus。Concentus を使う。BSD 系のライセンス）。
/// サーバーは中身を見ずに中継するだけなので、圧縮と展開はクライアントだけで行う。
/// </summary>
internal static class RadioCodec
{
    public static RadioEncoder CreateEncoder() => new();
}

/// <summary>マイク1つ分の圧縮（40ms ずつ。続けて使うので、マイクを開くたびに作る）。</summary>
internal sealed class RadioEncoder
{
    private readonly IOpusEncoder _encoder;
    private readonly byte[] _out = new byte[TrainRadioFormat.MaxPacketBytes];

    public RadioEncoder()
    {
        _encoder = OpusCodecFactory.CreateEncoder(TrainRadioFormat.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = TrainRadioFormat.Bitrate;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.UseVBR = true;
        _encoder.Complexity = 5;
    }

    /// <summary>PCM16 の 40ms 分（640 サンプル）を圧縮する。失敗したら null。</summary>
    public byte[]? Encode(short[] samples)
    {
        try
        {
            var size = _encoder.Encode(samples, TrainRadioFormat.FrameSamples, _out, _out.Length);
            return size > 0 ? _out.AsSpan(0, size).ToArray() : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>話している人1人分の展開（前の音を覚えているので、人ごとに1つ）。</summary>
internal sealed class RadioDecoder
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(TrainRadioFormat.SampleRate, 1);
    private readonly short[] _samples = new short[TrainRadioFormat.FrameSamples * 3]; // 120ms まで受けられる

    /// <summary>Opus のパケット1つを PCM16（リトルエンディアン）にする。壊れていたら null。</summary>
    public byte[]? Decode(byte[] opus)
    {
        try
        {
            var n = _decoder.Decode(opus, _samples, _samples.Length, false);
            if (n <= 0) return null;
            var pcm = new byte[n * 2];
            Buffer.BlockCopy(_samples, 0, pcm, 0, pcm.Length);
            return pcm;
        }
        catch
        {
            return null;
        }
    }
}
