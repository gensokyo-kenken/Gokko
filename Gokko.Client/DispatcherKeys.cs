using System.Runtime.InteropServices;
using System.Text.Json;

namespace Gokko.Client;

/// <summary>司令機のキー（応答・共通／個別の切り替え）。%APPDATA%/GokkoClient/radio-keys.json に保存する。ゲームを操作していても効く（RegisterHotKey）。
/// （共同制作者の「列車無線」のパーツから）</summary>
internal sealed class DispatcherKeys
{
    public Keys Answer { get; set; } = Keys.F7;
    public Keys Mode { get; set; } = Keys.F8;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GokkoClient", "radio-keys.json");
    public static string Label(Keys key) => new KeysConverter().ConvertToString(key) ?? key.ToString();
    public static string? Validate(Keys answer, Keys mode)
    {
        foreach (var key in new[] { answer, mode })
        {
            var code = key & Keys.KeyCode;
            if (code == Keys.None || !Enum.IsDefined(typeof(Keys), code) ||
                code is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu or Keys.Escape or Keys.Tab ||
                ((int)code < 8 || (int)code > 254) || (key & ~(Keys.KeyCode | Keys.Control | Keys.Shift | Keys.Alt)) != 0)
                return "文字・数字・ファンクションキーなどを選んでください。";
            if (code is Keys.F5 or Keys.F6) return "F5 と F6 は防護無線・司令呼出に使うので選べません。";
        }
        return answer == mode ? "応答と切り替えには、別のキーを選んでください。" : null;
    }
    public static DispatcherKeys Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<DispatcherKeys>(File.ReadAllText(FilePath));
            if (value != null && Validate(value.Answer, value.Mode) == null) return value;
        }
        catch { }
        return new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
    internal const int AnswerId = 0x474D, ModeId = 0x474E;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    private static bool Register(IntPtr window, int id, Keys key)
    {
        uint mods = 0x4000; // MOD_NOREPEAT: 長押しで通話方式を連続反転しない。
        if ((key & Keys.Alt) != 0) mods |= 1;
        if ((key & Keys.Control) != 0) mods |= 2;
        if ((key & Keys.Shift) != 0) mods |= 4;
        return RegisterHotKey(window, id, mods, (uint)(key & Keys.KeyCode));
    }
    public bool TryRegister(IntPtr window)
    {
        Release(window);
        if (Register(window, AnswerId, Answer) && Register(window, ModeId, Mode)) return true;
        Release(window); return false;
    }
    public static void Release(IntPtr window)
    {
        UnregisterHotKey(window, AnswerId); UnregisterHotKey(window, ModeId);
    }
}

internal sealed class DispatcherKeyDialog : Form
{
    private readonly TextBox _answer = new() { ReadOnly = true };
    private readonly TextBox _mode = new() { ReadOnly = true };
    public DispatcherKeys Selection { get; }
    public DispatcherKeyDialog(DispatcherKeys current)
    {
        Selection = new DispatcherKeys { Answer = current.Answer, Mode = current.Mode };
        Text = "司令のキー設定"; ClientSize = new Size(440, 245);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        var help = new Label { Text = "入力欄を選び、割り当てたいキーを押してください。\nCtrl・Shift・Altとの組合せも設定できます。", AutoSize = false };
        help.SetBounds(20, 15, 400, 45); Controls.Add(help);
        var a = new Label { Text = "応答", AutoSize = true, Location = new Point(20, 80) };
        var m = new Label { Text = "共通／個別切替", AutoSize = true, Location = new Point(20, 125) };
        _answer.SetBounds(155, 75, 260, 28); _mode.SetBounds(155, 120, 260, 28);
        _answer.Text = DispatcherKeys.Label(Selection.Answer); _mode.Text = DispatcherKeys.Label(Selection.Mode);
        _answer.AccessibleName = "応答キー"; _mode.AccessibleName = "共通・個別切替キー";
        Bind(_answer, k => Selection.Answer = k); Bind(_mode, k => Selection.Mode = k);
        var note = new Label { Text = "司令として参加している間は、ほかの画面（ゲームなど）を操作していても効きます。", AutoSize = true, Location = new Point(20, 163) };
        var ok = new Button { Text = "保存", Location = new Point(245, 200), Size = new Size(80, 30) };
        var cancel = new Button { Text = "戻る", DialogResult = DialogResult.Cancel, Location = new Point(335, 200), Size = new Size(80, 30) };
        ok.Click += (_, _) =>
        {
            var error = DispatcherKeys.Validate(Selection.Answer, Selection.Mode);
            if (error != null) { MessageBox.Show(this, error, Text); return; }
            DialogResult = DialogResult.OK;
        };
        Controls.AddRange([a, m, _answer, _mode, note, ok, cancel]);
    }
    private static void Bind(TextBox box, Action<Keys> set)
    {
        box.PreviewKeyDown += (_, e) => { if (e.KeyCode != Keys.Tab) e.IsInputKey = true; };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Tab) return;
            e.SuppressKeyPress = true; e.Handled = true;
            if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu) return;
            set(e.KeyData); box.Text = DispatcherKeys.Label(e.KeyData);
        };
    }
}
