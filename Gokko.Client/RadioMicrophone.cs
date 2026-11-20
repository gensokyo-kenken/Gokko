using System.Runtime.InteropServices;
using Gokko.Common;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Gokko.Client;

/// <summary>列車無線のマイク。16kHz・16bit・モノラルの PCM を 40ms（1280バイト）ずつ届ける。</summary>
internal interface IRadioMicrophone : IDisposable
{
    /// <summary>40ms 分の音（録音のスレッドから呼ばれる）。</summary>
    event Action<byte[]>? Frame;

    /// <summary>録音が止まった（理由）。</summary>
    event Action<string>? Failed;

    /// <summary>画面に出す名前（「エコー除去あり」など）。</summary>
    string Kind { get; }
}

/// <summary>選べる入力デバイス（マイク）。</summary>
public sealed record RadioInputDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class RadioMicrophone
{
    /// <summary>使える入力デバイス（Windows に今つながっているマイク）。並びは Windows の一覧の順（エコー除去の番号と同じ）。</summary>
    public static List<RadioInputDevice> Devices()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new RadioInputDevice(d.ID, d.FriendlyName)).ToList();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// マイクを開く。deviceId が空なら Windows の既定のマイク。エコー除去を選んでいれば、Windows の通話用の音声処理（Voice Capture DSP）を使う。
    /// 使えなければ、ふつうの録音にして、その理由を note に入れる。
    /// </summary>
    public static IRadioMicrophone Open(bool echoCancel, string deviceId, out string note)
    {
        note = "";
        var devices = Devices();
        var index = deviceId.Length == 0 ? -1 : devices.FindIndex(d => d.Id == deviceId);
        if (deviceId.Length > 0 && index < 0)
        {
            note = "選んだマイクが見つからないので、Windows の既定のマイクを使います";
            deviceId = "";
        }

        if (echoCancel)
        {
            try
            {
                return new EchoCancelMicrophone(index);
            }
            catch (Exception ex)
            {
                note = "エコー除去を使えないので、ふつうに録音します（" + ex.Message + "）";
            }
        }

        return new PlainMicrophone(deviceId);
    }
}

/// <summary>40ms ずつに区切る。</summary>
internal sealed class FrameSplitter(Action<byte[]> emit)
{
    private readonly byte[] _pending = new byte[TrainRadioFormat.FrameBytes];
    private int _count;

    public void Add(byte[] data, int offset, int length)
    {
        while (length > 0)
        {
            var size = Math.Min(_pending.Length - _count, length);
            Buffer.BlockCopy(data, offset, _pending, _count, size);
            _count += size;
            offset += size;
            length -= size;
            if (_count < _pending.Length) continue;
            emit(_pending.ToArray());
            _count = 0;
        }
    }
}

/// <summary>ふつうの録音（WASAPI）。デバイスの形式（48kHz・ステレオなど）から、16kHz・モノラルに変える。</summary>
internal sealed class PlainMicrophone : IRadioMicrophone
{
    private readonly WasapiCapture _input;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _converted;
    private readonly FrameSplitter _splitter;
    private readonly float[] _samples = new float[TrainRadioFormat.SampleRate / 10];
    private readonly byte[] _bytes = new byte[TrainRadioFormat.SampleRate / 10 * 2];
    private volatile bool _disposed;

    public event Action<byte[]>? Frame;
    public event Action<string>? Failed;
    public string Kind => "ふつうの録音";

    public PlainMicrophone(string deviceId)
    {
        _splitter = new FrameSplitter(f => Frame?.Invoke(f));
        using (var e = new MMDeviceEnumerator())
        {
            var device = deviceId.Length > 0 ? e.GetDevice(deviceId) : e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _input = new WasapiCapture(device, false, 40);
        }

        var format = _input.WaveFormat;
        _buffer = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true, ReadFully = false };
        ISampleProvider s = _buffer.ToSampleProvider();
        if (format.Channels == 2) s = new StereoToMonoSampleProvider(s);
        else if (format.Channels > 2) s = new MultiplexingSampleProvider([s], 1); // 1チャンネル目だけ
        if (s.WaveFormat.SampleRate != TrainRadioFormat.SampleRate) s = new WdlResamplingSampleProvider(s, TrainRadioFormat.SampleRate);
        _converted = s;

        _input.DataAvailable += (_, ev) =>
        {
            if (_disposed) return;
            _buffer.AddSamples(ev.Buffer, 0, ev.BytesRecorded);
            int n;
            while ((n = _converted.Read(_samples, 0, _samples.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var v = (short)Math.Clamp(_samples[i] * 32767f, short.MinValue, short.MaxValue);
                    _bytes[i * 2] = (byte)v;
                    _bytes[i * 2 + 1] = (byte)(v >> 8);
                }

                _splitter.Add(_bytes, 0, n * 2);
            }
        };
        _input.RecordingStopped += (_, ev) =>
        {
            if (!_disposed && ev.Exception != null) Failed?.Invoke("マイクが止まりました: " + ev.Exception.Message);
        };
        try
        {
            _input.StartRecording();
        }
        catch
        {
            _input.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _input.StopRecording(); } catch { }
        try { _input.Dispose(); } catch { }
    }
}

