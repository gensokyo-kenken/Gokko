using System.Drawing.Drawing2D;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 実機風の列車無線機（縦型受話器。別ウィンドウ）。F6 は司令の呼出（送話キーではない）。司令員が応答したら、当事者のマイクが開く。
/// ・運転士・車掌の無線機（360×700）: 左のキーは「呼出」「取消」。司令員からの個別呼出が来たら「呼出」が「応答」になる
/// ・司令機（1040×700）: 左のキーは「応答」「共通／個別」「取消・終了」。右に司令操作盤（呼出の一覧・一斉通告・区間防護）と、
///   列車防護の受報一覧（運転士の F5）・司令からの個別呼出が付く。応答・切り替えはキー（初めは F7・F8）でもできる
/// （共同制作者の「列車無線」のパーツの縦型の見た目を使い、司令員が何人もいる形・音の設定・区間防護の複数発報に合わせたもの）
/// </summary>
public sealed class TrainRadioConsole : Form
{
    private static readonly Color Lcd = Color.FromArgb(129, 227, 102);
    private static readonly Color LcdText = Color.FromArgb(222, 218, 158);
    private static readonly Color Beige = Color.Beige;
    private static readonly Color Muted = Color.Silver;
    private static readonly Rectangle Handset = new(153, 81, 131, 473);

    private readonly ServerLink _server;
    private readonly RadioSettings _settings;
    private readonly Action _saveSettings;
    private readonly TrainRadioAudio _audio;
    private readonly Func<bool> _alarm;
    private readonly bool _dispatcher;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly Image _face = LoadArt("radio_vertical.png");

    // 左の操作キー
    private readonly ConsoleButton _call = new() { Small = true, Accent = Color.FromArgb(225, 108, 49) };
    private readonly ConsoleButton _mode = new() { Small = true, Text = "共通" };
    private readonly ConsoleButton _cancel = new() { Small = true, Text = "取消" };
    private readonly VolumeKnob _volume = new();
    private readonly ConsoleButton _settingsButton = new() { Text = "音の設定", Small = true };
    private readonly ConsoleButton _pinButton = new() { Small = true };

    // 大きさの変更: 絵と部品を、もとの大きさ（_base）の座標で置き、倍率をかけて描く。縦横の比は変えない
    private readonly Size _base;
    private readonly Dictionary<Control, Rectangle> _layout = new();
    private readonly Dictionary<Control, float> _fontSizes = new();
    private float _scale = 1f;
    private Point _offset;

    // 司令操作盤
    private readonly ListBox _requests = DarkList(Color.FromArgb(34, 49, 38), Color.FromArgb(190, 223, 152));
    private readonly ConsoleButton _broadcast = new() { Text = "一斉通告", Small = true };
    private readonly ComboBox _firstStation = StationSelector();
    private readonly ComboBox _lastStation = StationSelector();
    private readonly GuardedProtectionSwitch _protectionSwitch = new();
    private readonly ConsoleButton _keySettings = new() { Small = true };

    // 列車防護の受報一覧・司令からの個別呼出・区間防護の発報中の一覧
    private readonly ListBox _protectionSources = DarkList(Color.FromArgb(40, 31, 27), Color.Salmon);
    private readonly ConsoleButton _confirmAlarm = new() { Text = "確認（警報音停止）", Small = true, Accent = Color.FromArgb(196, 62, 48) };

    // 司令機の防護受報: 警報音（運転士の F5 の発報があれば鳴らす）。「確認」した発報は音を止め、ランプを点けたままにする
    private ProtectionRadioAudio? _monitorAudio;
    private bool _monitorAudioFailed;
    private readonly HashSet<string> _confirmedSources = new();
    private bool _monitorUnconfirmed, _monitorActive;
    private readonly ComboBox _trainSelect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Yu Gothic UI", 10.5f), BackColor = Color.FromArgb(29, 43, 33), ForeColor = Color.FromArgb(222, 218, 158) };
    private readonly ConsoleButton _directCall = new() { Text = "個別呼出", Small = true };
    private readonly ListBox _ranges = DarkList(Color.FromArgb(34, 49, 38), Color.FromArgb(190, 223, 152));

    private RadioSettingsForm? _settingsForm;
    private DispatcherKeys _keys = DispatcherKeys.Load();
    private bool _keysRegistered, _keysAttempted, _editingKeys;
    private string _keyStatus = "";

    private TrainRadioState? _state;
    private DispatcherProtectionState? _area;
    private bool _online;
    private bool _answerPrivate;
    private bool _commandBusy;
    private bool _shuttingDown;
    private bool _hiddenByUser;
    private string _requestKey = "", _stationKey = "", _rangeKey = "", _sourceKey = "", _targetKey = "";
    private string _notice = "";
    private DateTime _noticeUntil;

