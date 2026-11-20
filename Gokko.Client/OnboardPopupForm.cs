using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 放送装置の操作のポップ（車掌用操作盤とは別の小さな窓。いつも最前面）。運転士・車掌が、ゲームの画面の上で放送とインターホンを扱う。
/// ・車内放送・車外放送: 押すと流し始め、もう一度押すと止める
/// ・インターホン: 入・切
/// 閉じても隠すだけ（本体の「車内放送」タブの［放送装置を開く］で、また出す）。位置と大きさは覚える。
/// </summary>
public sealed class OnboardPopupForm : Form
{
    private static readonly Color Back = Color.FromArgb(31, 35, 43);
    private static readonly Color Muted = Color.FromArgb(160, 168, 180);

    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly CheckBox _inside = Toggle("車内放送");
    private readonly CheckBox _outside = Toggle("車外放送");
    private readonly CheckBox _intercom = Toggle("インターホン");
    private readonly Label _send = new() { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly LevelBar _mic = new() { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 4) };

    /// <summary>車内放送・車外放送のボタンを押した（流す・止めるは本体が決める）。</summary>
    public event Action<OnboardChannel>? BroadcastClicked;

    /// <summary>インターホンのボタンを押した。</summary>
    public event Action? IntercomClicked;

    /// <summary>true にすると、閉じるボタンで本当に閉じる（ふだんは隠すだけ）。</summary>
    public bool AllowClose { get; set; }

    /// <summary>ユーザーが閉じるボタンで隠した。</summary>
    public event Action? HiddenByUser;

