namespace Gokko.Client;

/// <summary>
/// 放送装置のキー・ボタンの設定（車内・車外・連絡）。押すたびに入・切。ゲームを前面にしたままでも効く（キーはゲームにもそのまま渡る）。
/// 1つの操作に、キーとコントローラーのボタンを1つずつ登録できる（"N|Joy0:5"）。
/// </summary>
public sealed class OnboardKeyDialog : Form
{
    /// <summary>操作の名前（設定の鍵）と表示名。</summary>
    public static readonly (string Name, string Label)[] Actions = [("Inside", "車内（車内放送）"), ("Outside", "車外（車外放送）"), ("Intercom", "連絡（インターホン）")];

    /// <summary>決めた割り当て（操作の名前 → 割り当ての文字列。空は使わない）。</summary>
    public Dictionary<string, string> Result { get; } = new();

    private readonly Dictionary<string, Button> _buttons = new();

    public OnboardKeyDialog(IReadOnlyDictionary<string, string> bindings)
    {
        Text = "放送装置のキー・ボタン";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true;
        Font = new Font("Yu Gothic UI", 10f);
        foreach (var (name, _) in Actions) Result[name] = bindings.GetValueOrDefault(name) ?? "";

        ClientSize = new Size(520, 32 * Actions.Length + 160);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        var note = new Label
        {
            Text = OnboardTexts.KeyDialogNote, // 文言は OnboardTexts.cs
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(note);
        layout.SetColumnSpan(note, 3);

        foreach (var (name, label) in Actions)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            var b = new Button { Dock = DockStyle.Fill };
            b.Click += (_, _) =>
            {
                using var dialog = new KeyCaptureDialog($"「{label}」に使うキーかボタンを押してください。") { TopMost = true };
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Selected is not { } chosen) return;
                var isJoy = JoystickPoller.IsJoystick(chosen); // キーはキー同士、ボタンはボタン同士で置き換える
                var kept = CrewController.SplitBindings(Result[name]).Where(x => JoystickPoller.IsJoystick(x) != isJoy);
                Result[name] = string.Join("|", kept.Append(chosen));
                ShowKeys();
            };
            _buttons[name] = b;
            layout.Controls.Add(b);
            var clear = new Button { Text = "なし", Dock = DockStyle.Fill };
            clear.Click += (_, _) => { Result[name] = ""; ShowKeys(); };
            layout.Controls.Add(clear);
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var ok = new Button { Text = "保存", Size = new Size(100, 30), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "やめる", Size = new Size(100, 30), DialogResult = DialogResult.Cancel };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        bottom.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(bottom);
        layout.SetColumnSpan(bottom, 3);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ShowKeys();
    }

    private void ShowKeys()
    {
        foreach (var (name, b) in _buttons)
        {
            var parts = CrewController.SplitBindings(Result[name]).Select(KeyCaptureDialog.BindingText).ToList();
            b.Text = parts.Count > 0 ? string.Join(" ／ ", parts) : "（なし）";
        }
    }
}
