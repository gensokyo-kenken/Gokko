using System.Threading.Channels;
using Gokko.Common;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Gokko.Client;

/// <summary>
/// 列車無線の音。自分が通話の当事者の間だけマイクを開いて送り、聞こえる通話の音声を、話している人ごとに流す。
/// ・マイク: エコー除去（選んだとき）→ 感度 → ノイズゲート（小さい音は送らない）
/// ・受話: 人ごとの音量 × 受話音量。無線風の音（選んだとき）。防護無線の受報中は小さくする
/// （共同制作者の「列車無線」のパーツをもとに、音の設定を足したもの）
/// </summary>
public sealed class TrainRadioAudio : IDisposable
{
    private const int Rate = TrainRadioFormat.SampleRate;
    private static readonly TimeSpan GateHold = TimeSpan.FromMilliseconds(400);

    /// <summary>マイクの感度（ブースト）の上限（10倍 = +20dB）。</summary>
    public const float MaxMicGain = 10f;

    private const float AgcTarget = 0.4f;
    private const float AgcMaxBoost = 8f;
    private float _agc = 1f;

    private readonly ServerLink _server;
    private readonly RadioSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, RadioVoice> _voices = new(); // 話した人の接続ID → 声
    private readonly RingTone _ring = new();
    private readonly ChimeTone _chime = new();
    private long _chimeCallId;
    private MixingSampleProvider? _mixer;
    private WaveOutEvent? _output;

    private IRadioMicrophone? _mic;
    private Channel<byte[]>? _frames;
    private CancellationTokenSource? _sendCancel;
    private volatile bool _micWanted;
    private volatile bool _micTest;
    private long _micRetryTick;
    private long _outputRetryTick;
    private long _gateOpenUntil;

    private volatile TrainRadioState? _state;
    private volatile bool _online;
    private volatile float _duck = 1f;
    private volatile float _micLevel;
    private volatile bool _transmitting;
    private volatile string _error = "";
    private volatile string _micKind = "";
    private volatile bool _disposed;

    /// <summary>直近のエラー（なければ空）。</summary>
    public string Error => _error;

    /// <summary>マイクの大きさ（感度をかけたあと。0〜1）。</summary>
    public float MicLevel => _micLevel;

    /// <summary>受話の大きさ（0〜1）。</summary>
    public float ReceiveLevel { get; private set; }

    /// <summary>マイクを開いているか。</summary>
    public bool MicrophoneOpen => _mic != null;

    /// <summary>いま送っているか（ノイズゲートが開いている）。</summary>
    public bool Transmitting => _transmitting;

    /// <summary>マイクの種類（「エコー除去あり」など）。</summary>
    public string MicKind => _micKind;

    /// <summary>音の設定の画面: 通話していなくてもマイクを開いて、大きさを見る（送らない）。</summary>
    public bool MicTest
    {
        get => _micTest;
        set => _micTest = value;
    }

    public TrainRadioAudio(ServerLink server, RadioSettings settings)
    {
        _server = server;
        _settings = settings;
        _server.RadioAudioReceived += Receive;
    }