    private static CheckBox Toggle(string text) => new()
    {
        Text = text, Appearance = Appearance.Button, AutoCheck = false, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
        TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold), Margin = new Padding(3)
    };

    private readonly RadioSettings _settings;

    /// <summary>モニター: 自分が話した声を、スピーカー越しの音にして自分でも聞く（音量の確認用。ヘッドホンで）。</summary>
    private readonly CheckBox _monitor = new()
    {
        Text = OnboardTexts.MonitorCheckShort, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(229, 231, 235), Margin = new Padding(3, 0, 3, 0)
    };

    private readonly Label _model = new() { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly List<(TrackBar Bar, Label Value, Func<float> Get)> _volumes = new();

    /// <summary>［キー設定］を押した。</summary>
    public event Action? KeysClicked;

    /// <summary>［音の設定］を押した（マイクの感度・ブースト・ノイズゲート。列車無線と共通）。</summary>
    public event Action? SoundClicked;

    /// <param name="settings">音量（送る音量・放送を聞く音量・連絡を聞く音量）をここで変える。</param>
    /// <param name="save">音量を変えたら呼ぶ（設定を保存する）。</param>
    public OnboardPopupForm(string title, RadioSettings settings, Action save)
    {
        Text = title;
        Font = new Font("Yu Gothic UI", 9.5f);
        BackColor = Back;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(340, 326);
        MinimumSize = new Size(260, 296);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20); // ふだんは画面の右下

        // ゲームの放送操作盤と同じ呼び名（車内・車外・連絡）
        _inside.Text = "車内";
        _outside.Text = "車外";
        _intercom.Text = "連絡";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(6, 4, 6, 4) };
        for (var i = 0; i < 3; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); // 0 状態
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 1 ボタン
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // 2 送話
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10)); // 3 マイク
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // 4〜6 音量
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // 7 形式・キー設定
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // 8 モニター・音の設定
        void Span(Control c, int col, int row, int span)
        {
            layout.Controls.Add(c, col, row);
            layout.SetColumnSpan(c, span);
        }

        Span(_status, 0, 0, 3);
        layout.Controls.Add(_inside, 0, 1);
        layout.Controls.Add(_outside, 1, 1);
        layout.Controls.Add(_intercom, 2, 1);
        Span(_send, 0, 2, 3);
        Span(_mic, 0, 3, 3);

        // 音量（話しながらでも変えられる）。送る音量は自分の声の大きさ、聞く音量は自分に聞こえる大きさ
        void Volume(string label, Func<float> get, Action<float> set, int row)
        {
            var name = new Label { Text = label, ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            var bar = new TrackBar
            {
                Minimum = 0, Maximum = 200, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 10, Dock = DockStyle.Fill, BackColor = Back,
                AutoSize = false, Height = 26, AccessibleName = label, Value = (int)Math.Round(Math.Clamp(get(), 0, 2) * 100)
            };
            var value = new Label { ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Text = $"{bar.Value}%" };
            var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            inner.Controls.Add(bar, 0, 0);
            inner.Controls.Add(value, 1, 0);
            bar.ValueChanged += (_, _) =>
            {
                value.Text = $"{bar.Value}%";
                set(bar.Value / 100f);
                save();
            };
            layout.Controls.Add(name, 0, row);
            Span(inner, 1, row, 2);
            _volumes.Add((bar, value, get));
        }

        Volume("送る音量", () => settings.OnboardSendGain, v => settings.OnboardSendGain = v, 4);
        Volume("放送を聞く", () => settings.BroadcastVolume, v => settings.BroadcastVolume = v, 5);
        Volume("連絡を聞く", () => settings.IntercomVolume, v => settings.IntercomVolume = v, 6);

        var keys = new Button
        {
            Text = "キー設定", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(55, 61, 72), Margin = new Padding(3, 2, 3, 2)
        };
        keys.FlatAppearance.BorderColor = Color.FromArgb(75, 82, 95);
        keys.Click += (_, _) => KeysClicked?.Invoke();
        Span(_model, 0, 7, 2);
        layout.Controls.Add(keys, 2, 7);
        _monitor.Checked = settings.OnboardMonitor;
        _monitor.CheckedChanged += (_, _) =>
        {
            if (settings.OnboardMonitor == _monitor.Checked) return;
            settings.OnboardMonitor = _monitor.Checked;
            save();
        };
        Span(_monitor, 0, 8, 2);
        var sound = new Button
        {
            Text = "音の設定", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(55, 61, 72), Margin = new Padding(3, 1, 3, 1)
        };
        sound.FlatAppearance.BorderColor = Color.FromArgb(75, 82, 95);
        sound.Click += (_, _) => SoundClicked?.Invoke();
        layout.Controls.Add(sound, 2, 8);
        _settings = settings;
        Controls.Add(layout);

        _inside.Click += (_, _) => BroadcastClicked?.Invoke(OnboardChannel.Inside);
        _outside.Click += (_, _) => BroadcastClicked?.Invoke(OnboardChannel.Outside);
        _intercom.Click += (_, _) => IntercomClicked?.Invoke();
    }

    /// <summary>当てたキーをボタンに小さく出す（「車内\nF9」など）。</summary>
    public void ShowKeys(string inside, string outside, string intercom)
    {
        static string Label(string name, string key) => key.Length > 0 ? $"{name}\n{key}" : name;
        _insideKey = Label("車内", inside);
        _outsideKey = Label("車外", outside);
        _intercomKey = intercom;
    }

    private string _insideKey = "車内", _outsideKey = "車外", _intercomKey = "";

    /// <summary>形式の表示（「形式 5320（新しめの車両の音）」など）。</summary>
    public void ShowModel(string text)
    {
        if (_model.Text != text) _model.Text = text;
    }

    /// <summary>本体のタブなどで音量を変えたら、つまみも合わせる。</summary>
    private void SyncVolumes()
    {
        foreach (var (bar, _, get) in _volumes)
        {
            if (bar.Capture) continue;
            var v = (int)Math.Round(Math.Clamp(get(), 0, 2) * 100);
            if (v != bar.Value) bar.Value = v;
        }
    }

    /// <summary>前回の窓の位置と大きさ（画面からはみ出すなら使わない）。</summary>
    public void ApplySavedBounds(string text)
    {
        var b = text.Split(',');
        if (b.Length != 4 || !b.All(x => int.TryParse(x, out _))) return;
        var r = new Rectangle(int.Parse(b[0]), int.Parse(b[1]), int.Parse(b[2]), int.Parse(b[3]));
        if (r.Width < MinimumSize.Width || r.Height < MinimumSize.Height) return;
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.X, r.Y, r.Width, 30)))) return;
        Bounds = r;
    }

    public string SavedBounds => $"{Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height}";

    /// <summary>本体の表示に合わせる（100ms ごと）。</summary>
    public void ShowState(string status, Color statusColor, OnboardState? st, string send, Color sendColor, float micLevel)
    {
        if (_status.Text != status) _status.Text = status;
        // 暗い背景でも読めるように、明るい色に置き換える
        _status.ForeColor = statusColor == MainForm.InsideColor ? Color.FromArgb(147, 197, 253)
            : statusColor == MainForm.OutsideColor ? Color.FromArgb(252, 211, 77)
            : statusColor == Color.Gray ? Muted : Color.White;
        var canTalk = st?.CanTalk == true;
        var otherBroadcasting = st != null && st.Broadcast != OnboardChannel.None && st.BroadcasterId != st.SelfId;
        // 押されているボタンは水色（「入」とは書かない）
        Paint(_inside, st is { Broadcast: OnboardChannel.Inside } && st.BroadcasterId == st.SelfId, canTalk && !otherBroadcasting, MainForm.PressedColor);
        Paint(_outside, st is { Broadcast: OnboardChannel.Outside } && st.BroadcasterId == st.SelfId, canTalk && !otherBroadcasting, MainForm.PressedColor);
        Paint(_intercom, st?.Intercom == true, canTalk, MainForm.PressedColor);
        var intercomText = "連絡" + (_intercomKey.Length > 0 ? $"\n{_intercomKey}" : "");
        if (_intercom.Text != intercomText) _intercom.Text = intercomText;
        if (_inside.Text != _insideKey) _inside.Text = _insideKey;
        if (_outside.Text != _outsideKey) _outside.Text = _outsideKey;
        if (_send.Text != send) _send.Text = send;
        _send.ForeColor = sendColor == Color.Firebrick ? Color.FromArgb(248, 113, 113) : Muted;
        _mic.Level = micLevel;
        SyncVolumes();
        if (_monitor.Checked != _settings.OnboardMonitor) _monitor.Checked = _settings.OnboardMonitor; // タブで変えたら合わせる
    }

    private static void Paint(CheckBox b, bool on, bool enabled, Color color)
    {
        b.Checked = on;
        b.Enabled = enabled || on;
        b.BackColor = on ? color : Color.FromArgb(55, 61, 72);
        b.ForeColor = on ? MainForm.PressedText : enabled ? Color.FromArgb(229, 231, 235) : Color.FromArgb(110, 117, 128);
        b.FlatAppearance.BorderColor = on ? color : Color.FromArgb(75, 82, 95);
        b.FlatAppearance.CheckedBackColor = b.BackColor; // 入のときもこの色（既定の淡い色にしない）
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            HiddenByUser?.Invoke();
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>マイクの大きさの細い帯。</summary>
    private sealed class LevelBar : Control
    {
        private float _level;

        public LevelBar()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(55, 61, 72);
        }

        public float Level
        {
            set
            {
                var v = Math.Clamp(value, 0, 1);
                if (Math.Abs(v - _level) < 0.01f) return;
                _level = v;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var w = (int)(Width * _level);
            if (w <= 0) return;
            using var brush = new SolidBrush(_level > 0.8f ? Color.FromArgb(248, 113, 113) : Color.FromArgb(74, 222, 128));
            e.Graphics.FillRectangle(brush, 0, 0, w, Height);
        }
    }
}
