using System.Runtime.InteropServices;

namespace Gokko.Client;

/// <summary>
/// ゲームコントローラー（マスコン・ゲームパッドなど）のボタンを読む。Windows の昔からのジョイスティックの仕組み（winmm）を使う。
/// XInput のゲームパッド（Xbox のコントローラーなど）も、ここに出てくる。
/// 割り当ては "Joy{番号}:{ボタン番号}"（番号は 0 から、ボタンは 1 から）。
/// </summary>
public sealed class JoystickPoller
{
    private const int MaxDevices = 16;
    private const uint JOY_RETURNBUTTONS = 0x80;
    private const int JOYERR_NOERROR = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    private readonly uint[] _last = new uint[MaxDevices];
    private readonly bool[] _present = new bool[MaxDevices];
    private long _lastScanMs;

    /// <summary>
    /// 押した・離したボタンを返す（前回から変わったものだけ）。つながっていない番号は、5秒ごとにだけ確かめ直す（毎回確かめると重いため）。
    /// </summary>
    public List<(string Binding, bool Down)> Poll()
    {
        var changes = new List<(string, bool)>();
        var now = Environment.TickCount64;
        var rescan = now - _lastScanMs > 5000;
        if (rescan) _lastScanMs = now;
        for (var id = 0; id < MaxDevices; id++)
        {
            if (!_present[id] && !rescan) continue;
            var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
            if (joyGetPosEx(id, ref info) != JOYERR_NOERROR)
            {
                if (_present[id])
                    for (var b = 0; b < 32; b++)
                        if ((_last[id] & (1u << b)) != 0) changes.Add(($"Joy{id}:{b + 1}", false)); // 抜けたら、押していたボタンは離したことにする
                _present[id] = false;
                _last[id] = 0;
                continue;
            }

            var first = !_present[id];
            _present[id] = true;
            var diff = info.dwButtons ^ _last[id];
            _last[id] = info.dwButtons;
            if (first || diff == 0) continue; // つないだ直後に押しっぱなしだったボタンは、押したことにしない
            for (var b = 0; b < 32; b++)
                if ((diff & (1u << b)) != 0) changes.Add(($"Joy{id}:{b + 1}", (info.dwButtons & (1u << b)) != 0));
        }

        return changes;
    }

    /// <summary>割り当ての文字列が、コントローラーのボタンか。</summary>
    public static bool IsJoystick(string binding) => binding.StartsWith("Joy", StringComparison.Ordinal) && binding.Contains(':');

    /// <summary>表示用（"コントローラー1 ボタン5"）。</summary>
    public static string Text(string binding)
    {
        var colon = binding.IndexOf(':');
        return int.TryParse(binding[3..colon], out var id) ? $"コントローラー{id + 1} ボタン{binding[(colon + 1)..]}" : binding;
    }
}