    public TrainRadioConsole(ServerLink server, RadioSettings settings, Action saveSettings, Func<bool> alarm, bool dispatcher)
    {
        _server = server;
        _settings = settings;
        _saveSettings = saveSettings;
        _alarm = alarm;
        _dispatcher = dispatcher;
        _audio = new TrainRadioAudio(server, settings);
        Text = dispatcher ? "列車無線 — 司令機" : "列車無線";
        _base = new Size(dispatcher ? 1040 : 360, 700);
        var scale = Math.Clamp(dispatcher ? settings.DispatcherScale : settings.DriverScale, MinScale, MaxScale);
        ClientSize = new Size((int)Math.Round(_base.Width * scale), (int)Math.Round(_base.Height * scale));
        FormBorderStyle = FormBorderStyle.Sizable; // 縁をつかんで大きさを変えられる（縦横の比はそのまま）
        MinimumSize = SizeFromClientSize(new Size((int)(_base.Width * MinScale), (int)(_base.Height * MinScale)));
        TopMost = settings.TopMost;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(53, 65, 56);

        _call.Text = dispatcher ? "応答" : "呼出";
        Place(_call, 89, 259, 48, 35);
        Place(_mode, 89, 302, 48, 35);
        Place(_cancel, 89, dispatcher ? 345 : 302, 48, 35);
        _mode.Visible = dispatcher;
        Place(_volume, 87, 145, 49, 49);
        _volume.Value = settings.Volume;
        Place(_settingsButton, 40, 650, 140, 26);
        Place(_pinButton, 186, 650, 134, 26);
        Controls.AddRange([_call, _mode, _cancel, _volume, _settingsButton, _pinButton]);

        if (dispatcher)
        {
            Place(_requests, 390, 112, 290, 108);
            Place(_broadcast, 390, 280, 290, 42);
            Place(_firstStation, 390, 410, 290, 30);
            Place(_lastStation, 390, 465, 290, 30);
            Place(_protectionSwitch, 548, 535, 132, 88);
            Place(_keySettings, 390, 663, 290, 28);
            Place(_protectionSources, 744, 100, 272, 112);
            Place(_confirmAlarm, 744, 218, 272, 34);
            Place(_trainSelect, 744, 345, 272, 30);
            Place(_directCall, 744, 385, 272, 40);
            Place(_ranges, 744, 528, 272, 84);
            Controls.AddRange([_requests, _broadcast, _firstStation, _lastStation, _protectionSwitch, _keySettings,
                _protectionSources, _confirmAlarm, _trainSelect, _directCall, _ranges]);

            _broadcast.Click += async (_, _) =>
            {
                if (_state?.MyCall is { IsBroadcast: true } b) await CommandAsync(Protocol.EndRadioCall, b.Id);
                else await CommandAsync(Protocol.StartRadioBroadcast);
            };
            _protectionSwitch.ToggleRequested += async () =>
            {
                if (_firstStation.SelectedItem is not ProtectionStation a || _lastStation.SelectedItem is not ProtectionStation b) return;
                await CommandAsync(Protocol.SetDispatcherProtection, SelectedRange() == null, a.Id, b.Id);
            };
            _ranges.SelectedIndexChanged += (_, _) =>
            {
                if (_ranges.SelectedItem is not DispatcherProtectionRange r) return;
                SelectStation(_firstStation, r.FirstStationId);
                SelectStation(_lastStation, r.LastStationId);
            };
            _directCall.Click += async (_, _) =>
            {
                if (_state?.Outgoing is { } o) await CommandAsync(Protocol.CancelDispatcherCall, o.Id);
                else if (_trainSelect.SelectedItem is TrainRadioTarget t) await CommandAsync(Protocol.CallRadioTrain, t.Id);
            };
            _keySettings.Click += (_, _) => EditDispatcherKeys();
            _confirmAlarm.Click += (_, _) => ConfirmProtectionAlarm();
        }

        _volume.ValueChanged += value =>
        {
            _settings.Volume = value;
            _saveSettings();
        };
        _settingsButton.Click += (_, _) => OpenSettings();
        _pinButton.Click += (_, _) =>
        {
            TopMost = !TopMost;
            _settings.TopMost = TopMost;
            _saveSettings();
            RefreshPin();
        };
        RefreshPin();
        ResizeEnd += (_, _) =>
        {
            if (_dispatcher) _settings.DispatcherScale = _scale;
            else _settings.DriverScale = _scale;
            _saveSettings();
        };
        ApplyScale();
        _call.Click += async (_, _) => await CallOrAnswerAsync();
        _cancel.Click += async (_, _) => await HangUpAsync();
        _mode.Click += async (_, _) => await ToggleModeAsync();
        MouseClick += async (_, e) =>
        {
            if (!Handset.Contains(ToBase(e.Location))) return;
            if (_state?.Incoming != null) await CallOrAnswerAsync();
            else if (_state?.MyCall != null || _state?.OwnRequestPending == true || _state?.Outgoing != null) await HangUpAsync();
            else await CallOrAnswerAsync();
        };
        MouseMove += (_, e) => Cursor = Handset.Contains(ToBase(e.Location)) ? Cursors.Hand : Cursors.Default;
        _timer.Tick += (_, _) => RefreshRadio();
        _timer.Start();
    }

    // ---- 部品 ----

