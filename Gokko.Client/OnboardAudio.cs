using System.Threading.Channels;
using Gokko.Common;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Gokko.Client;

/// <summary>
/// 列車ごとの放送装置の音（列車無線・内線電話とは別に動き、音は重なって聞こえる）。
/// ・マイク: 自分が放送を流しているか、インターホンを入にしているあいだだけ開く。声を出すと自動で送る（ノイズゲート。列車無線と同じ設定）。
///   放送を流しているあいだは、声は放送として送る（インターホンには送らない）
/// ・受話: 車内放送は車内のスピーカー、車外放送はホームに響く拡声器、インターホンは電話のような音にして流す。
///   音量は放送とインターホンで別（列車無線の受話音量とも別）
/// </summary>
public sealed class OnboardAudio : IDisposable
{
    private const int Rate = TrainRadioFormat.SampleRate;
    private static readonly TimeSpan GateHold = TimeSpan.FromMilliseconds(400);

    private readonly ServerLink _server;
    private readonly RadioSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<(string Sender, OnboardChannel Channel), OnboardVoice> _voices = new();
    private MixingSampleProvider? _mixer;
    private WaveOutEvent? _output;
    private IRadioMicrophone? _mic;
    private Channel<byte[]>? _frames;
    private CancellationTokenSource? _sendCancel;
    private long _micRetryTick, _outputRetryTick, _gateOpenUntil;
    private volatile OnboardState? _state;
    private volatile bool _online, _disposed, _transmitting;
    private volatile OnboardChannel _sendChannel;
    private volatile float _micLevel;
    private volatile string _error = "";

    public string Error => _error;

    /// <summary>いま声を送っているか（ノイズゲートが開いている）。</summary>
    public bool Transmitting => _transmitting;

    /// <summary>送っている先（放送・インターホン。マイクを開いていなければ None）。</summary>
    public OnboardChannel SendChannel => _sendChannel;

    /// <summary>マイクの大きさ（0〜1。送る音量をかけたあと）。</summary>
    public float MicLevel => _micLevel;

    /// <summary>乗っている列車の形式（ゲームの CarModel）。放送の音（帯域・割れ・音圧）を形式ごとに変える。</summary>
    public string CarModel
    {
        get => _carModel;
        set => _carModel = value ?? "";
    }

    private volatile string _carModel = "";

    /// <summary>今の形式に合わせた放送の音。</summary>
    public SpeakerProfile Profile => SpeakerProfile.Of(_carModel);

    /// <summary>
    /// 聞く人が乗っている車両の乗車率（ゲームの occupancyRate。分からなければ負）。混んでいるほど、車内放送の響きを短く・こもらせる（人が音を吸う）。
    /// ゲームの値が 0〜1 か 0〜100（%）か分からないので、2 を超えたら % と見なす。1（定員）で吸い込みがいちばん強い。
    /// </summary>
    public float Occupancy
    {
        get => _occupancy;
        set => _occupancy = value;
    }

    private volatile float _occupancy = -1;

    /// <summary>乗車率から決めた、車内の音の吸い込み（0 空いている 〜 1 満員）。</summary>
    public float Absorb
    {
        get
        {
            var o = _occupancy;
            if (o < 0 || float.IsNaN(o)) return 0.3f; // 分からなければ、ほどほど
            if (o > 2) o /= 100f;
            return Math.Clamp(o, 0, 1);
        }
    }

    /// <summary>いま聞こえている音（放送・インターホン）の話し手。</summary>
    public IReadOnlyList<(string Sender, OnboardChannel Channel)> Speaking
    {
        get
        {
            lock (_gate)
                return _voices.Where(kv => kv.Key.Sender != MonitorId && Environment.TickCount64 - kv.Value.LastPacketTick < 500).Select(kv => kv.Key).ToList();
        }
    }

