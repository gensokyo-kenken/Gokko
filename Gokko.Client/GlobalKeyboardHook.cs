using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gokko.Client;

/// <summary>
/// システム全体のキー入力を拾う低レベルキーボードフック（ConductorApp から WinForms 用に移したもの）。
/// 車掌用操作盤が前面でなくても（ゲームを操作していても）キーで扱えるようにする。キーはゲームにもそのまま渡す。
/// 通知はフックを入れたスレッド（UI のスレッド）で届く。
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc;
    private readonly HashSet<Keys> _pressed = new();
    private IntPtr _hookId = IntPtr.Zero;

    /// <summary>押した（キーリピートは除く）。</summary>
    public event Action<Keys>? KeyDown;

    /// <summary>離した。</summary>
    public event Action<Keys>? KeyUp;

    public GlobalKeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _pressed.Clear();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var key = (Keys)Marshal.ReadInt32(lParam);
            var msg = (int)wParam;
            try
            {
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (_pressed.Add(key)) KeyDown?.Invoke(key);
                }
                else if (msg is WM_KEYUP or WM_SYSKEYUP)
                {
                    _pressed.Remove(key);
                    KeyUp?.Invoke(key);
                }
            }
            catch
            {
                // フックの中で例外を出すと、キー入力全体が止まるので握りつぶす
            }
        }

        // ゲームにもキーを渡す
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