    private static Bitmap LoadArt(string name)
    {
        using var stream = typeof(TrainRadioConsole).Assembly.GetManifestResourceStream("Gokko.Client.Images." + name)
                           ?? throw new InvalidOperationException("無線機の絵が見つかりません: " + name);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    // ---- 大きさ・最前面 ----

    private const float MinScale = 0.6f, MaxScale = 2.5f;

    /// <summary>部品を、もとの大きさの座標で置く（倍率をかけて置き直す）。</summary>
    private void Place(Control c, int x, int y, int w, int h)
    {
        _layout[c] = new Rectangle(x, y, w, h);
        if (c is ListBox or ComboBox) _fontSizes[c] = c.Font.Size;
        PlaceOne(c);
    }

    private void PlaceOne(Control c)
    {
        var r = _layout[c];
        c.SetBounds(_offset.X + (int)Math.Round(r.X * _scale), _offset.Y + (int)Math.Round(r.Y * _scale),
            (int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale));
        if (_fontSizes.TryGetValue(c, out var size) && Math.Abs(c.Font.Size - size * _scale) > .05f)
        {
            var old = c.Font;
            c.Font = new Font(old.FontFamily, size * _scale, old.Style);
            old.Dispose();
        }

        switch (c)
        {
            case ConsoleButton b: b.Zoom = _scale; break;
            case GuardedProtectionSwitch s: s.Zoom = _scale; break;
        }
    }

    /// <summary>窓の大きさに合わせて倍率を決め、部品を置き直す（縦横の比を保ち、余りは真ん中に寄せる）。</summary>
    private void ApplyScale()
    {
        if (_base.Width == 0 || ClientSize.Width == 0 || ClientSize.Height == 0) return;
        _scale = Math.Clamp(Math.Min(ClientSize.Width / (float)_base.Width, ClientSize.Height / (float)_base.Height), MinScale / 2, MaxScale);
        _offset = new Point((ClientSize.Width - (int)Math.Round(_base.Width * _scale)) / 2, (ClientSize.Height - (int)Math.Round(_base.Height * _scale)) / 2);
        SuspendLayout();
        foreach (var c in _layout.Keys) PlaceOne(c);
        ResumeLayout();
        Invalidate();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        ApplyScale();
    }

    /// <summary>窓の座標 → もとの大きさの座標。</summary>
    private Point ToBase(Point p) => new((int)((p.X - _offset.X) / _scale), (int)((p.Y - _offset.Y) / _scale));

    private void RefreshPin()
    {
        _pinButton.Text = TopMost ? "最前面：固定中" : "最前面に固定";
        _pinButton.Accent = TopMost ? Color.FromArgb(219, 193, 69) : Color.FromArgb(160, 172, 140);
    }

    // 縁をつかんで大きさを変えるとき、縦横の比を保つ
    private const int WM_SIZING = 0x0214;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private void KeepAspect(ref Message m)
    {
        var r = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeRect>(m.LParam);
        var frame = SizeFromClientSize(Size.Empty);
        var w = r.Right - r.Left - frame.Width;
        var h = r.Bottom - r.Top - frame.Height;
        var edge = m.WParam.ToInt32();
        if (edge is 3 or 6) w = (int)Math.Round(h * _base.Width / (float)_base.Height); // 上・下の縁: 高さに合わせて幅を決める
        else h = (int)Math.Round(w * _base.Height / (float)_base.Width); // 左右の縁・角: 幅に合わせて高さを決める
        if (edge is 1 or 4 or 7) r.Left = r.Right - w - frame.Width; else r.Right = r.Left + w + frame.Width;
        if (edge is 3 or 4 or 5) r.Top = r.Bottom - h - frame.Height; else r.Bottom = r.Top + h + frame.Height;
        System.Runtime.InteropServices.Marshal.StructureToPtr(r, m.LParam, false);
        m.Result = 1;
    }

    private static ListBox DarkList(Color back, Color fore) => new()
    {
        BorderStyle = BorderStyle.None, BackColor = back, ForeColor = fore, Font = new Font("Yu Gothic UI", 10f),
        IntegralHeight = false, HorizontalScrollbar = true
    };

    private static ComboBox StationSelector() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Yu Gothic UI", 11f),
        BackColor = Color.FromArgb(29, 43, 33), ForeColor = Color.FromArgb(190, 223, 152)
    };

    // ---- 開く・閉じる ----

    /// <summary>
    /// 無線機を出す。元の画面を持ち主にはしない（元の画面と無線機を、それぞれ別々に最小化できるように。タスクバーにも別々に出る）。
    /// 最小化していたら元に戻す。
    /// </summary>
    public void OpenConsole(IWin32Window? owner)
    {
        _hiddenByUser = false;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>F6: 無線機を出して、司令を呼び出す。司令員からの個別呼出が来ていたら応答する。</summary>
    public async Task CallFromKeyAsync(IWin32Window owner)
    {
        if (_shuttingDown || _dispatcher) return;
        OpenConsole(owner);
        await CallOrAnswerAsync();
    }

    private void OpenSettings() => OpenSoundSettings();

    /// <summary>
    /// 音の設定（マイクの選択・感度・ノイズゲートなど）を開く。列車無線と放送装置で共通の設定。
    /// 放送装置のポップ（最前面）から開くときは topMost にする（ポップの後ろに隠れないように）。
    /// </summary>
    public void OpenSoundSettings(bool topMost = false)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.TopMost = topMost;
            if (!_settingsForm.Visible) _settingsForm.Show();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new RadioSettingsForm(_settings, _audio, _saveSettings) { TopMost = topMost };
        if (Visible) _settingsForm.Show(this);
        else _settingsForm.Show(); // 無線機を閉じていても開けるように
    }

    // ---- 操作 ----

    /// <summary>呼出キー: 司令員は選んだ呼出（なければ先頭）に応答。運転士・車掌は、個別呼出が来ていれば応答し、なければ司令を呼び出す。</summary>
    private async Task CallOrAnswerAsync()
    {
        if (_dispatcher)
        {
            var request = _requests.SelectedItem as TrainRadioRequest ?? _state?.Requests.FirstOrDefault();
            if (request != null) await CommandAsync(Protocol.AnswerRadioCall, request.Id, _answerPrivate);
        }
        else if (_state?.Incoming is { } offer) await CommandAsync(Protocol.AcceptDispatcherCall, offer.Id);
        else if (_state?.MyCall == null && BroadcastHeard() is { } b) await CommandAsync(Protocol.RespondRadioBroadcast, b.Id);
        else if (_state?.MyCall == null && _state?.OwnRequestPending != true) await CommandAsync(Protocol.RequestRadioCall);
    }

    /// <summary>聞こえている一斉通告（なければ null）。</summary>
    private TrainRadioCall? BroadcastHeard() => _state?.Audible.FirstOrDefault(c => c.IsBroadcast);

    /// <summary>一斉通告に応答している人の名前（「1040列車（運転士）ほか2人」など）。いなければ空。</summary>
    private static string RespondersText(TrainRadioCall call) => call.Responders.Count switch
    {
        0 => "",
        1 => call.Responders[0].Label,
        _ => $"{call.Responders[0].Label} ほか{call.Responders.Count - 1}人"
    };

