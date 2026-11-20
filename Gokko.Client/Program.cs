using Gokko.Common;

namespace Gokko.Client;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 開発用: 環境変数 GOKKO_PANEL_PREVIEW に PNG のパスを入れると、車掌用操作盤の見本の画像を作って終わる（ゲーム・サーバーにはつながない）
        // 開発用: GOKKO_ONBOARD_PREVIEW に PNG のパスを入れると、放送装置のポップの見本の画像を作って終わる
        // 開発用: GOKKO_ONBOARD_SOUND にフォルダを入れると、放送の音の見本（同梱のチャイムを放送装置に通したもの）を WAV で書き出して終わる
        if (Environment.GetEnvironmentVariable("GOKKO_ONBOARD_SOUND") is { Length: > 0 } soundDir)
        {
            SaveOnboardSounds(soundDir);
            return;
        }

        if (Environment.GetEnvironmentVariable("GOKKO_ONBOARD_PREVIEW") is { Length: > 0 } onboardPath)
        {
            SaveOnboardPreview(onboardPath);
            return;
        }

        if (Environment.GetEnvironmentVariable("GOKKO_PANEL_PREVIEW") is { Length: > 0 } previewPath)
        {
            SavePanelPreview(previewPath);
            return;
        }

        // 接続のモードは起動時に選ぶ（元の画面の［モード選択へ］で、ログインしたまま選び直せる）。
        // 在線共有なら、シナリオを始める前に他列車を消す必要があるため、TRAIN CREW につながった時点ですぐ設定する
        var config = ClientConfig.Load();

        // 乗務員のログイン（Discord。移行のあいだは社員番号とパスワードも）。ログインしないと使えない
        CrewSession crew;
        // 社員番号と（記憶するなら）合い札・パスワードは、ログインの画面が config に入れて保存する
        using (var login = new LoginDialog(config))
        {
            if (login.ShowDialog() != DialogResult.OK || login.Session is not { } s) return; // ログインせずに閉じたら終了
            crew = s;
        }

        // モードを選ぶ画面 → 元の画面、を繰り返す。元の画面の［モード選択へ］で閉じたら、ログインしたまま、またモードを選ぶ画面に戻る
        var envMode = Environment.GetEnvironmentVariable("GOKKO_MODE"); // 開発用（最初の1回だけ使う）
        while (true)
        {
            // 司令員の役職があるか（あれば「司令として参加」を選べる。サーバーも参加のときに確かめる）。戻るたびに確かめ直す
            var canDispatch = false;
            try
            {
                var me = CrewApi.SendAsync(HttpMethod.Get, "/crew/me", token: crew.Token).GetAwaiter().GetResult();
                canDispatch = me["roles"] is System.Text.Json.Nodes.JsonArray roles && roles.Any(r => r?.GetValue<string>() == "司令員");
            }
            catch
            {
                // 分からなければ選べないままにする
            }

            ShareMode mode;
            var role = config.Role;
            if (envMode is "shared" or "crossing")
            {
                mode = envMode == "shared" ? ShareMode.Shared : ShareMode.CrossingOnly;
                role = Environment.GetEnvironmentVariable("GOKKO_ROLE") switch { "conductor" => CrewRole.Conductor, "rider" => CrewRole.Rider, "dispatcher" => CrewRole.Dispatcher, _ => CrewRole.Driver };
                envMode = null;
            }
            else
            {
                using var dialog = new ModeDialog(config.Mode, config.Role, canDispatch);
                if (dialog.ShowDialog() != DialogResult.OK || dialog.Selected is not { } selected) return; // 選ばずに閉じたら終了
                mode = selected;
                role = dialog.SelectedRole;
            }

            bool back;
            if (role == CrewRole.Dispatcher)
            {
                // 司令: TRAIN CREW にはつながず、司令機（列車無線）だけ
                config.Role = role;
                config.Save();
                var form = new DispatcherForm(config, crew);
                Application.Run(form);
                back = form.ReturnToStart;
            }
            else
            {
                if (mode != ShareMode.Shared) role = CrewRole.Driver;
                config.Mode = mode;
                config.Role = role;
                config.Save();
                var form = new MainForm(config, mode, crew, role);
                Application.Run(form);
                back = form.ReturnToStart;
            }

            if (!back) return;
            CloseLeftoverForms();
        }
    }

    /// <summary>モードを選ぶ画面に戻るとき、前のモードの窓（申し込み・お知らせなど）が残っていれば閉じる。</summary>
    private static void CloseLeftoverForms()
    {
        foreach (var f in Application.OpenForms.Cast<Form>().ToList())
        {
            try
            {
                f.Close();
                f.Dispose();
            }
            catch
            {
                // 閉じられなくても続ける
            }
        }
    }

    /// <summary>放送の音の見本: 車内（空いている・満員）・車外・連絡を、形式ごとに WAV で書き、響きの残り（音が終わってから0.5秒の大きさ）を onboard-sounds.txt に書く。</summary>
    private static void SaveOnboardSounds(string dir)
    {
        Directory.CreateDirectory(dir);
        var source = ChimeTone.Load("broadcast_chime.wav").Take(Gokko.Common.TrainRadioFormat.SampleRate * 2).ToArray(); // 2秒分
        var report = new List<string>();
        foreach (var (profile, tag) in new[] { (SpeakerProfile.Old, "old"), (SpeakerProfile.Standard, "standard"), (SpeakerProfile.Newer, "newer") })
        foreach (var (channel, absorb, name) in new[]
                 {
                     (Gokko.Common.OnboardChannel.Inside, 0f, "inside-empty"), (Gokko.Common.OnboardChannel.Inside, 1f, "inside-full"),
                     (Gokko.Common.OnboardChannel.Outside, 0f, "outside"), (Gokko.Common.OnboardChannel.Intercom, 0f, "intercom")
                 })
        {
            var encoder = RadioCodec.CreateEncoder();
            var voice = new OnboardVoice(channel, profile) { Gain = 1f, Absorb = absorb };
            var frame = Gokko.Common.TrainRadioFormat.FrameSamples;
            var output = new List<float>();
            var buf = new float[frame];
            var frames = source.Length / frame + 50; // 後ろに2秒の無音（響きの残り）
            for (var f = 0; f < frames; f++)
            {
                var samples = new short[frame];
                for (var i = 0; i < frame; i++)
                {
                    var k = f * frame + i;
                    samples[i] = k < source.Length ? (short)Math.Clamp(source[k] * 32767f, short.MinValue, short.MaxValue) : (short)0;
                }

                if (f * frame < source.Length && encoder.Encode(samples) is { } opus) voice.AddOpus(opus);
                voice.Read(buf, 0, frame);
                output.AddRange(buf);
            }

            var end = source.Length + Gokko.Common.TrainRadioFormat.SampleRate * 200 / 1000; // 受話の遅れの分だけ後ろから
            var tail = output.Skip(end).Take(Gokko.Common.TrainRadioFormat.SampleRate / 2).ToArray();
            var body = output.Take(end).ToArray();
            double Rms(float[] x) => x.Length == 0 ? 0 : Math.Sqrt(x.Average(v => (double)v * v));
            report.Add($"{tag,-9} {name,-13} 本体 {Rms(body):0.0000}  響きの残り {Rms(tail):0.00000}  最大 {output.Max(Math.Abs):0.000}");
            using var w = new NAudio.Wave.WaveFileWriter(Path.Combine(dir, $"{tag}-{name}.wav"), NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(Gokko.Common.TrainRadioFormat.SampleRate, 1));
            w.WriteSamples(output.ToArray(), 0, output.Count);
        }

        File.WriteAllLines(Path.Combine(dir, "onboard-sounds.txt"), report);
    }

    /// <summary>放送装置のポップの見本（待機・自分が車外放送とインターホン・運転士が車内放送中）を横に並べて保存する。</summary>
    private static void SaveOnboardPreview(string path)
    {
        Gokko.Common.OnboardState State(Gokko.Common.OnboardChannel b, string by, bool intercom) => new()
        {
            TrainNumber = "1140", SelfId = "me", Role = CrewRole.Conductor, Intercom = intercom, Broadcast = b, BroadcasterId = by
        };
        var shots = new List<Bitmap>();
        foreach (var (st, status, color, send, level) in new[]
                 {
                     (State(Gokko.Common.OnboardChannel.None, "", false), "列番 1140　放送は流れていません", Color.FromArgb(32, 36, 44), "マイクは閉じています", 0f),
                     (State(Gokko.Common.OnboardChannel.Outside, "me", false), "列番 1140　自分が車外放送を流しています", MainForm.OutsideColor, "● 送話中（車外放送）", 0.55f),
                     (State(Gokko.Common.OnboardChannel.Inside, "d", true), "列番 1140　運転士　山田が車内放送を流しています", MainForm.InsideColor, "マイク待機中（インターホン。声を出すと自動で送ります）", 0.05f)
                 })
        {
            using var f = new OnboardPopupForm("放送装置（車掌）", new RadioSettings(), () => { }) { AllowClose = true };
            f.Show();
            f.ShowKeys("F9", "F10", "F11");
            f.ShowModel("形式 4300 + 5320（新しめの車両）・乗車率 85%");
            f.ShowState(status, color, st, send, send.StartsWith("●") ? Color.Firebrick : Color.Gray, level);
            Application.DoEvents();
            var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            shots.Add(bmp);
            f.Close();
        }

        using var all = new Bitmap(shots.Sum(b => b.Width) + 32, shots.Max(b => b.Height) + 16);
        using (var g = Graphics.FromImage(all))
        {
            g.Clear(Color.FromArgb(200, 200, 200));
            var x = 8;
            foreach (var b in shots)
            {
                g.DrawImage(b, x, 8);
                x += b.Width + 8;
                b.Dispose();
            }
        }

        all.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>車掌用操作盤の見本（ふだん、左に鍵を差して開けて非常ブレーキを入れた状態、いちばん小さくしたとき）を横に並べて保存する。</summary>
    private static void SavePanelPreview(string path)
    {
        var shots = new List<Bitmap>();
        foreach (var step in new[] { 0, 1, 2 })
        {
            using var panel = new ConductorPanelForm { AllowClose = true };
            if (step == 0) panel.SetLink("列番 1140", "運転士 B3（前）　ずれ +0.4m", true);
            else
            {
                panel.SetLink("列番 1140", "運転士 非常（前）　ずれ -1.2m", true);
                panel.SetPreviewState(emergency: true, leftKey: true, leftOpen: true, status: "左 開");
            }

            if (step == 2) panel.ClientSize = new Size(300, 664); // いちばん小さくしたとき
            panel.Show();
            Application.DoEvents();
            var bmp = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(bmp, new Rectangle(0, 0, panel.Width, panel.Height));
            shots.Add(bmp);
            panel.Close();
        }

        using var all = new Bitmap(shots.Sum(b => b.Width) + 24, shots.Max(b => b.Height) + 16);
        using (var g = Graphics.FromImage(all))
        {
            g.Clear(Color.FromArgb(60, 60, 60));
            var x = 8;
            foreach (var b in shots)
            {
                g.DrawImage(b, x, 8);
                x += b.Width + 8;
                b.Dispose();
            }
        }

        all.Save(path, System.Drawing.Imaging.ImageFormat.Png);

        // 運転士のキーの設定画面も（同じフォルダに -keys.png で）
        using var keys = new DriverKeyDialog("N|Joy0:5", new Dictionary<string, string> { ["GradientStart"] = "G" });
        keys.Show();
        Application.DoEvents();
        using var kb = new Bitmap(keys.Width, keys.Height);
        keys.DrawToBitmap(kb, new Rectangle(0, 0, keys.Width, keys.Height));
        kb.Save(Path.ChangeExtension(path, null) + "-keys.png", System.Drawing.Imaging.ImageFormat.Png);
        keys.Close();
    }
}