    /// <summary>無線機の画面から 50ms ごとに呼ぶ。状態に合わせてマイク・呼出音・声をそろえる。</summary>
    public void Synchronize(TrainRadioState? state, bool online, bool protectionAlarm)
    {
        if (_disposed) return;
        _state = online ? state : null;
        _online = online;
        _duck = protectionAlarm ? .18f : 1f;
        _micLevel *= .8f;

        EnsureOutput();
        var volume = Math.Clamp(_settings.Volume, 0, 1);

        // 声: 聞こえる通話の話し手だけ残す。人ごとの音量・無線風をかけ直す
        var speakers = new Dictionary<string, TrainRadioSpeaker>();
        var mine = new HashSet<string>(); // 自分の通話（自分が話している通話・一斉通告）の話し手
        foreach (var call in _state?.Audible ?? new())
        {
            var own = call.Id == _state?.MyCall?.Id; // 自分が話者として入っている通話（一斉通告に応答中も）
            foreach (var s in Speakers(call))
            {
                speakers[s.Id] = s;
                if (own) mine.Add(s.Id);
            }
        }

        _mine = mine;

        float level = 0;
        lock (_gate)
        {
            foreach (var (id, voice) in _voices.ToList())
            {
                if (!speakers.TryGetValue(id, out var who) && Environment.TickCount64 - voice.LastPacketTick > 1000)
                {
                    _mixer?.RemoveMixerInput(voice);
                    _voices.Remove(id);
                    continue;
                }

                voice.Gain = VoiceGain(id, who?.Key ?? "");
                voice.Effect = _settings.RadioEffect;
                voice.Strength = _settings.EffectStrength;
                level = Math.Max(level, voice.TakePeak());
            }
        }

        ReceiveLevel = Math.Max(level, ReceiveLevel * .8f);

        // 名前を覚える（人ごとの音量の画面に出す）
        foreach (var s in speakers.Values)
            if (s.Key.Length > 0 && s.Id != _state?.SelfId)
            {
                _settings.SpeakerNames[s.Key] = s.Name.Length > 0 ? s.Name : s.Label;
                _settings.SpeakerLabels[s.Key] = s.Label;
            }

        // 一斉通告の開始チャイム: 新しい一斉通告が聞こえ始めたら、全員で1回鳴らす（流す司令員にも、途中から参加した人にも）
        if (_state?.Audible.FirstOrDefault(c => c.IsBroadcast) is { } broadcast && broadcast.Id != _chimeCallId)
        {
            _chimeCallId = broadcast.Id;
            _chime.Start();
        }

        _chime.Gain = volume * _duck; // 防護受報中は小さく（防護の警報を優先）
        var myBroadcast = _state?.MyCall is { IsBroadcast: true };

        // 呼出音: 司令員に、応答待ちの呼出があるとき（通話中は小さく。一斉通告中は鳴らさない）
        // 運転士・車掌には、司令員からの個別呼出の着信音
        var ringing = online && ((_state is { IsDispatcher: true, Requests.Count: > 0 } && !myBroadcast) || _state?.Incoming != null);
        _ring.Gain = ringing ? (_state!.MyCall != null ? .18f : .65f) * volume * _duck : 0f;

        // マイク: 自分が通話の当事者のとき（一斉通告なら司令員だけ。チャイムを待たずにすぐ開く）。音の設定のテスト中も開く
        _micWanted = online && _state?.MyCall != null && !_state.PhoneLinked; // スマホを受話器にしているあいだは開かない
        var open = _micWanted || _micTest;
        if (open && _mic == null && Environment.TickCount64 >= _micRetryTick) StartMicrophone();
        if (!open && _mic != null) StopMicrophone();
        if (!_micWanted) _transmitting = false;
    }

    private static IEnumerable<TrainRadioSpeaker> Speakers(TrainRadioCall call)
    {
        yield return call.Dispatcher;
        if (call.Caller != null) yield return call.Caller;
        foreach (var r in call.Responders) yield return r; // 一斉通告に応答した人
    }

    private volatile HashSet<string> _mine = new();

    /// <summary>声の大きさ: 話者として入っている通話なら受話音量、聞いているだけなら ListenVolume。× 人ごとの音量。防護受報中は小さく。</summary>
    private float VoiceGain(string senderId, string key) =>
        Math.Clamp(_mine.Contains(senderId) ? _settings.Volume : _settings.ListenVolume, 0, 1) * _duck * SpeakerVolume(key);

    /// <summary>人ごとの音量（社員番号ごと。なければ 1）。</summary>
    public float SpeakerVolume(string key) =>
        key.Length > 0 && _settings.SpeakerVolumes.TryGetValue(key, out var v) ? Math.Clamp(v, 0, 2) : 1f;

    /// <summary>エコー除去の入・切を変えたとき: マイクを開き直す。</summary>
    public void ReopenMicrophone()
    {
        StopMicrophone();
        _micRetryTick = 0;
    }