    /// <summary>取消キー: 個別呼出の取消、通話の終了、呼出の取消。</summary>
    private async Task HangUpAsync()
    {
        if (_state?.Incoming is { } inc) await CommandAsync(Protocol.CancelDispatcherCall, inc.Id);
        else if (_state?.Outgoing is { } outg) await CommandAsync(Protocol.CancelDispatcherCall, outg.Id);
        else if (_state?.MyCall is { } call) await CommandAsync(Protocol.EndRadioCall, call.Id);
        else if (_state?.OwnRequestPending == true) await CommandAsync(Protocol.CancelRadioCall);
    }

    /// <summary>共通／個別キー（司令員）: 通話中なら今の通話を切り替え、待受なら次に応答するときの方式を選ぶ。一斉通告中は切り替えない。</summary>
    private async Task ToggleModeAsync()
    {
        if (!_dispatcher || !_online) return;
        if (_state?.MyCall is { IsBroadcast: true }) return;
        if (_state?.MyCall is { } call) await CommandAsync(Protocol.ChangeRadioMode, call.Id, !call.IsPrivate);
        else _answerPrivate = !_answerPrivate;
    }

    private async Task CommandAsync(string method, params object?[] args)
    {
        if (_commandBusy || _shuttingDown) return;
        _commandBusy = true;
        try
        {
            var result = await _server.RadioCommandAsync(method, args);
            _notice = result.Message;
            _noticeUntil = DateTime.UtcNow.AddSeconds(5);
            if (!result.Ok) System.Media.SystemSounds.Beep.Play();
        }
        finally
        {
            _commandBusy = false;
        }
    }

    // ---- 司令のキー（応答・共通／個別） ----

    private void ReleaseDispatcherKeys()
    {
        if (IsHandleCreated) DispatcherKeys.Release(Handle);
        _keysRegistered = _keysAttempted = false;
    }

    /// <summary>司令として参加している間だけ、キーを登録する（無線機を閉じた・切断・設定の入力中は外す）。</summary>
    private void RefreshDispatcherKeys()
    {
        var wanted = _dispatcher && _online && !_hiddenByUser && !_editingKeys && !_shuttingDown;
        if (!wanted)
        {
            if (_keysAttempted) ReleaseDispatcherKeys();
            return;
        }

        if (_keysAttempted || !IsHandleCreated) return;
        _keysAttempted = true;
        _keysRegistered = _keys.TryRegister(Handle);
        _keyStatus = _keysRegistered ? "" : "キーを登録できません。［キー設定］で変えてください";
    }

    private void EditDispatcherKeys()
    {
        if (_editingKeys) return;
        _editingKeys = true;
        ReleaseDispatcherKeys();
        try
        {
            using var dialog = new DispatcherKeyDialog(_keys);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var candidate = dialog.Selection;
            if (!candidate.TryRegister(Handle))
            {
                MessageBox.Show(this, "そのキーは、ほかのアプリか Windows が使っています。別のキーを選んでください（設定は変えていません）。", "キーを登録できません");
                return;
            }

            DispatcherKeys.Release(Handle);
            try
            {
                candidate.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "設定を保存できませんでした。前の設定のままにします。\n" + ex.Message, "保存できません");
                return;
            }

            _keys = candidate;
        }
        finally
        {
            _editingKeys = false;
            RefreshDispatcherKeys();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SIZING)
        {
            KeepAspect(ref m);
            return;
        }

        if (m.Msg == TrainRadioHotKey.Message && m.WParam.ToInt32() is DispatcherKeys.AnswerId or DispatcherKeys.ModeId)
        {
            if (_keysRegistered && !_editingKeys && !_commandBusy && !_shuttingDown && _online)
            {
                if (m.WParam.ToInt32() == DispatcherKeys.AnswerId && _call.Enabled) _ = CallOrAnswerAsync();
                else if (m.WParam.ToInt32() == DispatcherKeys.ModeId && _mode.Enabled) _ = ToggleModeAsync();
            }

            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseDispatcherKeys();
        base.OnHandleDestroyed(e);
    }

    // ---- 表示 ----

    private void RefreshRadio()
    {
        if (_shuttingDown) return;
        _online = _server.IsJoined && _server.HasFreshState;
        _state = _online ? _server.TrainRadio : null;
        UpdateProtectionMonitor();
        // 無線機を閉じて（隠して）いても、聞こえる通話・一斉通告・呼出音は流す（×で閉じたときは自分の通話だけ終える）
        _audio.Synchronize(_state, _online, _alarm() || _monitorUnconfirmed);
        if (!_volume.Capture && Math.Abs(_volume.Value - _settings.Volume) > 0.001f) _volume.Value = _settings.Volume; // ほかの画面で変えた受話音量
        RefreshDispatcherKeys();
        var my = _state?.MyCall;
        var requests = _state?.Requests ?? new();

        if (_dispatcher)
        {
            var key = string.Join("|", requests.Select(r => r.Id + ":" + r.Label));
            if (key != _requestKey)
            {
                var selected = (_requests.SelectedItem as TrainRadioRequest)?.Id;
                _requests.BeginUpdate();
                _requests.Items.Clear();
                foreach (var r in requests) _requests.Items.Add(r);
                var index = requests.FindIndex(r => r.Id == selected);
                if (requests.Count > 0) _requests.SelectedIndex = index >= 0 ? index : 0;
                _requests.EndUpdate();
                _requestKey = key;
            }

            _requests.Visible = requests.Count > 0;
            var busy = my != null || _state?.Outgoing != null;
            _call.Enabled = _online && !_commandBusy && !busy && requests.Count > 0;
            var showPrivate = my is { IsBroadcast: false } ? my.IsPrivate : _answerPrivate;
            _mode.Text = showPrivate ? "個別" : "共通";
            _mode.Accent = showPrivate ? Color.FromArgb(194, 139, 66) : Color.FromArgb(219, 193, 69);
            _mode.Enabled = _online && !_commandBusy && my is not { IsBroadcast: true };
            _cancel.Text = my != null ? "終了" : "取消";
            _cancel.Enabled = _online && !_commandBusy && (my != null || _state?.Outgoing != null);

            var broadcasting = my is { IsBroadcast: true };
            var otherBroadcast = _state?.Audible.Any(c => c.IsBroadcast && c.Id != my?.Id) == true;
            _broadcast.Text = broadcasting ? "通告を終える" : "一斉通告";
            _broadcast.Accent = broadcasting ? Color.FromArgb(214, 82, 58) : Color.FromArgb(160, 172, 140);
            _broadcast.Enabled = _online && !_commandBusy && (broadcasting || (!busy && !otherBroadcast));
            _keySettings.Text = $"キー設定：応答 {DispatcherKeys.Label(_keys.Answer)} / 切替 {DispatcherKeys.Label(_keys.Mode)}";
            RefreshMonitor(busy);
            RefreshProtection();
        }
        else
        {
            var incoming = _state?.Incoming != null;
            var canRespond = my == null && BroadcastHeard() != null; // 一斉通告の間は、応答して話せる
            _call.Text = incoming || canRespond ? "応答" : "呼出";
            _call.Enabled = _online && !_commandBusy && (incoming || canRespond || (my == null && _state?.DispatcherCount > 0 && _state?.OwnRequestPending != true));
            _cancel.Enabled = _online && !_commandBusy && (incoming || my != null || _state?.OwnRequestPending == true);
        }

        Invalidate();
    }