    public OnboardAudio(ServerLink server, RadioSettings settings)
    {
        _server = server;
        _settings = settings;
        _server.OnboardAudioReceived += Receive;
    }

    /// <summary>画面から 100ms ごとに呼ぶ。状態に合わせてマイクと声をそろえる。</summary>
    public void Synchronize(OnboardState? state, bool online)
    {
        if (_disposed) return;
        _state = online ? state : null;
        _online = online;
        _micLevel *= .7f;
        EnsureOutput();

        var broadcastGain = Math.Clamp(_settings.BroadcastVolume, 0, 2);
        var intercomGain = Math.Clamp(_settings.IntercomVolume, 0, 2);
        var hearsIntercom = _state?.HearsIntercom == true;
        var absorb = Absorb;
        lock (_gate)
            foreach (var (key, voice) in _voices.ToList())
            {
                var stale = Environment.TickCount64 - voice.LastPacketTick > 4000; // 残響が消えるまで待ってから外す
                if (stale || _state == null || (key.Channel == OnboardChannel.Intercom && !hearsIntercom))
                {
                    _mixer?.RemoveMixerInput(voice);
                    _voices.Remove(key);
                    continue;
                }

                voice.Gain = (key.Channel == OnboardChannel.Intercom ? intercomGain : broadcastGain) * SpeakerVolume(key.Sender);
                voice.Absorb = absorb;
            }

        // マイク: 自分が放送を流しているか、インターホンを入にしているとき（話せるのは運転士・車掌だけ）
        var s = _state;
        _sendChannel = s is not { CanTalk: true } || s.PhoneMic ? OnboardChannel.None // スマホのマイクで話している間は、PC のマイクは閉じる
            : s.BroadcasterId == s.SelfId && s.Broadcast != OnboardChannel.None ? s.Broadcast
            : s.Intercom ? OnboardChannel.Intercom
            : OnboardChannel.None;
        var open = online && _sendChannel != OnboardChannel.None;
        if (open && _mic == null && Environment.TickCount64 >= _micRetryTick) StartMicrophone();
        if (!open && _mic != null) StopMicrophone();
        if (!open) _transmitting = false;
    }

    private float SpeakerVolume(string sender)
    {
        var key = _state?.Members.FirstOrDefault(m => m.Id == sender)?.Key ?? "";
        return key.Length > 0 && _settings.SpeakerVolumes.TryGetValue(key, out var v) ? Math.Clamp(v, 0, 2) : 1f;
    }

