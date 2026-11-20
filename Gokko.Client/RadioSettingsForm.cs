namespace Gokko.Client;

/// <summary>
/// 列車無線の音の設定。変えたらすぐ効く（閉じたときに保存する）。
/// ・受話: 受話音量、無線風の音（入・切と強さ）
/// ・マイク: エコー除去、感度、小さい音を切る下限（ノイズゲート）。テストでマイクの大きさを見ながら合わせる
/// ・人ごとの音量: これまでに聞いた人（社員番号ごと）
/// </summary>
public sealed class RadioSettingsForm : Form
{
    private static readonly Color Back = Color.FromArgb(246, 247, 249);
    private static readonly Color Card = Color.White;
    private static readonly Color Ink = Color.FromArgb(32, 36, 44);
    private static readonly Color Muted = Color.FromArgb(110, 117, 128);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);

    private readonly RadioSettings _settings;
    private readonly TrainRadioAudio _audio;
    private readonly Action _save;
    private readonly LevelMeter _meter = new() { Dock = DockStyle.Fill };
    private readonly Label _micState = new() { AutoSize = true, ForeColor = Muted };
    private readonly CheckBox _test = new() { Text = "マイクのテスト", Appearance = Appearance.Button, AutoSize = false, Size = new Size(130, 30), TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat };
    private readonly FlowLayoutPanel _speakers = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private string _speakerKey = "";

    public RadioSettingsForm(RadioSettings settings, TrainRadioAudio audio, Action save)
    {
        _settings = settings;
        _audio = audio;
        _save = save;
        Text = "音の設定（列車無線・放送装置で共通）";
        Font = new Font("Yu Gothic UI", 10f);
        BackColor = Back;
        ForeColor = Ink;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 700);
        MinimumSize = new Size(460, 480);
        ShowInTaskbar = false;

        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(16, 12, 16, 16)
        };
        Controls.Add(page);

        // ---- 受話 ----
        var receive = NewCard(page, "受話", "聞こえる声の大きさと、無線らしい音。");
        AddSlider(receive, "受話音量（話しているとき）", 0, 100, (int)(settings.Volume * 100), v => $"{v}%", v => settings.Volume = v / 100f);
        var effect = AddCheck(receive, "無線風の音にする", "声の高い音・低い音を削り、軽く歪ませます（雑音は混ぜません）。",
            settings.RadioEffect, v => settings.RadioEffect = v);
        var strength = AddSlider(receive, "無線風の強さ", 0, 100, (int)(settings.EffectStrength * 100), v => $"{v}%", v => settings.EffectStrength = v / 100f);
        strength.Enabled = settings.RadioEffect;
        effect.CheckedChanged += (_, _) => strength.Enabled = effect.Checked;

        // ---- マイク ----
        var mic = NewCard(page, "マイク", "ハウリングを防ぐにはヘッドセットがおすすめです。");
        AddDevicePicker(mic);
        var echo = AddCheck(mic, "エコー除去", "スピーカーから出た受話の声を、マイクの音から差し引きます（Windows の通話用の音声処理）。雑音の抑えも効きます。",
            settings.EchoCancel, v => settings.EchoCancel = v);
        echo.CheckedChanged += (_, _) => _audio.ReopenMicrophone();
        AddSlider(mic, "マイクの感度（ブースト）", 25, (int)(TrainRadioAudio.MaxMicGain * 100), (int)(settings.MicGain * 100),
            v => $"{v}%（{20 * Math.Log10(v / 100.0):+0;-0;0} dB）", v => settings.MicGain = v / 100f);
        AddCheck(mic, "自動で音量をそろえる", "小さい声は自動で持ち上げ（最大 8倍）、大きすぎる声は割れないように抑えます。ブーストと一緒に使えます。",
            settings.AutoGain, v => settings.AutoGain = v);
        AddSlider(mic, "小さい音を切る（ノイズゲート）", 0, 200, (int)Math.Round(settings.Gate * 1000), v => v == 0 ? "切らない" : $"{v / 10.0:0.0}%", v => settings.Gate = v / 1000f);
        var meterRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, Height = 34, Margin = new Padding(0, 6, 0, 0) };
        meterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        meterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        meterRow.Controls.Add(_meter, 0, 0);
        meterRow.Controls.Add(_test, 1, 0);
        _test.FlatAppearance.BorderColor = Accent;
        _test.CheckedChanged += (_, _) =>
        {
            _audio.MicTest = _test.Checked;
            _test.BackColor = _test.Checked ? Accent : Card;
            _test.ForeColor = _test.Checked ? Color.White : Accent;
        };
        _test.ForeColor = Accent;
        mic.Controls.Add(meterRow);
        mic.Controls.Add(Hint("縦の線より大きい音だけを送ります。声を出してもメーターが線に届かないなら、感度（ブースト）を上げるか線を下げてください。話していないときに線を越えるなら、線を上げてください。"));
        mic.Controls.Add(_micState);

        // ---- 人ごとの音量 ----
        var people = NewCard(page, "人ごとの音量", "これまでに聞いた人です（名前の横は、最後に聞いたときの列番と役割）。100% がふつうの大きさです。");
        people.Controls.Add(_speakers);
        _speakers.Layout += (_, _) =>
        {
            foreach (Control c in _speakers.Controls)
                if (c is Label l) l.MaximumSize = new Size(_speakers.ClientSize.Width, 0);
                else c.Width = _speakers.ClientSize.Width;
        };

        page.Resize += (_, _) => FitWidth(page);
        Shown += (_, _) => FitWidth(page);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private static void FitWidth(FlowLayoutPanel page)
    {
        var w = page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        foreach (Control c in page.Controls) c.Width = Math.Max(300, w);
    }

    private void Tick()
    {
        _meter.Level = _audio.MicLevel;
        _meter.Gate = _settings.Gate;
        _meter.Open = _audio.Transmitting || (_audio.MicTest && _audio.MicLevel >= _settings.Gate);
        _meter.Invalidate();
        _micState.Text = _audio.Error.Length > 0 ? _audio.Error
            : _audio.MicrophoneOpen ? $"マイク: {_audio.MicKind}" + (_audio.MicTest ? "（テスト中。送りません）" : "")
            : "マイクは通話中だけ開きます。［マイクのテスト］で大きさを確かめられます。";
        _micState.ForeColor = _audio.Error.Length > 0 ? Color.Firebrick : Muted;

        var key = string.Join("|", _settings.SpeakerNames.OrderBy(x => x.Key).Select(x => x.Key + x.Value + _settings.SpeakerLabels.GetValueOrDefault(x.Key)));
        if (key == _speakerKey) return;
        _speakerKey = key;
        _speakers.SuspendLayout();
        _speakers.Controls.Clear();
        if (_settings.SpeakerNames.Count == 0) _speakers.Controls.Add(Hint("まだ誰の声も聞いていません。"));
        foreach (var (id, name) in _settings.SpeakerNames.OrderBy(x => x.Value, StringComparer.CurrentCulture))
        {
            var v = (int)Math.Round(_audio.SpeakerVolume(id) * 100);
            var label = _settings.SpeakerLabels.GetValueOrDefault(id, "");
            AddSlider(_speakers, label.Length > 0 && label != name ? $"{name}　{label}" : name, 0, 200, v, x => $"{x}%", x =>
            {
                if (x == 100) _settings.SpeakerVolumes.Remove(id);
                else _settings.SpeakerVolumes[id] = x / 100f;
            });
        }

        _speakers.ResumeLayout();
    }

    // ---- 部品 ----

    private static FlowLayoutPanel NewCard(Control page, string title, string note)
    {
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Card,
            Padding = new Padding(16, 12, 16, 14), Margin = new Padding(0, 0, 0, 12)
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 229, 234));
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Yu Gothic UI", 12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
        card.Controls.Add(Hint(note));
        card.Layout += (_, _) =>
        {
            foreach (Control c in card.Controls)
                if (c is TableLayoutPanel or FlowLayoutPanel) c.Width = card.ClientSize.Width - card.Padding.Horizontal;
                else if (c is Label l) l.MaximumSize = new Size(card.ClientSize.Width - card.Padding.Horizontal, 0);
        };
        page.Controls.Add(card);
        return card;
    }

    /// <summary>入力デバイス（マイク）を選ぶ欄。変えたら、マイクを開き直す。</summary>
    private void AddDevicePicker(Control card)
    {
        card.Controls.Add(new Label { Text = "入力デバイス", AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(0, 0, 0, 4) };
        void Fill()
        {
            box.Items.Clear();
            box.Items.Add(new RadioInputDevice("", "Windows の既定のマイク"));
            foreach (var d in RadioMicrophone.Devices()) box.Items.Add(d);
            var index = box.Items.Cast<RadioInputDevice>().ToList().FindIndex(d => d.Id == (_settings.InputDeviceId ?? ""));
            if (index < 0)
            {
                box.Items.Add(new RadioInputDevice(_settings.InputDeviceId!, "（選んでいたマイクは、今はつながっていません）"));
                index = box.Items.Count - 1;
            }

            box.SelectedIndex = index;
        }

        Fill();
        box.DropDown += (_, _) =>
        {
            var keep = _settings.InputDeviceId;
            box.BeginUpdate();
            Fill(); // 開くたびに、つながっているマイクを読み直す
            box.EndUpdate();
            _settings.InputDeviceId = keep;
        };
        box.SelectionChangeCommitted += (_, _) =>
        {
            if (box.SelectedItem is not RadioInputDevice d || d.Id == _settings.InputDeviceId) return;
            _settings.InputDeviceId = d.Id;
            _save();
            _audio.ReopenMicrophone();
        };
        card.Controls.Add(box);
    }

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 2, 0, 6) };

    private CheckBox AddCheck(Control card, string title, string note, bool value, Action<bool> set)
    {
        var box = new CheckBox { Text = title, Checked = value, AutoSize = true, Margin = new Padding(0, 8, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        box.CheckedChanged += (_, _) =>
        {
            set(box.Checked);
            _save();
        };
        card.Controls.Add(box);
        card.Controls.Add(new Label { Text = note, AutoSize = true, ForeColor = Muted, Margin = new Padding(20, 0, 0, 4) });
        return box;
    }

    private static TableLayoutPanel AddSlider(Control card, string title, int min, int max, int value, Func<int, string> format, Action<int> set)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Height = 64, Width = 300, Margin = new Padding(0, 6, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        var label = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left };
        var shown = new Label { AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = Accent, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold) };
        var bar = new TrackBar
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), TickStyle = TickStyle.None, Dock = DockStyle.Fill,
            SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10)
        };
        shown.Text = format(bar.Value);
        bar.ValueChanged += (_, _) =>
        {
            shown.Text = format(bar.Value);
            set(bar.Value);
        };
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(shown, 1, 0);
        row.Controls.Add(bar, 0, 1);
        row.SetColumnSpan(bar, 2);
        card.Controls.Add(row);
        return row;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        _audio.MicTest = false;
        _save();
        base.OnFormClosed(e);
    }
}

/// <summary>マイクの大きさのメーター（ノイズゲートの線つき）。</summary>
internal sealed class LevelMeter : Control
{
    public float Level;
    public float Gate;
    public bool Open;

    public LevelMeter()
    {
        DoubleBuffered = true;
        Height = 22;
        Margin = new Padding(0, 6, 10, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 4, Width - 1, Height - 9);
        using var back = new SolidBrush(Color.FromArgb(236, 238, 242));
        g.FillRectangle(back, r);
        // 大きさは 0〜0.5 を見やすく広げて出す
        float Scale(float v) => Math.Clamp((float)Math.Sqrt(Math.Clamp(v, 0, 1) / 0.5f), 0, 1);
        var w = (int)(r.Width * Scale(Level));
        using var fill = new SolidBrush(Open ? Color.FromArgb(22, 163, 74) : Color.FromArgb(148, 163, 184));
        g.FillRectangle(fill, r.X, r.Y, w, r.Height);
        var x = r.X + (int)(r.Width * Scale(Gate));
        using var line = new Pen(Color.FromArgb(220, 38, 38), 2);
        if (Gate > 0) g.DrawLine(line, x, 0, x, Height);
    }
}