    /// <summary>
    /// 司令機の防護受報: 運転士の F5 の発報があれば警報音を鳴らす（司令員の位置にかかわらず）。
    /// 「確認」した発報は音を止める。解除されたら確認も忘れる（同じ列車がまた発報したら鳴る）。抜けたら確認を全部忘れる。
    /// </summary>
    private void UpdateProtectionMonitor()
    {
        if (!_dispatcher) return;
        var sources = _online ? _server.State?.ProtectionSources ?? new() : new();
        var keys = sources.Select(s => s.ToString()).ToHashSet();
        _confirmedSources.IntersectWith(keys);
        _monitorActive = keys.Count > 0;
        _monitorUnconfirmed = keys.Any(k => !_confirmedSources.Contains(k));
        _confirmAlarm.Enabled = _monitorUnconfirmed;
        var sound = _monitorUnconfirmed && !_hiddenByUser;
        if (sound && _monitorAudio == null && !_monitorAudioFailed)
        {
            try { _monitorAudio = new ProtectionRadioAudio(); }
            catch
            {
                _monitorAudioFailed = true;
                _notice = "防護無線の警報音を読み込めませんでした（ランプと一覧だけで知らせます）";
                _noticeUntil = DateTime.UtcNow.AddSeconds(8);
            }
        }

        _monitorAudio?.Update(sound);
    }

    /// <summary>「確認（警報音停止）」: いまの発報の警報音を止める（発報は解除しない）。</summary>
    private void ConfirmProtectionAlarm()
    {
        foreach (var s in _server.State?.ProtectionSources ?? new()) _confirmedSources.Add(s.ToString());
        UpdateProtectionMonitor();
    }

    /// <summary>列車防護の受報一覧と、司令からの個別呼出。</summary>
    private void RefreshMonitor(bool busy)
    {
        var sources = _online ? _server.State?.ProtectionSources ?? new() : new();
        var key = string.Join("|", sources.Select(s => s.ToString()));
        if (key != _sourceKey)
        {
            _protectionSources.BeginUpdate();
            _protectionSources.Items.Clear();
            foreach (var s in sources) _protectionSources.Items.Add(s);
            _protectionSources.EndUpdate();
            _sourceKey = key;
        }

        var targets = _state?.Targets ?? new();
        key = string.Join("|", targets.Select(t => t.Id + ":" + t.Label));
        if (key != _targetKey)
        {
            var selected = (_trainSelect.SelectedItem as TrainRadioTarget)?.Id;
            _trainSelect.BeginUpdate();
            _trainSelect.Items.Clear();
            foreach (var t in targets) _trainSelect.Items.Add(t);
            if (targets.Count > 0) _trainSelect.SelectedIndex = Math.Max(0, targets.FindIndex(t => t.Id == selected));
            _trainSelect.EndUpdate();
            _targetKey = key;
        }

        var calling = _state?.Outgoing != null;
        _directCall.Text = calling ? "個別呼出を取り消す" : "個別呼出";
        _directCall.Accent = calling ? Color.FromArgb(214, 82, 58) : Color.FromArgb(160, 172, 140);
        _directCall.Enabled = _online && !_commandBusy && (calling || (!busy && _trainSelect.SelectedIndex >= 0));
        _trainSelect.Enabled = _online && !_commandBusy && !busy;
    }

    private void RefreshProtection()
    {
        _area = _online ? _server.State?.DispatcherProtection : null;
        var stations = _area?.Stations ?? new();
        var key = string.Join("|", stations.Select(s => s.Id + ":" + s.Name));
        if (key != _stationKey)
        {
            var first = (_firstStation.SelectedItem as ProtectionStation)?.Id;
            var last = (_lastStation.SelectedItem as ProtectionStation)?.Id;
            _firstStation.Items.Clear();
            _lastStation.Items.Clear();
            foreach (var s in stations)
            {
                _firstStation.Items.Add(s);
                _lastStation.Items.Add(s);
            }

            if (stations.Count > 0)
            {
                if (!SelectStation(_firstStation, first)) _firstStation.SelectedIndex = 0;
                if (!SelectStation(_lastStation, last)) _lastStation.SelectedIndex = Math.Min(1, stations.Count - 1);
            }

            _stationKey = key;
        }

        var ranges = _area?.Active ?? new();
        var rkey = string.Join("|", ranges.Select(r => r.ToString()));
        if (rkey != _rangeKey)
        {
            _ranges.BeginUpdate();
            _ranges.Items.Clear();
            foreach (var r in ranges) _ranges.Items.Add(r);
            _ranges.EndUpdate();
            _rangeKey = rkey;
        }

        _firstStation.Enabled = _lastStation.Enabled = _online;
        _protectionSwitch.Enabled = _online && stations.Count > 0 && !_commandBusy;
        _protectionSwitch.Active = SelectedRange() != null;
    }

