using System.Drawing.Drawing2D;

namespace Gokko.Client;

/// <summary>保護蓋を開いて発報。作動中は1回の操作で解除する。</summary>
internal sealed class GuardedProtectionSwitch : Control
{
    private bool _coverOpen;
    private bool _active;
    public event Action? ToggleRequested;

    /// <summary>窓の倍率（無線機の大きさを変えたとき）。絵と文字をこの倍率で描く。</summary>
    public float Zoom { get => _zoom; set { if (Math.Abs(_zoom - value) < .001f) return; _zoom = value; Invalidate(); } }
    private float _zoom = 1f;

    public bool Active
    {
        get => _active;
        set { if (_active && !value) _coverOpen = false; _active = value; Invalidate(); }
    }
    public GuardedProtectionSwitch()
    {
        DoubleBuffered = true; Cursor = Cursors.Hand; TabStop = true;
        AccessibleName = "区間防護送信";
        AccessibleRole = AccessibleRole.PushButton;
        BackColor = Color.FromArgb(45, 57, 49);
    }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled) return;
        if (!_active && !_coverOpen) { _coverOpen = true; Invalidate(); return; }
        ToggleRequested?.Invoke();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.Button == MouseButtons.Right && !_active) { _coverOpen = false; Invalidate(); }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        // もとの大きさの座標で描く（Width・Height はもとの大きさに直す）
        var z = _zoom;
        var Width = (int)Math.Round(this.Width / z);
        var Height = (int)Math.Round(this.Height / z);
        g.ScaleTransform(z, z);
        void Label(string text, Font f, Rectangle r, Color color) =>
            TextRenderer.DrawText(g, text, f, new Rectangle((int)(r.X * z), (int)(r.Y * z), (int)(r.Width * z), (int)(r.Height * z)), color, TextFormatFlags.HorizontalCenter);
        using var baseBrush = new SolidBrush(Color.FromArgb(20, 28, 23));
        g.FillRectangle(baseBrush, 5, 6, Width - 10, Height - 12);
        using var border = new Pen(Color.FromArgb(126, 140, 116));
        g.DrawRectangle(border, 5, 6, Width - 10, Height - 12);
        using var plate = new LinearGradientBrush(new Rectangle(17, 15, Width - 34, 44),
            _active ? Color.FromArgb(220, 62, 44) : Color.FromArgb(132, 145, 119), Color.FromArgb(48, 55, 43), 90);
        g.FillRectangle(plate, 17, _active ? 13 : 24, Width - 34, 30);
        using var font = new Font("Yu Gothic UI", 11 * z, FontStyle.Bold);
        Label(_active ? "解除" : "発報", font, new Rectangle(10, 52, Width - 20, 22), Color.FromArgb(221, 230, 206));
        if (!_active && !_coverOpen)
        {
            using var cover = new SolidBrush(Color.FromArgb(205, 149, 30, 23));
            g.FillRectangle(cover, 2, 3, Width - 4, Height - 6);
            using var highlight = new Pen(Color.FromArgb(222, 133, 108), 2);
            g.DrawRectangle(highlight, 2, 3, Width - 5, Height - 7);
            g.DrawLine(highlight, 8, 10, Width - 12, Height - 15);
            Label("保護蓋を開く", font, new Rectangle(2, 29, Width - 4, 25), Color.FromArgb(252, 226, 203));
        }
        if (!Enabled)
        {
            using var disabled = new SolidBrush(Color.FromArgb(100, 30, 37, 31));
            g.FillRectangle(disabled, 0, 0, Width, Height);
        }
    }
}
