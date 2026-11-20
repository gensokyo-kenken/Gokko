namespace Gokko.Client;

/// <summary>
/// キーかコントローラーのボタンを1つ決める小さな窓（押したものをそのまま選ぶ）。運転士のキーの設定に使う。
/// 結果は <see cref="Selected"/>（キーなら Keys の名前、コントローラーなら "Joy{番号}:{ボタン}"）。
/// </summary>
public sealed class KeyCaptureDialog : Form
{
    public string? Selected { get; private set; }

    private readonly JoystickPoller _joy = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };

    public KeyCaptureDialog(string prompt)
    {
        Text = "キー・ボタンの設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        Font = new Font("Yu Gothic UI", 10f);
        ClientSize = new Size(400, 130);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.Controls.Add(new Label { Text = $"{prompt}\n（キーボードのキーか、コントローラーのボタンを押してください）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        var cancel = new Button { Text = "やめる", Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };
        layout.Controls.Add(cancel);
        Controls.Add(layout);
        CancelButton = cancel;

        _joy.Poll(); // いま押しているボタンを覚えておく（押し直したものだけ選ぶ）
        _timer.Tick += (_, _) =>
        {
            foreach (var (binding, down) in _joy.Poll())
            {
                if (!down) continue;
                Choose(binding);
                return;
            }
        };
        _timer.Start();
    }

    private void Choose(string binding)
    {
        _timer.Stop();
        Selected = binding;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        Choose(key.ToString());
        return true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    public static string KeyText(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)(k - Keys.D0)).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "テンキー" + (int)(k - Keys.NumPad0),
        _ => k.ToString()
    };

    /// <summary>割り当ての表示（キーでもコントローラーでも）。</summary>
    public static string BindingText(string binding) =>
        JoystickPoller.IsJoystick(binding) ? JoystickPoller.Text(binding) : Enum.TryParse<Keys>(binding, out var k) ? KeyText(k) : binding;
}