    private static bool SelectStation(ComboBox box, string? id)
    {
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is ProtectionStation s && s.Id == id)
            {
                box.SelectedIndex = i;
                return true;
            }

        return false;
    }

    /// <summary>選んでいる2駅の区間防護が発報中なら、それ（逆順に選んでも同じ）。</summary>
    private DispatcherProtectionRange? SelectedRange()
    {
        var a = (_firstStation.SelectedItem as ProtectionStation)?.Id;
        var b = (_lastStation.SelectedItem as ProtectionStation)?.Id;
        return _area?.Active.FirstOrDefault(r => (r.FirstStationId == a && r.LastStationId == b) || (r.FirstStationId == b && r.LastStationId == a));
    }

    /// <summary>液晶の大きな文字。</summary>
    /// <summary>
    /// 司令機の液晶に出す相手の列番（自分の通話の相手・個別呼出の相手）。運転士は「1040列車」、車掌は「1040 車掌」。
    /// 運転士・車掌の無線機や、相手がいないときは null。
    /// </summary>
    private string? LcdPartner()
    {
        if (!_dispatcher || !_online) return null;
        var label = _state?.MyCall?.Caller?.Label ?? _state?.Outgoing?.Label;
        if (string.IsNullOrEmpty(label)) return null;
        var cut = label.IndexOf('（');
        var train = cut >= 0 ? label[..cut] : label; // 「1040列車」「列番未設定」
        return label.Contains("（車掌）") ? (train.EndsWith("列車") ? train[..^2] : train) + " 車掌" : train;
    }

    private string StatusText()
    {
        var my = _state?.MyCall;
        if (!_online) return "未接続";
        if (_state?.Incoming != null) return "司令着信";
        if (_state?.Outgoing != null) return "列車呼出";
        if (my is { IsBroadcast: true }) return _dispatcher ? "一斉通告" : "通告応答";
        if (_dispatcher)
        {
            if (my != null) return my.IsPrivate ? "個別通話" : "共通通話";
            if (_state?.Requests.Count > 0) return "呼出あり";
            if (_state?.Audible.Any(c => !c.IsPrivate) == true) return "共通受信";
            return _state?.Audible.Count > 0 ? "個別傍受" : "待受"; // ほかの司令員の個別通話を聞いている
        }

        // 運転士・車掌: 聞こえている通話の種類（自分が話す通話でも「受信」と出す）
        if (my is { IsPrivate: true }) return "個別受信";
        if (_state?.Audible.Count > 0) return "共通受信";
        return _state?.OwnRequestPending == true ? "呼出中" : "待受";
    }

    /// <summary>下の帯の文字（相手・聞こえている通話など）。</summary>
    private string DetailText()
    {
        var my = _state?.MyCall;
        if (_audio.Error.Length > 0) return _audio.Error;
        if (DateTime.UtcNow < _noticeUntil) return _notice;
        if (!_online) return "サーバーに接続してください";
        if (_state?.Incoming is { } inc) return $"{inc.Label} から個別呼出。応答キー・受話器・F6 で応答";
        if (_state?.Outgoing is { } outg) return $"{outg.Label} を呼び出しています";
        if (_state?.PhoneLinked == true) return my != null ? "スマホで通話中（PC のマイクは使いません）" : "スマホの受話器につないでいます";
        if (my is { IsBroadcast: true })
            return !_dispatcher ? "一斉通告に応答中（全員に聞こえます。取消で送話を終えます）"
                : my.Responders.Count > 0 ? $"全員に聞こえています　応答: {RespondersText(my)}" : "全員に聞こえています";
        if (my != null) return (_dispatcher ? my.Caller!.Label : my.Dispatcher.Label) + (my.IsPrivate ? "（個別）" : "（共通。全員に聞こえます）");
        var other = BroadcastHeard() ?? _state?.Audible.FirstOrDefault();
        if (other != null)
        {
            if (!other.IsBroadcast) return $"{other.Dispatcher.Label} ⇔ {other.Caller!.Label}" + (other.IsPrivate ? "（個別）" : "");
            var reply = other.Responders.Count > 0 ? $"　応答: {RespondersText(other)}" : "";
            return _dispatcher ? $"一斉通告: {other.Dispatcher.Label}{reply}" : $"一斉通告: {other.Dispatcher.Label}　応答キー・F6 で話せます";
        }
        if (!_dispatcher && _state?.DispatcherCount == 0) return "司令員が参加していません";
        return "受話器をクリック：呼出／終話";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // ここから下は、もとの大きさの座標で描く（倍率は Graphics にかける。文字は DrawText で合わせる）
        _paintTransform?.Dispose();
        _paintTransform = g.Transform;
        g.TranslateTransform(_offset.X, _offset.Y);
        g.ScaleTransform(_scale, _scale);
        g.DrawImage(_face, new Rectangle(0, 0, 360, 700));
        if (!_dispatcher)
        {
            // 運転士・車掌のキーは「呼出」「取消」の2つ。3段目のキーの穴を隠す
            using var plate = new SolidBrush(Color.FromArgb(57, 63, 59));
            g.FillRectangle(plate, 84, 340, 59, 47);
        }

        using var small = new Font("Yu Gothic UI", 8f * _scale, FontStyle.Bold);
        using var mid = new Font("Yu Gothic UI", 10f * _scale, FontStyle.Bold);
        using var big = new Font("Yu Gothic UI", 14f * _scale, FontStyle.Bold);
        var speaking = _online && _state?.MyCall != null && _audio.MicrophoneOpen;

        // 液晶
        DrawText(g, _dispatcher ? "司令" : "列車", small, Lcd, new(180, 120, 42, 18));
        DrawText(g, _online ? "電源" : "断", small, _online ? Lcd : Color.Gray, new(231, 120, 34, 18));
        var partner = LcdPartner();
        if (partner == null)
            DrawText(g, StatusText(), mid, LcdText, new(179, 145, 85, 25));
        else
        {
            // 司令機: 通話・呼出の相手の列番を、状態の下に出す
            DrawText(g, StatusText(), mid, LcdText, new(179, 139, 85, 22));
            DrawText(g, partner, small, LcdText, new(178, 160, 88, 18));
        }

        DrawText(g, speaking ? (_audio.Transmitting ? "送話 ●" : "送話 ○") : "受話", small, Lcd, new(180, 178, 80, 18));

        // ランプ: 防護受報（司令機は、運転士の F5 が1件でもあれば）・送話・受話・呼出
        // 司令機: 確認していない発報があれば速く点滅、確認ずみだけなら点けたまま
        var blink = Environment.TickCount64 % 250 < 125;
        Lamp(g, new(98, 76, 24, 24), _online && ((_alarm() || _monitorUnconfirmed) ? blink : _monitorActive), Color.Red);
        Lamp(g, new(120, 438, 10, 10), speaking && _audio.Transmitting, Color.LimeGreen);
        Lamp(g, new(120, 468, 10, 10), _audio.ReceiveLevel > .01f, Color.LimeGreen);
        var ringing = _online && (_state?.OwnRequestPending == true || _state?.Requests.Count > 0 || _state?.Incoming != null || _state?.Outgoing != null);
        Lamp(g, new(250, 352, 8, 8), ringing && Environment.TickCount64 % 1000 < 500, Color.Orange);
        DrawText(g, DetailText(), small, Color.FromArgb(32, 52, 37), new(18, 680, 324, 18));

        if (_dispatcher) PaintDispatcherPanel(g, small, mid, big);
    }

    private void PaintDispatcherPanel(Graphics g, Font small, Font mid, Font big)
    {
        using var line = new Pen(Color.FromArgb(117, 135, 109));
        g.DrawRectangle(line, 376, 34, 320, 620);
        g.DrawRectangle(line, 728, 34, 304, 620);
        var green = Lcd;
        var my = _state?.MyCall;

        // 司令操作盤
        DrawText(g, "司令操作盤", big, Beige, new(390, 49, 280, 32));
        DrawText(g, $"呼出待ち  /  左の応答キーで応答（{(_answerPrivate ? "個別" : "共通")}で応答）", small, Muted, new(390, 85, 300, 22));
        if (!_requests.Visible)
            DrawText(g, my is { IsBroadcast: true } ? "全列車へ一斉通告中" : my != null ? "通話中" : _state?.Outgoing != null ? "個別呼出中" : "呼出なし",
                mid, green, new(390, 135, 290, 40));
        DrawText(g, my is { IsBroadcast: true } ? "送話中：もう一度押すと終わります"
            : "全員にチャイムを鳴らして通告", small, Muted, new(390, 245, 290, 25));
        g.DrawLine(line, 390, 344, 680, 344);
        DrawText(g, "区間防護", mid, Beige, new(390, 357, 230, 28));
        DrawText(g, "開始駅", small, Muted, new(390, 388, 200, 22));
        DrawText(g, "終了駅", small, Muted, new(390, 444, 200, 22));
        var count = _area?.Active.Count ?? 0;
        Lamp(g, new(663, 365, 14, 14), count > 0, Color.Red);
        DrawText(g, SelectedRange() != null ? "選んだ区間は発報中" : "上下線 / 両端の駅を含む", small, Beige, new(390, 505, 290, 25));
        DrawText(g, SelectedRange() != null ? "スイッチで解除" : "保護蓋を開いて発報", small, Muted, new(390, 558, 150, 24));
        DrawText(g, "区間はいくつでも", small, Muted, new(390, 584, 150, 24));
        DrawText(g, _keyStatus, small, Color.Orange, new(390, 628, 290, 20));

        // 列車防護の受報一覧・司令からの個別呼出・区間防護の発報中の一覧
        var sources = _server.State?.ProtectionSources.Count ?? 0;
        DrawText(g, "列車防護 受報一覧", mid, Color.Salmon, new(744, 50, 272, 28));
        DrawText(g, sources > 0 ? $"発報中 {sources}件  /  列車・発報した場所" : "いまの発報なし", small, Muted, new(744, 80, 272, 20));
        DrawText(g, _monitorUnconfirmed ? "確認で警報音を止めます（発報は解除しません）" : _monitorActive ? "確認ずみ（新しい発報でまた鳴ります）" : "解除・切断で一覧から消えます",
            small, Muted, new(744, 258, 272, 20));
        DrawText(g, "司令から個別呼出", mid, Beige, new(744, 296, 272, 28));
        DrawText(g, "列車を選んで呼び出す（相手が応答したら個別通話）", small, Muted, new(744, 324, 272, 18));
        DrawText(g, _state?.Outgoing is { } o ? $"{o.Label} を呼び出し中" : "応答がなければ 30秒で取り消し", small, Beige, new(744, 432, 272, 20));
        DrawText(g, "区間防護 発報中", mid, Beige, new(744, 474, 272, 24));
        DrawText(g, count > 0 ? "押すと、開始駅・終了駅に入ります" : "発報中の区間はありません", small, Muted, new(744, 504, 272, 20));
    }

    /// <summary>文字を描く。rect はもとの大きさの座標（TextRenderer は Graphics の倍率を使わないので、窓の座標に直して描く。文字の大きさは font で合わせる）。</summary>
    private void DrawText(Graphics g, string text, Font font, Color color, Rectangle rect)
    {
        var client = Rectangle.FromLTRB(_offset.X + (int)Math.Round(rect.Left * _scale), _offset.Y + (int)Math.Round(rect.Top * _scale),
            _offset.X + (int)Math.Round(rect.Right * _scale), _offset.Y + (int)Math.Round(rect.Bottom * _scale));
        var scaled = g.Transform;
        g.Transform = _paintTransform ?? new Matrix(); // 倍率をかける前の状態に戻して、窓の座標で描く
        TextRenderer.DrawText(g, text, font, client, color, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        g.Transform = scaled;
        scaled.Dispose();
    }

    private Matrix? _paintTransform;

    private static void Lamp(Graphics g, Rectangle rect, bool on, Color color)
    {
        using var brush = new SolidBrush(on ? color : Color.FromArgb(25, 37, 24));
        g.FillEllipse(brush, rect);
        using var edge = new Pen(Color.FromArgb(100, 113, 94));
        g.DrawEllipse(edge, rect);
    }

    // ---- 終わり ----

    /// <summary>サーバーから抜けたとき: マイクと受話を止める。</summary>
    public void StopForDisconnect() => _audio.Suspend();

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_shuttingDown && e.CloseReason == CloseReason.UserClosing)
        {
            // ×は隠すだけ。自分の通話・呼出・個別呼出は終える（元の画面から開き直せる）。司令のキーも外す
            e.Cancel = true;
            _hiddenByUser = true;
            ReleaseDispatcherKeys();
            Hide();
            await HangUpAsync();
            return;
        }

        base.OnFormClosing(e);
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        ReleaseDispatcherKeys();
        _timer.Stop();
        _settingsForm?.Close();
        _audio.Dispose();
        _monitorAudio?.Dispose();
        _monitorAudio = null;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shuttingDown = true;
            _timer.Dispose();
            _audio.Dispose();
            _face.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>無線機のキー。</summary>
