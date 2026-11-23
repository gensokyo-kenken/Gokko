using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Gokko.Client;

/// <summary>
/// ロープレ時刻の時計（クライアントの中の小さな窓）。ゲームをしながら、別の窓でロープレ時刻を見られるようにする。
/// ・見た目は信号制御盤のデジタル時計と同じ（白い樹脂の枠に灰緑の液晶。時:分 と小さい秒、その下に 月/日 と曜日）。
///   信号制御盤の部品（relaypanel/parts の clock-*.svg）の形・色・置き場所を、そのまま GDI+ で描く（320×160 の座標で描いて、窓の大きさに合わせる）
/// ・枠のない窓。どこをつかんでも動かせ、右下の角で大きさを変える（横:縦 = 2:1 のまま）。右クリックで「最前面」「閉じる」
/// ・位置・大きさ・最前面は覚えておき、次に開くときも同じにする
/// ・時刻はサーバーから届くロープレ時刻（ClientState.GameTime）。サーバーにつながっていない間は数字を消す（液晶のうすい線だけ）。
///   つながっていてロープレ時刻を設定していなければ、信号制御盤と同じく、この PC の時刻を出す。日付と曜日は、この PC の今日
/// </summary>
internal sealed class RoleplayClockForm : Form
{
    private const float W = 320, H = 160;

    private readonly ClientConfig _config;
    private readonly Func<bool> _connected;
    private readonly Func<string?> _gameTime;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _topMostItem = new("最前面に表示") { CheckOnClick = true };

    public RoleplayClockForm(ClientConfig config, Func<bool> connected, Func<string?> gameTime)
    {
        _config = config;
        _connected = connected;
        _gameTime = gameTime;
        Text = "ロープレ時計";
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0xe6, 0xe9, 0xee);
        MinimumSize = new Size(120, 60);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _topMostItem.Checked = config.ClockTopMost;
        _topMostItem.CheckedChanged += (_, _) =>
        {
            TopMost = _topMostItem.Checked;
            _config.ClockTopMost = _topMostItem.Checked;
            _config.Save();
        };
        var close = new ToolStripMenuItem("閉じる");
        close.Click += (_, _) => Close();
        _menu.Items.AddRange([_topMostItem, new ToolStripSeparator(), close]);
        TopMost = config.ClockTopMost;