/// <summary>
/// エコー除去つきの録音。Windows に入っている通話用の音声処理（Voice Capture DSP、CLSID_CWMAudioAEC）を、
/// 自分でマイクとスピーカーの音を拾う使い方（ソースモード）で動かす。スピーカーから出た受話の音を、マイクの音から差し引く。
/// 雑音の抑えも、既定で効く。
/// </summary>
internal sealed class EchoCancelMicrophone : IRadioMicrophone
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly FrameSplitter _splitter;
    private Exception? _startError;
    private volatile bool _stop;

    public event Action<byte[]>? Frame;
    public event Action<string>? Failed;
    public string Kind => "エコー除去あり";

    private readonly int _micIndex;

    /// <param name="micIndex">マイクの番号（Windows の一覧の順。-1 なら既定のマイク）。</param>
    public EchoCancelMicrophone(int micIndex)
    {
        _micIndex = micIndex;
        _splitter = new FrameSplitter(f => Frame?.Invoke(f));
        _thread = new Thread(Run) { IsBackground = true, Name = "列車無線のマイク（エコー除去）" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _stop = true;
            throw new InvalidOperationException("音声処理が始まりません");
        }

        if (_startError != null) throw _startError;
    }

    private void Run()
    {
        object? com = null;
        IntPtr bufferPtr = IntPtr.Zero;
        IntPtr format = IntPtr.Zero;
        try
        {
            var type = Type.GetTypeFromCLSID(Dsp.ClsidAec, true)!;
            com = Activator.CreateInstance(type)!;
            var dmo = (Dsp.IMediaObject)com;
            var props = (Dsp.IPropertyStore)com;

            // 単一チャンネルのエコー除去。選んだマイク（なければ既定）と、既定のスピーカーの音を使う
            Dsp.SetInt(props, Dsp.SystemMode, 0); // SINGLE_CHANNEL_AEC
            Dsp.SetInt(props, Dsp.DeviceIndexes, unchecked((int)(0xFFFF0000u | (uint)(_micIndex & 0xFFFF)))); // 上位: スピーカー、下位: マイク（-1 は既定）

            format = Marshal.AllocCoTaskMem(18);
            Marshal.WriteInt16(format, 0, 1);
            Marshal.WriteInt16(format, 2, 1);
            Marshal.WriteInt32(format, 4, TrainRadioFormat.SampleRate);
            Marshal.WriteInt32(format, 8, TrainRadioFormat.SampleRate * 2);
            Marshal.WriteInt16(format, 12, 2);
            Marshal.WriteInt16(format, 14, 16);
            Marshal.WriteInt16(format, 16, 0);
            var mt = new Dsp.DmoMediaType
            {
                MajorType = Dsp.MediaTypeAudio, SubType = Dsp.MediaSubtypePcm, FixedSizeSamples = 1, SampleSize = 2,
                FormatType = Dsp.FormatWaveFormatEx, FormatSize = 18, Format = format
            };
            Dsp.Check(dmo.SetOutputType(0, ref mt, 0), "出力の形式");
            Dsp.Check(dmo.AllocateStreamingResources(), "準備");

            var buffer = new DmoMediaBuffer(TrainRadioFormat.SampleRate * 2);
            bufferPtr = Marshal.GetComInterfaceForObject(buffer, typeof(IDmoMediaBuffer));
            _ready.Set();

            var chunk = new byte[TrainRadioFormat.SampleRate * 2];
            while (!_stop)
            {
                Dsp.OutputDataBuffer odb;
                do
                {
                    buffer.Length = 0;
                    odb = new Dsp.OutputDataBuffer { Buffer = bufferPtr };
                    var hr = dmo.ProcessOutput(0, 1, ref odb, out _);
                    if (hr < 0) throw new COMException("音声処理が止まりました", hr);
                    if (buffer.Length > 0)
                    {
                        var n = Math.Min(buffer.Length, chunk.Length);
                        Marshal.Copy(buffer.Data, chunk, 0, n);
                        _splitter.Add(chunk, 0, n);
                    }
                } while (!_stop && (odb.Status & Dsp.OutputIncomplete) != 0);

                Thread.Sleep(10);
            }

            try { dmo.FreeStreamingResources(); } catch { }
            buffer.Dispose();
        }
        catch (Exception ex)
        {
            if (!_ready.IsSet)
            {
                _startError = ex is COMException c ? new InvalidOperationException($"0x{c.HResult:X8}") : ex;
                _ready.Set();
            }
            else if (!_stop) Failed?.Invoke("マイクが止まりました: " + ex.Message);
        }
        finally
        {
            if (bufferPtr != IntPtr.Zero) Marshal.Release(bufferPtr);
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            if (com != null) try { Marshal.ReleaseComObject(com); } catch { }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _thread) _thread.Join(500);
    }

    /// <summary>Voice Capture DSP を使うための COM の定義（wmcodecdsp.h・mediaobj.h・propsys.h より）。</summary>
    private static class Dsp
    {
        public static readonly Guid ClsidAec = new("745057c7-f353-4f2d-a7ee-58434477730e");
        public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
        public static readonly Guid MediaSubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
        public static readonly Guid FormatWaveFormatEx = new("05589f81-c356-11ce-bf01-00aa0055595a");
        private static readonly Guid AecProps = new("6f52c567-0360-4bd2-9617-ccbf1421c939");
        public static readonly PropertyKey SystemMode = new() { FormatId = AecProps, PropertyId = 2 };
        public static readonly PropertyKey DeviceIndexes = new() { FormatId = AecProps, PropertyId = 4 };
        public const int OutputIncomplete = 0x01000000;

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new InvalidOperationException($"{what}（0x{hr:X8}）");
        }

        public static void SetInt(IPropertyStore props, PropertyKey key, int value)
        {
            var v = new PropVariant { VarType = 3, IntValue = value }; // VT_I4
            Check(props.SetValue(ref key, ref v), "設定");
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey
        {
            public Guid FormatId;
            public int PropertyId;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PropVariant
        {
            [FieldOffset(0)] public short VarType;
            [FieldOffset(8)] public int IntValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DmoMediaType
        {
            public Guid MajorType;
            public Guid SubType;
            public int FixedSizeSamples;
            public int TemporalCompression;
            public int SampleSize;
            public Guid FormatType;
            public IntPtr Unknown;
            public int FormatSize;
            public IntPtr Format;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OutputDataBuffer
        {
            public IntPtr Buffer;
            public int Status;
            public long TimeStamp;
            public long TimeLength;
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("d8ad0f58-5494-4102-97c5-ec798e59bcf4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMediaObject
        {
            [PreserveSig] int GetStreamCount(out int inputs, out int outputs);
            [PreserveSig] int GetInputStreamInfo(int index, out int flags);
            [PreserveSig] int GetOutputStreamInfo(int index, out int flags);
            [PreserveSig] int GetInputType(int index, int typeIndex, IntPtr type);
            [PreserveSig] int GetOutputType(int index, int typeIndex, IntPtr type);
            [PreserveSig] int SetInputType(int index, IntPtr type, int flags);
            [PreserveSig] int SetOutputType(int index, ref DmoMediaType type, int flags);
            [PreserveSig] int GetInputCurrentType(int index, IntPtr type);
            [PreserveSig] int GetOutputCurrentType(int index, IntPtr type);
            [PreserveSig] int GetInputSizeInfo(int index, out int size, out int lookahead, out int alignment);
            [PreserveSig] int GetOutputSizeInfo(int index, out int size, out int alignment);
            [PreserveSig] int GetInputMaxLatency(int index, out long latency);
            [PreserveSig] int SetInputMaxLatency(int index, long latency);
            [PreserveSig] int Flush();
            [PreserveSig] int Discontinuity(int index);
            [PreserveSig] int AllocateStreamingResources();
            [PreserveSig] int FreeStreamingResources();
            [PreserveSig] int GetInputStatus(int index, out int flags);
            [PreserveSig] int ProcessInput(int index, IntPtr buffer, int flags, long time, long length);
            [PreserveSig] int ProcessOutput(int flags, int count, ref OutputDataBuffer buffers, out int status);
            [PreserveSig] int Lock(int lockIt);
        }

    }
}

/// <summary>音声処理（DMO）が書き込む先の入れ物（mediaobj.h の IMediaBuffer）。COM に渡すので public にしておく。</summary>
[ComVisible(true), Guid("59eff8b9-938c-4a26-82f2-95cb84cdc837"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDmoMediaBuffer
{
    [PreserveSig] int SetLength(int length);
    [PreserveSig] int GetMaxLength(out int max);
    [PreserveSig] int GetBufferAndLength(IntPtr buffer, IntPtr length);
}

/// <summary>音声処理が書き込む先（ネイティブのメモリ）。</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DmoMediaBuffer : IDmoMediaBuffer, IDisposable
{
    private readonly int _max;
    public IntPtr Data { get; private set; }
    public int Length { get; set; }

    public DmoMediaBuffer(int max)
    {
        _max = max;
        Data = Marshal.AllocHGlobal(max);
    }

    public int SetLength(int length)
    {
        if (length < 0 || length > _max) return unchecked((int)0x80070057); // E_INVALIDARG
        Length = length;
        return 0;
    }

    public int GetMaxLength(out int max)
    {
        max = _max;
        return 0;
    }

    public int GetBufferAndLength(IntPtr buffer, IntPtr length)
    {
        if (buffer != IntPtr.Zero) Marshal.WriteIntPtr(buffer, Data);
        if (length != IntPtr.Zero) Marshal.WriteInt32(length, Length);
        return 0;
    }

    public void Dispose()
    {
        if (Data == IntPtr.Zero) return;
        Marshal.FreeHGlobal(Data);
        Data = IntPtr.Zero;
    }
}