internal sealed class ConsoleButton : Control
{
    private bool _pressed;
    private Color _accent = Color.FromArgb(160, 172, 140);
    public bool Small { get; set; }

    /// <summary>窓の倍率（文字の大きさに使う）。</summary>
    public float Zoom { get => _zoom; set { if (Math.Abs(_zoom - value) < .001f) return; _zoom = value; Invalidate(); } }
    private float _zoom = 1f;

    public Color Accent
    {
        get => _accent;
        set
        {
            if (_accent == value) return;
            _accent = value;
            Invalidate();
        }
    }

    public ConsoleButton()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        Focus();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_pressed && e.KeyCode is Keys.Space or Keys.Enter)
        {
            _pressed = false;
            Invalidate();
            OnClick(EventArgs.Empty);
        }

        base.OnKeyUp(e);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var dark = new SolidBrush(Color.FromArgb(20, 28, 21));
        g.FillRectangle(dark, ClientRectangle);
        var rect = new Rectangle(2, _pressed ? 4 : 1, Width - 5, Height - 6);
        var c = Enabled ? Accent : Color.FromArgb(88, 99, 83);
        using var fill = new LinearGradientBrush(rect, ControlPaint.Light(c, .25f), ControlPaint.Dark(c, .25f), 90);
        g.FillRectangle(fill, rect);
        using var edge = new Pen(ControlPaint.Light(c));
        g.DrawRectangle(edge, rect);
        using var font = new Font("Yu Gothic UI", (Small ? 8 : 13) * _zoom, FontStyle.Bold);
        TextRenderer.DrawText(g, Text, font, rect, Color.FromArgb(25, 34, 23), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(rect, -4, -4));
    }
}

