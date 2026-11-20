using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 司令として参加したときの元の画面（TRAIN CREW にはつながない）。サーバーへの接続と、司令機（列車無線）を開くだけ。
/// 司令機では、運転士・車掌の呼出への応答、一斉通告、区間防護を扱う。
/// </summary>
public sealed class DispatcherForm : Form
{
    private static readonly Color Back = Color.FromArgb(246, 247, 249);
    private static readonly Color Muted = Color.FromArgb(110, 117, 128);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);

    private readonly ClientConfig _config;
    private readonly CrewSession _crew;
    private readonly ServerLink _server = new();
    private readonly TrainRadioConsole _console;
    private readonly ExtensionPhoneForm _phone; // 内線電話（卓上電話機）。駅・列車とかけ合う。全駅の一斉呼出
    private readonly TextBox _passwordBox = new() { UseSystemPasswordChar = true, Width = 220 };
    private readonly Button _connectButton = new() { AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(14, 4, 14, 4) };
    private readonly Label _status = new() { AutoSize = true, Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold) };
    private readonly Label _detail = new() { AutoSize = true, ForeColor = Muted };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private bool _closing;

    /// <summary>［モード選択へ］で閉じたか（閉じたあと、ログインしたまま、モードを選ぶ画面に戻る）。</summary>
    public bool ReturnToStart { get; private set; }

    public DispatcherForm(ClientConfig config, CrewSession crew)
    {
        _config = config;
        _crew = crew;
        Text = $"特発共有クライアント（司令）  ID:{crew.Id}";
        Font = new Font("Yu Gothic UI", 10f);
        BackColor = Back;
        ClientSize = new Size(460, 400);
        MinimumSize = new Size(420, 280);
        StartPosition = FormStartPosition.CenterScreen;
        _passwordBox.Text = config.Password;
        if (Environment.GetEnvironmentVariable("GOKKO_PASSWORD") is { } pw) _passwordBox.Text = pw; // 開発用

        _console = new TrainRadioConsole(_server, config.Radio, config.Save, () => false, dispatcher: true);
        _phone = new ExtensionPhoneForm(_server, config.Radio, keitai: false);

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 16) };
        Controls.Add(page);
        page.Controls.Add(new Label { Text = "司令（列車無線）", AutoSize = true, Font = new Font("Yu Gothic UI", 14f, FontStyle.Bold) });
        page.Controls.Add(new Label
        {
            Text = "運転士・車掌からの呼出への応答、全列車への一斉通告、区間防護を扱います。TRAIN CREW は起動しなくてかまいません。",
            AutoSize = true, MaximumSize = new Size(410, 0), ForeColor = Muted, Margin = new Padding(0, 4, 0, 12)
        });

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        row.Controls.Add(new Label { Text = "合言葉", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        row.Controls.Add(_passwordBox);
        row.Controls.Add(_connectButton);
        page.Controls.Add(row);
        page.Controls.Add(_status);
        page.Controls.Add(_detail);

        var open = new Button { Text = "司令機を開く", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Padding = new Padding(16, 6, 16, 6), Margin = new Padding(0, 14, 0, 0) };
        open.FlatAppearance.BorderSize = 0;
        open.Click += (_, _) => _console.OpenConsole(this);
        page.Controls.Add(open);
        var phone = new Button { Text = "内線電話を開く", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Accent, Padding = new Padding(16, 4, 16, 4), Margin = new Padding(0, 8, 0, 0) };
        phone.FlatAppearance.BorderColor = Accent;
        phone.Click += (_, _) => _phone.OpenPhone();
        page.Controls.Add(phone);
        // モードを選ぶ画面に戻る（クライアントを起動し直さずに、運転士・車掌などを選び直す）
        var back = new Button { Text = "モード選択へ", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Muted, Padding = new Padding(16, 4, 16, 4), Margin = new Padding(0, 8, 0, 0) };
        back.FlatAppearance.BorderColor = Muted;
        back.Click += (_, _) =>
        {
            if (_closing) return;
            if (_server.Status is ServerStatus.Connecting or ServerStatus.Connected
                && MessageBox.Show(this, "サーバーとの接続を切って、モードを選ぶ画面に戻りますか？\n（ログインはそのままです）",
                    "モード選択へ", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            ReturnToStart = true;
            Close();
        };
        page.Controls.Add(back);

        _connectButton.FlatAppearance.BorderColor = Accent;
        _connectButton.ForeColor = Accent;
        _connectButton.Click += async (_, _) => await ToggleConnectionAsync();
        _timer.Tick += (_, _) => UpdateView();
        _timer.Start();
        Shown += async (_, _) =>
        {
            _console.OpenConsole(this);
            await ToggleConnectionAsync(); // 起動したらすぐ接続する
        };
        UpdateView();
    }

    private async Task ToggleConnectionAsync()
    {
        _connectButton.Enabled = false;
        try
        {
            if (_server.Status is ServerStatus.Stopped or ServerStatus.Rejected)
            {
                _config.Password = _passwordBox.Text;
                _config.Save();
                _server.Start(ServerAddress.Current, _config.Password, ShareMode.CrossingOnly, _crew.Token, CrewRole.Dispatcher);
            }
            else
            {
                _console.StopForDisconnect();
                _phone.StopForDisconnect();
                await _server.StopAsync();
            }
        }
        finally
        {
            _connectButton.Enabled = true;
            UpdateView();
        }
    }

    private void UpdateView()
    {
        var running = _server.Status is ServerStatus.Connecting or ServerStatus.Connected;
        _connectButton.Text = running ? "切断" : "接続";
        _passwordBox.Enabled = !running;
        var radio = _server.TrainRadio;
        (_status.Text, _status.ForeColor) = _server.Status switch
        {
            ServerStatus.Connected => ("● 接続中（司令員）", Color.FromArgb(22, 163, 74)),
            ServerStatus.Connecting => ("○ 接続しています…", Color.DarkGoldenrod),
            ServerStatus.Rejected => ("× 断られました", Color.Firebrick),
            _ => ("未接続", Muted)
        };
        _detail.Text = _server.Status switch
        {
            ServerStatus.Connected => $"参加者 {_server.State?.PlayerCount ?? 0}人　司令員 {radio?.DispatcherCount ?? 0}人　呼出待ち {radio?.Requests.Count ?? 0}件"
                                      + (_server.State?.GameTime is { } t ? $"　ロープレ時刻 {t}" : ""),
            ServerStatus.Rejected => _server.Message,
            _ => _server.Message
        };
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            _closing = true;
            _timer.Stop();
            _console.Shutdown();
            _phone.Shutdown();
            _config.Save();
            await _server.StopAsync();
            _console.Dispose();
            _phone.Dispose();
            _timer.Dispose();
            Close();
            return;
        }

        base.OnFormClosing(e);
    }
}