    private void EnsureOutput()
    {
        if (_output != null || Environment.TickCount64 < _outputRetryTick) return;
        try
        {
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)) { ReadFully = true };
            _output = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 120, NumberOfBuffers = 3 };
            _output.Init(_mixer);
            _output.Play();
        }
        catch (Exception ex)
        {
            _error = "放送装置の音を出せません: " + ex.Message;
            _outputRetryTick = Environment.TickCount64 + 5000;
            try { _output?.Dispose(); } catch { }
            _output = null;
            _mixer = null;
        }
    }

    // ---- マイク ----

    private void StartMicrophone()
    {
        try
        {
            var mic = RadioMicrophone.Open(_settings.EchoCancel, _settings.InputDeviceId ?? "", out var note);
            if (note.Length > 0) _error = note;
            else if (_error.StartsWith("マイク")) _error = "";
            var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
            var cancel = new CancellationTokenSource();
            var encoder = RadioCodec.CreateEncoder();
            mic.Frame += pcm => OnFrame(pcm, encoder, frames);
            mic.Failed += message =>
            {
                _error = message;
                _micRetryTick = Environment.TickCount64 + 3000;
                StopMicrophone();
            };
            _frames = frames;
            _sendCancel = cancel;
            _mic = mic;
            _ = SendLoopAsync(frames, cancel.Token);
        }
        catch (Exception ex)
        {
            _error = "マイクを開けません: " + ex.Message + "（Windows の設定で、マイクの使用を許可してください）";
            _micRetryTick = Environment.TickCount64 + 3000;
        }
    }

    private void StopMicrophone()
    {
        var mic = _mic;
        _mic = null;
        _transmitting = false;
        _sendCancel?.Cancel();
        _frames?.Writer.TryComplete();
        _sendCancel = null;
        _frames = null;
        if (mic == null) return;
        try { mic.Dispose(); } catch { }
        _micLevel = 0;
    }

    /// <summary>
    /// マイクの 40ms 分: 感度をかけ、ノイズゲートを通ったものだけ送る（声を出すと自動で送る。GateHold だけ余韻を残す）。
    /// 送る音量（放送装置だけの設定）は、ゲートを見たあとにかける（音量を下げても、声を拾わなくならないように）。割れないように頭を丸める。
    /// </summary>
    private void OnFrame(byte[] pcm, RadioEncoder encoder, Channel<byte[]> frames)
    {
        var gain = Math.Clamp(_settings.MicGain, 0.25f, TrainRadioAudio.MaxMicGain);
        var send = Math.Clamp(_settings.OnboardSendGain, 0f, 2f);
        var n = pcm.Length / 2;
        var x = new float[n];
        var peak = 0f;
        for (var i = 0; i < n; i++)
        {
            x[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f * gain;
            peak = Math.Max(peak, Math.Abs(x[i]));
        }

        // 自動で音量をそろえる（列車無線の［音の設定］と同じ設定。上げるのはゆっくり、下げるのは速く）
        if (_settings.AutoGain && peak >= _settings.Gate)
        {
            var desired = Math.Clamp(0.4f / Math.Max(peak, 0.002f), 0.5f, 8f);
            _agc += (desired - _agc) * (desired < _agc ? 0.5f : 0.08f);
        }
        else if (!_settings.AutoGain) _agc = 1f;

        var samples = new short[n];
        var sentPeak = 0f;
        for (var i = 0; i < n; i++)
        {
            var y = SoftLimit(x[i] * _agc * send);
            sentPeak = Math.Max(sentPeak, Math.Abs(y));
            samples[i] = (short)Math.Clamp(y * 32767f, short.MinValue, short.MaxValue);
        }

        _micLevel = Math.Max(_micLevel, Math.Min(sentPeak, 1f));
        var now = Environment.TickCount64;
        if (peak >= _settings.Gate) _gateOpenUntil = now + (long)GateHold.TotalMilliseconds;
        _transmitting = now < _gateOpenUntil && _sendChannel != OnboardChannel.None;
        if (!_transmitting) return;
        var packet = encoder.Encode(samples);
        if (packet == null) return;
        frames.Writer.TryWrite(packet);
        // モニター: 送ったものと同じ（圧縮した）音を、聞く人と同じスピーカー越しの音にして自分でも流す
        if (_settings.OnboardMonitor) AddVoice((MonitorId, _sendChannel), packet);
    }

    private float _agc = 1f;

    /// <summary>モニター（自分の声）の話し手の印。</summary>
    private const string MonitorId = "(self)";

    /// <summary>0.8 を超えたところだけ、なだらかに抑える（音割れを防ぐ）。</summary>
    private static float SoftLimit(float v)
    {
        var a = Math.Abs(v);
        if (a <= 0.8f) return v;
        return Math.Sign(v) * (0.8f + 0.2f * (float)Math.Tanh((a - 0.8f) / 0.2f));
    }

    private async Task SendLoopAsync(Channel<byte[]> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var opus in frames.Reader.ReadAllAsync(ct))
            {
                var channel = _sendChannel;
                if (channel == OnboardChannel.None || !_online || !_server.HasFreshState) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                await _server.SendOnboardAudioAsync(new OnboardPacket { Channel = channel, Opus = opus }, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _error = "音声を送れません: " + ex.Message;
        }
    }

    // ---- 受話 ----

    private void Receive(OnboardPacket packet)
    {
        if (_disposed || !_online) return;
        var state = _state;
        if (state == null || packet.Opus is not { Length: > 0 and <= TrainRadioFormat.MaxPacketBytes } || packet.SenderId == state.SelfId) return;
        if (packet.Channel is not (OnboardChannel.Inside or OnboardChannel.Outside or OnboardChannel.Intercom)) return;
        if (packet.Channel == OnboardChannel.Intercom && !state.HearsIntercom) return; // 乗客には聞かせない（サーバーも送らない）
        if (state.Members.All(m => m.Id != packet.SenderId)) return;

        AddVoice((packet.SenderId, packet.Channel), packet.Opus);
    }

    private void AddVoice((string Sender, OnboardChannel Channel) key, byte[] opus)
    {
        if (key.Channel == OnboardChannel.None) return;
        lock (_gate)
        {
            if (_mixer == null) return;
            if (!_voices.TryGetValue(key, out var voice))
            {
                voice = new OnboardVoice(key.Channel, SpeakerProfile.Of(_carModel))
                {
                    Gain = Math.Clamp(key.Channel == OnboardChannel.Intercom ? _settings.IntercomVolume : _settings.BroadcastVolume, 0, 2) * SpeakerVolume(key.Sender),
                    Absorb = Absorb
                };
                _voices[key] = voice;
                _mixer.AddMixerInput(voice);
            }

            voice.AddOpus(opus);
        }
    }

    /// <summary>切断したとき: マイクと受話を止める。</summary>
    public void Suspend()
    {
        Synchronize(null, false);
        lock (_gate)
        {
            foreach (var v in _voices.Values) _mixer?.RemoveMixerInput(v);
            _voices.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.OnboardAudioReceived -= Receive;
        StopMicrophone();
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
    }
}

/// <summary>
/// 形式ごとの放送の音。聞く人が乗っている列車の形式（ゲームの CarModel）で決める（同じ列車に乗っているので、みんな同じ形式）。
/// 形式の名前の数字で分ける: 3000番台以下は古いスピーカー（狭い帯域・よく割れる）、4000番台は標準、5000番台は新しめ（広い帯域・割れにくく音圧が高い）、
/// 5桁（50000形など）は新型。分からなければ標準。
/// </summary>
public sealed record SpeakerProfile(string Name, float InsideLow, float InsideHigh, float InsideDrive, float OutsideLow, float OutsideHigh, float OutsideDrive, float Level)
{
    public static readonly SpeakerProfile Old = new("古い車両", 350, 3000, 2.4f, 450, 2800, 3.0f, 1.0f);
    public static readonly SpeakerProfile Standard = new("標準", 200, 5000, 0f, 400, 3200, 2.2f, 1f);
    public static readonly SpeakerProfile Newer = new("新しめの車両", 150, 6500, 0f, 300, 4500, 1.5f, 1.1f);
    public static readonly SpeakerProfile Newest = new("新型車両", 120, 7000, 0f, 250, 5000, 1.2f, 1.15f);

    public static SpeakerProfile Of(string carModel)
    {
        var digits = new string((carModel ?? "").SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0) return Standard;
        if (digits.Length >= 5) return Newest;
        return digits[0] switch { <= '3' => Old, '4' => Standard, _ => Newer };
    }
}

/// <summary>
/// 放送装置の話し手1人・1種類分の受話の音。届いた音声をためて流し、種類ごとの音をかける。
/// ・車内放送: 車内のスピーカー（帯域・割れ・音圧は形式ごと。車内の短い響き）
/// ・車外放送: ホームに響く拡声器（帯域を絞って歪ませ、ホームの残響と、向かいの壁からの返り。形式ごとに帯域・割れを変える）
/// ・インターホン: 電話のような音（300〜3400Hz）
/// 声が途切れても、残響が消えるまで流し続ける。
/// </summary>
internal sealed class OnboardVoice : ISampleProvider
{
    private const int Rate = TrainRadioFormat.SampleRate;
    private static readonly WaveFormat Pcm = new(Rate, 16, 1);

    private readonly BufferedWaveProvider _buffer = new(Pcm)
    {
        BufferDuration = TimeSpan.FromMilliseconds(400), DiscardOnBufferOverflow = true, ReadFully = false
    };

    private readonly RadioDecoder _decoder = new();
    private readonly OnboardChannel _channel;
    private readonly Biquad[] _filters;
    private readonly Reverb? _reverb;
    private readonly float _drive, _level;
    private readonly byte[] _bytes = new byte[Rate * 2];
    private readonly byte[] _discard = new byte[Rate * 2];
    private bool _playing;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

    /// <summary>種類ごとの音量 × 人ごとの音量。</summary>
    public volatile float Gain = 1f;

    /// <summary>車内の音の吸い込み（乗車率から。0 空いている 〜 1 満員）。車内放送の響きだけに効く。</summary>
    public float Absorb
    {
        set
        {
            if (_channel == OnboardChannel.Inside && _reverb != null) _reverb.Absorb = value;
        }
    }

    public long LastPacketTick { get; private set; } = Environment.TickCount64;

    public OnboardVoice(OnboardChannel channel, SpeakerProfile profile)
    {
        _channel = channel;
        var p = profile;
        var oh = Math.Min(p.OutsideHigh, 7500); // 16kHz の音声の上限（8kHz）より下に
        var ih = Math.Min(p.InsideHigh, 7500);
        (_filters, _reverb, _drive, _level) = channel switch
        {
            OnboardChannel.Outside => (new[] { Biquad.HighPass(p.OutsideLow, Rate), Biquad.HighPass(p.OutsideLow, Rate), Biquad.LowPass(oh, Rate), Biquad.LowPass(oh, Rate) },
                new Reverb(Reverb.Platform), p.OutsideDrive, p.Level),
            OnboardChannel.Inside => (new[] { Biquad.HighPass(p.InsideLow, Rate), Biquad.LowPass(ih, Rate) }, new Reverb(Reverb.Car), p.InsideDrive, p.Level),
            _ => (new[] { Biquad.HighPass(300, Rate), Biquad.HighPass(300, Rate), Biquad.LowPass(3400, Rate), Biquad.LowPass(3400, Rate) }, (Reverb?)null, 1.6f, 1f)
        };
    }

    public void AddOpus(byte[] opus)
    {
        var pcm = _decoder.Decode(opus);
        if (pcm == null) return;
        LastPacketTick = Environment.TickCount64;
        _buffer.AddSamples(pcm, 0, pcm.Length);
        // 回線の遅れで 200ms を超えてたまったら、古い方から捨てて 120ms まで戻す
        const int max = Rate * 2 * 200 / 1000, keep = Rate * 2 * 120 / 1000;
        var excess = _buffer.BufferedBytes - keep;
        if (_buffer.BufferedBytes > max && excess > 0) _buffer.Read(_discard, 0, Math.Min(excess & ~1, _discard.Length));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // 話し始めは 80ms ためてから流す（回線のゆらぎで、すぐ途切れないように）
        if (!_playing && _buffer.BufferedBytes > 0
            && (_buffer.BufferedDuration >= TimeSpan.FromMilliseconds(80) || Environment.TickCount64 - LastPacketTick > 120))
            _playing = true;

        var bytes = Math.Min(count * 2, _bytes.Length);
        var got = _playing ? _buffer.Read(_bytes, 0, bytes) / 2 : 0;
        if (_playing && got < count) _playing = false;
        var gain = Gain;
        var drive = _drive;
        var norm = drive > 0 ? (float)Math.Tanh(drive) : 1f;
        for (var i = 0; i < count; i++)
        {
            var x = i < got ? BitConverter.ToInt16(_bytes, i * 2) / 32768f : 0f;
            foreach (var f in _filters) x = f.Process(x);
            if (drive > 0) x = (float)Math.Tanh(x * drive) / norm * 0.85f; // スピーカーの割れ（古い形式ほど強い）
            x *= _level; // 音圧（新しい形式ほど高い。頭は最後に丸める）
            if (_reverb != null) x = _reverb.Process(x);
            buffer[offset + i] = Math.Clamp(x * gain, -1f, 1f);
        }

        return count; // いつも満たして返す（ミキサーから外れないように）
    }
}

/// <summary>簡単な残響（Schroeder: 並列のくし形フィルター4つ＋直列のオールパス2つ。車外放送では、向かいの壁からの返りも足す）。</summary>
internal sealed class Reverb
{
    public sealed record Kind(int[] CombMs, float Feedback, float Damp, float Wet, float Dry, int EchoMs, float EchoGain);

    /// <summary>ホーム: ほどほどの響き（約0.8秒）と、0.18秒後の弱い返り。</summary>
    public static readonly Kind Platform = new([53, 61, 71, 79], 0.66f, 0.40f, 0.22f, 0.95f, 180, 0.12f);

    /// <summary>車内: 短い響き（空いているとき。混むほど <see cref="Absorb"/> で短く・こもらせる）。</summary>
    public static readonly Kind Car = new([23, 29, 31, 37], 0.56f, 0.40f, 0.17f, 1f, 0, 0f);

    /// <summary>音の吸い込み（0 そのまま 〜 1 いちばん強い）。響きの長さ・明るさ・量を下げる。</summary>
    public volatile float Absorb;

    private readonly Kind _kind;
    private readonly float[][] _combs;
    private readonly int[] _combPos;
    private readonly float[] _combLow;
    private readonly float[][] _allpass = [new float[Ms(5)], new float[Ms(1.7)]];
    private readonly int[] _apPos = new int[2];
    private readonly float[] _echo;
    private int _echoPos;

    private static int Ms(double ms) => Math.Max(1, (int)(TrainRadioFormat.SampleRate * ms / 1000));

    public Reverb(Kind kind)
    {
        _kind = kind;
        _combs = kind.CombMs.Select(ms => new float[Ms(ms)]).ToArray();
        _combPos = new int[_combs.Length];
        _combLow = new float[_combs.Length];
        _echo = new float[Ms(Math.Max(1, kind.EchoMs))];
    }

    public float Process(float x)
    {
        var absorb = Absorb;
        var feedback = _kind.Feedback * (1 - 0.45f * absorb); // 混むと響きが短くなる
        var damp = _kind.Damp + (0.85f - _kind.Damp) * absorb; // 混むと高い音から先に吸われる
        var wetGain = _kind.Wet * (1 - 0.7f * absorb);
        float wet = 0;
        for (var i = 0; i < _combs.Length; i++)
        {
            var buf = _combs[i];
            var y = buf[_combPos[i]];
            _combLow[i] = y * (1 - damp) + _combLow[i] * damp; // 高い音ほど早く消える
            buf[_combPos[i]] = x + _combLow[i] * feedback;
            if (++_combPos[i] >= buf.Length) _combPos[i] = 0;
            wet += y;
        }

        wet /= _combs.Length;
        for (var i = 0; i < _allpass.Length; i++)
        {
            var buf = _allpass[i];
            var b = buf[_apPos[i]];
            var y = -wet + b;
            buf[_apPos[i]] = wet + b * 0.5f;
            if (++_apPos[i] >= buf.Length) _apPos[i] = 0;
            wet = y;
        }

        var dry = x;
        if (_kind.EchoMs > 0)
        {
            var e = _echo[_echoPos];
            _echo[_echoPos] = x + e * 0.3f;
            if (++_echoPos >= _echo.Length) _echoPos = 0;
            dry += e * _kind.EchoGain;
        }

        return dry * _kind.Dry + wet * wetGain;
    }
}