/// <summary>受話音量のつまみ（上下にドラッグ・ホイール・矢印キー）。大きさに合わせて描く。</summary>
internal sealed class VolumeKnob : Control
{
    private float _value = .8f;
    private int _dragY;
    public event Action<float>? ValueChanged;

    public VolumeKnob()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(33, 38, 35);
        TabStop = true;
        AccessibleName = "受話音量";
    }

    public float Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    private void SetValue(float v)
    {
        _value = Math.Clamp(v, 0, 1);
        ValueChanged?.Invoke(_value);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Capture = true;
        _dragY = e.Y;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Capture) return;
        SetValue(_value + (_dragY - e.Y) / 100f);
        _dragY = e.Y;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        SetValue(_value + Math.Sign(e.Delta) * .05f);
        base.OnMouseWheel(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Right) SetValue(_value + .05f);
        if (e.KeyCode is Keys.Down or Keys.Left) SetValue(_value - .05f);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(Width / 109f, Height / 104f);
        var center = new PointF(54, 49);
        for (var i = 0; i <= 10; i++)
        {
            var a = (135 + i * 27) * Math.PI / 180;
            using var p = new Pen(Color.FromArgb(155, 168, 141), 2);
            g.DrawLine(p, center.X + (float)Math.Cos(a) * 40, center.Y + (float)Math.Sin(a) * 40,
                center.X + (float)Math.Cos(a) * 45, center.Y + (float)Math.Sin(a) * 45);
        }

        using var shadow = new SolidBrush(Color.FromArgb(18, 26, 18));
        g.FillEllipse(shadow, 20, 17, 72, 72);
        using var body = new LinearGradientBrush(new Rectangle(18, 13, 72, 72), Color.FromArgb(85, 99, 77), Color.FromArgb(25, 36, 24), 60);
        g.FillEllipse(body, 18, 13, 72, 72);
        using var edge = new Pen(Color.FromArgb(126, 143, 112));
        g.DrawEllipse(edge, 18, 13, 72, 72);
        var angle = (135 + _value * 270) * Math.PI / 180;
        using var needle = new Pen(Color.FromArgb(224, 225, 184), 4);
        g.DrawLine(needle, center.X + (float)Math.Cos(angle) * 22, center.Y + (float)Math.Sin(angle) * 22,
            center.X + (float)Math.Cos(angle) * 32, center.Y + (float)Math.Sin(angle) * 32);
    }
}
