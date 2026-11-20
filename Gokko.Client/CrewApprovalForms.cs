namespace Gokko.Client;

/// <summary>
/// 乗務の許可の小さな窓（運転士・車掌の画面なので今風に。ゲームを操作していても見えるよう、常に最前面）。
/// 本体の画面を止めないよう、モーダルにはしない。
/// </summary>
internal sealed class CrewPromptForm : Form
{
    private static readonly Color Back = Color.FromArgb(0x1f, 0x26, 0x33);
    private static readonly Color Fore = Color.FromArgb(0xec, 0xf0, 0xf6);
    private static readonly Color Accent = Color.FromArgb(0x2f, 0x81, 0xf7);
    private static readonly Color Muted = Color.FromArgb(0x3a, 0x44, 0x55);

    /// <summary>押したボタン（true=はい・OK、false=いいえ。× で閉じたら false）。</summary>
    public event Action<bool>? Answered;

    private bool _answered;

    /// <param name="yesNo">true なら［はい］［いいえ］、false なら［OK］だけ。</param>
    public CrewPromptForm(string title, string message, bool yesNo)
    {
        Text = title;
        TopMost = true;
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Back;
        ForeColor = Fore;
        Font = new Font("Yu Gothic UI", 11f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 18, 20, 14);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 16)
        });

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty };
        Button Make(string text, bool value, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(96, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Muted,
                ForeColor = Color.White,
                Margin = new Padding(8, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => Answer(value);
            return b;
        }

        if (yesNo)
        {
            // 右から: いいえ・はい（はいを先に押しやすく、Enter は はい）
            var no = Make("いいえ", false, false);
            var yes = Make("はい", true, true);
            buttons.Controls.AddRange([no, yes]);
            AcceptButton = yes;
            CancelButton = no;
        }
        else
        {
            var ok = Make("OK", true, true);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
        }

        layout.Controls.Add(buttons);
        Controls.Add(layout);
        FormClosed += (_, _) => Answer(false);
    }

    private void Answer(bool value)
    {
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(value);
        if (!IsDisposed) Close();
    }

    /// <summary>返事をせずに閉じる（申し込みが取り消されたとき）。</summary>
    public void CloseSilently()
    {
        _answered = true;
        if (!IsDisposed) Close();
    }
}