        // 位置と大きさ（なければ画面の右上。幅 240）
        var b = config.ClockBounds.Split(',');
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var r = b.Length == 4 && b.All(x => int.TryParse(x, out _))
            ? new Rectangle(int.Parse(b[0]), int.Parse(b[1]), int.Parse(b[2]), int.Parse(b[3]))
            : new Rectangle(screen.Right - 260, screen.Top + 20, 240, 120);
        r.Width = Math.Max(MinimumSize.Width, r.Width);
        r.Height = r.Width / 2;
        // 画面の外（外したモニター）なら、見える所へ戻す
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) r.Location = new Point(screen.Left + 40, screen.Top + 40);
        Bounds = r;

        _timer.Tick += (_, _) => Invalidate();
        Shown += (_, _) => _timer.Start();
        FormClosing += (_, _) => SaveBounds();
        FormClosed += (_, _) => { _timer.Dispose(); _menu.Dispose(); };
    }

    /// <summary>手前に出す（最小化していたら戻す）。</summary>
    public void BringUp()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void SaveBounds()
    {
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _config.ClockBounds = $"{r.X},{r.Y},{r.Width},{r.Height}";
        _config.Save();
    }

    // ── 窓の動かし方（枠がないので、つかむ所を Windows に教える） ──

    private const int WM_NCHITTEST = 0x0084, WM_NCRBUTTONUP = 0x00A5, WM_SIZING = 0x0214;
    private const int HTCAPTION = 2, HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
            {
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                var grip = Math.Max(10, Width / 10);
                m.Result = p.X >= Width - grip && p.Y >= Height - grip ? HTBOTTOMRIGHT : HTCAPTION;
                return;
            }
            case WM_NCRBUTTONUP:
                _menu.Show(Cursor.Position);
                return;
            case WM_SIZING:
            {
                // 大きさを変えている間も 2:1 のまま（幅に合わせて高さを決める）
                var rc = Marshal.PtrToStructure<RECT>(m.LParam);
                rc.Bottom = rc.Top + (rc.Right - rc.Left) / 2;
                Marshal.StructureToPtr(rc, m.LParam, false);
                m.Result = 1;
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 角を枠と同じに丸める
        var s = Width / W;
        using var path = RoundRect(0, 0, Width, Height, 9 * s);
        Region = new Region(path);
    }

    // ── 描画（座標は clock-face.svg の 320×160） ──

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(ClientSize.Width / W, ClientSize.Height / H);
        DrawFace(g);

        // 表示する時刻（その日の秒）。つながっていなければ数字を消す
        int? sec = null;
        if (_connected())
        {
            if (_gameTime() is { } t && TimeSpan.TryParse(t, out var ts)) sec = (int)ts.TotalSeconds % 86400;
            else sec = (int)DateTime.Now.TimeOfDay.TotalSeconds; // ロープレ時刻を設定していなければ、この PC の時刻（信号制御盤と同じ）
        }

        int? Tens(int? n) => n / 10;
        int? Ones(int? n) => n % 10;
        int? hh = sec / 3600, mm = sec / 60 % 60, ss = sec % 60;
        DrawDigit(g, Tens(hh), 18, 18, 49, 84);
        DrawDigit(g, Ones(hh), 67, 18, 49, 84);
        DrawDigit(g, Tens(mm), 140.5f, 18, 49, 84);
        DrawDigit(g, Ones(mm), 189.5f, 18, 49, 84);
        DrawDigit(g, Tens(ss), 246.5f, 54, 28, 48);
        DrawDigit(g, Ones(ss), 274.5f, 54, 28, 48);
        DrawColon(g, 116, 18, 24.5f, 84, DateTime.Now.Millisecond < 500 ? 1f : 0.25f); // 点は 1 秒ごとに点滅

        var today = DateTime.Now;
        if (today.Month >= 10) DrawDigit(g, today.Month / 10, 24, 116, 15.75f, 27); // 月の十の位が 0 のときは置かない
        DrawDigit(g, today.Month % 10, 41, 116, 15.75f, 27);
        DrawDigit(g, today.Day / 10, 72, 116, 15.75f, 27);
        DrawDigit(g, today.Day % 10, 89, 116, 15.75f, 27);
        DrawWeekday(g, (int)today.DayOfWeek, 268, 116, 27, 27);
    }

    private static readonly Color Ink = Color.FromArgb(0x18, 0x1b, 0x17);

    private static void DrawFace(Graphics g)
    {
        using (var path = RoundRect(1, 1, 318, 158, 9))
        {
            using var brush = new LinearGradientBrush(new RectangleF(0, 1, 1, 158), Color.White, Color.White, LinearGradientMode.Vertical)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = [Color.FromArgb(0xfb, 0xfc, 0xfd), Color.FromArgb(0xe6, 0xe9, 0xee), Color.FromArgb(0xc9, 0xce, 0xd6)],
                    Positions = [0f, 0.6f, 1f]
                }
            };
            g.FillPath(brush, path);
            using var pen = new Pen(Color.FromArgb(0x8d, 0x93, 0x9e), 1.1f);
            g.DrawPath(pen, path);
        }

        using (var path = RoundRect(3.4f, 3.4f, 313.2f, 153.2f, 7))
        using (var pen = new Pen(Color.FromArgb(204, 255, 255, 255), 1))
            g.DrawPath(pen, path);

        // 液晶の窓
        using (var path = RoundRect(10, 10, 300, 140, 3))
        {
            using var brush = new LinearGradientBrush(new RectangleF(10, 10, 300, 140), Color.FromArgb(0xb9, 0xc0, 0xae), Color.FromArgb(0xa2, 0xa9, 0x98), LinearGradientMode.ForwardDiagonal);
            g.FillPath(brush, path);
            using var pen = new Pen(Color.FromArgb(0x7d, 0x84, 0x78), 1.1f);
            g.DrawPath(pen, path);
        }

        using (var brush = new LinearGradientBrush(new RectangleF(10.6f, 10.5f, 298.8f, 9.2f), Color.FromArgb(71, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Vertical))
            g.FillRectangle(brush, 10.6f, 10.6f, 298.8f, 9);
        using (var brush = new SolidBrush(Color.FromArgb(15, 255, 255, 255)))
            g.FillPolygon(brush, [new PointF(196, 11), new PointF(246, 11), new PointF(176, 149), new PointF(126, 149)]);

        // 下の段を分ける線、月と日の間の斜めの線
        using (var pen = new Pen(Ink, 1.4f))
            g.DrawLine(pen, 17, 109, 303, 109);
        using (var pen = new Pen(Ink, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, 67.5f, 117, 61, 142);
    }

    // 数字（clock-digit-*.svg。28×48 の中の 7 つの線 a〜g、少し右に傾く）
    private static readonly PointF[][] Segments =
    [
        P(7.5f, 4.5f, 10, 1.5f, 18, 1.5f, 20.5f, 4.5f, 18, 7.5f, 10, 7.5f),           // a 上
        P(22.5f, 6, 25.5f, 8.5f, 25.5f, 20, 22.5f, 22.5f, 19.5f, 20, 19.5f, 8.5f),     // b 右上
        P(22.5f, 25.5f, 25.5f, 28, 25.5f, 39.5f, 22.5f, 42, 19.5f, 39.5f, 19.5f, 28),  // c 右下
        P(7.5f, 43.5f, 10, 40.5f, 18, 40.5f, 20.5f, 43.5f, 18, 46.5f, 10, 46.5f),     // d 下
        P(5.5f, 25.5f, 8.5f, 28, 8.5f, 39.5f, 5.5f, 42, 2.5f, 39.5f, 2.5f, 28),        // e 左下
        P(5.5f, 6, 8.5f, 8.5f, 8.5f, 20, 5.5f, 22.5f, 2.5f, 20, 2.5f, 8.5f),           // f 左上
        P(7.5f, 24, 10, 21, 18, 21, 20.5f, 24, 18, 27, 10, 27),                       // g 中
    ];

    // 数字ごとに出る線（a〜g のビット）
    private static readonly int[] DigitSegments = [0b0111111, 0b0000110, 0b1011011, 0b1001111, 0b1100110, 0b1101101, 0b1111101, 0b0000111, 0b1111111, 0b1101111];

    private static readonly Color Faint = Color.FromArgb(18, 0x1c, 0x21, 0x1a);      // 出ていない線（opacity 0.07）
    private static readonly Color Shadow = Color.FromArgb(89, 0x5c, 0x64, 0x55);     // 線の影（opacity 0.35）

    /// <summary>数字を1つ描く。v が null なら、どの線も出ていない（うすい線だけ）。</summary>
    private static void DrawDigit(Graphics g, int? v, float x, float y, float w, float h)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(w / 28, h / 48);
        Skew(g, 14, 24);
        var on = v is { } d ? DigitSegments[d] : 0;
        using var faint = new SolidBrush(Faint);
        using var shadow = new SolidBrush(Shadow);
        using var ink = new SolidBrush(Ink);
        for (var i = 0; i < 7; i++)
            if ((on & (1 << i)) == 0) g.FillPolygon(faint, Segments[i]);
        g.TranslateTransform(0.9f, 1.1f);
        for (var i = 0; i < 7; i++)
            if ((on & (1 << i)) != 0) g.FillPolygon(shadow, Segments[i]);
        g.TranslateTransform(-0.9f, -1.1f);
        for (var i = 0; i < 7; i++)
            if ((on & (1 << i)) != 0) g.FillPolygon(ink, Segments[i]);
        g.Restore(state);
    }

    // 時と分の間の点（clock-colon.svg。14×48）
    private static void DrawColon(Graphics g, float x, float y, float w, float h, float alpha)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(w / 14, h / 48);
        Skew(g, 7, 24);
        using var shadow = new SolidBrush(Color.FromArgb((int)(Shadow.A * alpha), Shadow));
        using var ink = new SolidBrush(Color.FromArgb((int)(255 * alpha), Ink));
        g.FillRectangles(shadow, [new RectangleF(4.9f, 13.1f, 6, 6), new RectangleF(4.9f, 31.1f, 6, 6)]);
        g.FillRectangles(ink, [new RectangleF(4, 12, 6, 6), new RectangleF(4, 30, 6, 6)]);
        g.Restore(state);
    }

    // 曜日（clock-wday-*.svg。9×9 の点で書いた字。0 = 日）
    private static readonly string[][] Weekdays =
    [
        [".#######.", ".#.....#.", ".#.....#.", ".#.....#.", ".#######.", ".#.....#.", ".#.....#.", ".#.....#.", ".#######."],
        ["..######.", "..#....#.", "..######.", "..#....#.", "..######.", "..#....#.", "..#....#.", ".#.....#.", "#.....##."],
        ["....#....", ".#..#..#.", ".#..#..#.", "#...#...#", "....#....", "...#.#...", "...#.#...", "..#...#..", "##.....##"],
        ["....#....", "....#....", "###.#..#.", "..#.#.#..", "..#.##...", ".#..#.#..", ".#..#..#.", "#...#...#", "...##...."],
        ["....#....", "....#....", "#########", "....#....", "...###...", "..#.#.#..", ".#..#..#.", "#...#...#", "....#...."],
        ["....#....", "...#.#...", ".##...##.", "#.#####.#", "....#....", ".#######.", ".#..#..#.", "..#.#.#..", "#########"],
        ["....#....", "....#....", "....#....", ".#######.", "....#....", "....#....", "....#....", "....#....", "#########"],
    ];

    private static void DrawWeekday(Graphics g, int day, float x, float y, float w, float h)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(w / 27, h / 27);
        var rows = Weekdays[day];
        var all = new List<RectangleF>();
        var lit = new List<RectangleF>();
        for (var r = 0; r < 9; r++)
            for (var c = 0; c < 9; c++)
            {
                var dot = new RectangleF(c * 3 + 0.2f, r * 3 + 0.2f, 2.5f, 2.5f);
                all.Add(dot);
                if (rows[r][c] == '#') lit.Add(dot);
            }

        using (var faint = new SolidBrush(Color.FromArgb(15, 0x1c, 0x21, 0x1a))) // 出ていない点（opacity 0.06）
            g.FillRectangles(faint, all.ToArray());
        using (var shadow = new SolidBrush(Shadow))
            g.FillRectangles(shadow, lit.Select(d => new RectangleF(d.X + 0.6f, d.Y + 0.7f, d.Width, d.Height)).ToArray());
        using (var ink = new SolidBrush(Ink))
            g.FillRectangles(ink, lit.ToArray());
        g.Restore(state);
    }

    // SVG の translate(cx cy) skewX(-5) translate(-cx -cy)
    private static void Skew(Graphics g, float cx, float cy)
    {
        var k = (float)Math.Tan(-5 * Math.PI / 180);
        using var m = new Matrix(1, 0, k, 1, -k * cy, 0);
        g.MultiplyTransform(m);
    }

    private static PointF[] P(params float[] v)
    {
        var p = new PointF[v.Length / 2];
        for (var i = 0; i < p.Length; i++) p[i] = new PointF(v[i * 2], v[i * 2 + 1]);
        return p;
    }

    private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