    private void EnsureOutput()
    {
        if (_output != null || Environment.TickCount64 < _outputRetryTick) return;
        try
        {
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)) { ReadFully = true };
            _mixer.AddMixerInput(_ring);
            _mixer.AddMixerInput(_chime);
            _output = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 120, NumberOfBuffers = 3 };
            _output.Init(_mixer);
            _output.Play();
        }
        catch (Exception ex)
        {
            _error = "受話の音を出せません: " + ex.Message;
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
            _micKind = mic.Kind;
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
    /// マイクの 40ms 分: 感度（ブースト）をかけ、ノイズゲートを通ったものだけ送る。
    /// 自動で音量をそろえる（AGC）が入なら、送る声を目安の大きさまで持ち上げる（大きすぎれば下げる）。最後に、割れないように頭を丸め、Opus で圧縮する。
    /// </summary>
    private void OnFrame(byte[] pcm, RadioEncoder encoder, Channel<byte[]> frames)
    {
        var gain = Math.Clamp(_settings.MicGain, 0.25f, MaxMicGain);
        var n = pcm.Length / 2;
        var x = new float[n];
        var peak = 0f;
        for (var i = 0; i < n; i++)
        {
            x[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f * gain;
            peak = Math.Max(peak, Math.Abs(x[i]));
        }

        _micLevel = Math.Max(_micLevel, Math.Min(peak, 1f));
        var now = Environment.TickCount64;
        if (peak >= _settings.Gate) _gateOpenUntil = now + (long)GateHold.TotalMilliseconds;
        var open = now < _gateOpenUntil;
        _transmitting = open && _micWanted;
        if (!_transmitting) return;

        if (_settings.AutoGain)
        {
            // 声の山が目安（0.4）になるように。上げるのはゆっくり、下げるのは速く（急に大声を出しても割れにくい）
            var desired = Math.Clamp(AgcTarget / Math.Max(peak, 0.002f), 0.5f, AgcMaxBoost);
            _agc += (desired - _agc) * (desired < _agc ? 0.5f : 0.08f);
        }
        else _agc = 1f;

        var samples = new short[n];
        for (var i = 0; i < n; i++)
            samples[i] = (short)Math.Clamp(SoftLimit(x[i] * _agc) * 32767f, short.MinValue, short.MaxValue);

        var packet = encoder.Encode(samples);
        if (packet != null) frames.Writer.TryWrite(packet);
    }

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
                var call = _state?.MyCall;
                if (call == null || !_online || !_server.HasFreshState) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                await _server.SendRadioAudioAsync(new TrainRadioPacket { SessionId = call.Id, Opus = opus }, timeout.Token);
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

    private void Receive(TrainRadioPacket packet)
    {
        if (_disposed || !_online) return;
        var state = _state;
        if (state == null || packet.Opus is not { Length: > 0 and <= TrainRadioFormat.MaxPacketBytes } || packet.SenderId == state.SelfId) return;
        var call = state.Audible.FirstOrDefault(c => c.Id == packet.SessionId);
        var who = packet.SenderId == call?.Dispatcher.Id ? call.Dispatcher
            : packet.SenderId == call?.Caller?.Id ? call.Caller
            : call?.Responders.FirstOrDefault(r => r.Id == packet.SenderId);
        if (who == null) return;

        lock (_gate)
        {
            if (_mixer == null) return;
            if (!_voices.TryGetValue(packet.SenderId, out var voice))
            {
                voice = new RadioVoice
                {
                    Gain = VoiceGain(packet.SenderId, who.Key),
                    Effect = _settings.RadioEffect, Strength = _settings.EffectStrength
                };
                _voices[packet.SenderId] = voice;
                _mixer.AddMixerInput(voice);
            }

            voice.AddOpus(packet.Opus);
        }
    }

    /// <summary>切断したとき: マイクと受話を止める。</summary>
    public void Suspend()
    {
        Synchronize(null, false, false);
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
        _server.RadioAudioReceived -= Receive;
        StopMicrophone();
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
    }
}
