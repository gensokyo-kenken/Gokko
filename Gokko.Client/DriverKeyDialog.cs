using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 運転士のキー・ボタンの設定（ゲームで使っているキーやコントローラーのボタンを登録する）。
/// 登録したものを押すと、乗っている車掌のゲームでも同じボタンを押す。ブザーは押している間だけ鳴らす。
/// 1つの操作に、キーとコントローラーのボタンを1つずつ登録できる（"N|Joy0:5"）。
/// </summary>
public sealed class DriverKeyDialog : Form
{
    /// <summary>決めた割り当て（"Buzzer" とボタン名 → 割り当ての文字列。空は使わない）。</summary>
    public Dictionary<string, string> Result { get; } = new();

    private readonly Dictionary<string, Button> _buttons = new();

    public DriverKeyDialog(string buzzerBindings, IReadOnlyDictionary<string, string> bindings)
    {
        Text = "運転士のキー・ボタン";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Yu Gothic UI", 10f);

        var rows = new List<(string Name, string Label)> { ("Buzzer", "ブザー（押している間）") };
        rows.AddRange(DriverInput.Actions.Select(kv => (kv.Key, kv.Value)));
        Result["Buzzer"] = buzzerBindings;
        foreach (var (name, _) in DriverInput.Actions) Result[name] = bindings.GetValueOrDefault(name) ?? "";

        ClientSize = new Size(520, 32 * rows.Count + 180);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        var note = new Label
        {
            Text = "ゲームで使っているキーやコントローラーのボタンを登録すると、押したときに車掌のゲームでも同じボタンを押します。" +
                   "キーとボタンを1つずつ登録できます（マウスで直接触った操作は伝わりません）。",
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(note);
        layout.SetColumnSpan(note, 3);

        foreach (var (name, label) in rows)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            var b = new Button { Dock = DockStyle.Fill };
            b.Click += (_, _) =>
            {
                using var dialog = new KeyCaptureDialog($"「{label}」に使っているキーかボタンを押してください。");
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Selected is not { } chosen) return;
                // キーはキー同士、ボタンはボタン同士で置き換える（両方を1つずつ持てる）
                var isJoy = JoystickPoller.IsJoystick(chosen);
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
