using System.Diagnostics;

namespace Gokko.Client;

/// <summary>
/// 「通信状況」の小窓。自分と、同じ列番の相手（運転士・車掌・添乗）の通信のようすを色つきで並べる。
/// 中身は本体の 0.5 秒ごとの処理から <see cref="ShowItems"/> で入れる。
/// </summary>
public sealed class LinkStatusForm : Form
{
    private sealed class BufferedListView : ListView
    {
        public BufferedListView() => DoubleBuffered = true;
    }

    private readonly BufferedListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill, MultiSelect = false
    };

    private readonly Label _logLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
    private string _shape = "";

    public LinkStatusForm()
    {
        Text = "通信状況";
        ClientSize = new Size(470, 440);
        MinimumSize = new Size(360, 260);
        Font = new Font("Yu Gothic UI", 10f);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        _list.Columns.Add("項目", 220);
        _list.Columns.Add("状態", 220);
        _list.Resize += (_, _) =>
        {
            var w = Math.Max(120, _list.ClientSize.Width - _list.Columns[0].Width);
            if (_list.Columns[1].Width != w) _list.Columns[1].Width = w;
        };

        var legend = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Text = "● 緑: よい　● 橙: 遅れ気味　● 赤: 途切れている・つながっていない"
        };

        var topMost = new CheckBox { Text = "いつも手前に出す", AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
        topMost.CheckedChanged += (_, _) => TopMost = topMost.Checked;
        var openFolder = new Button { Text = "記録のフォルダを開く", AutoSize = true };
        openFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(LinkMonitor.LogDir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LinkMonitor.LogDir}\"") { UseShellExecute = true });
            }
            catch
            {
                // 開けなければ何もしない
            }
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.AddRange([openFolder, topMost]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(legend);
        layout.Controls.Add(_list);
        layout.Controls.Add(_logLabel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    public void ShowItems(IReadOnlyList<LinkItem> items, string? logPath)
    {
        _logLabel.Text = logPath == null
            ? "途切れ・遅れがあると crew-logs に記録します（まだありません）"
            : $"記録: crew-logs\\{Path.GetFileName(logPath)}";

        // 並びが同じなら文字と色だけ書き換える（ちらつかないように）
        var shape = string.Join("\n", items.Select(i => i.Group + "\t" + i.Name));
        if (shape != _shape)
        {
            _shape = shape;
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var item in items)
            {
                if (!groups.TryGetValue(item.Group, out var g))
                {
                    g = new ListViewGroup(item.Group == "自分" ? "自分" : $"相手: {item.Group}");
                    groups[item.Group] = g;
                    _list.Groups.Add(g);
                }

                _list.Items.Add(new ListViewItem([item.Name, ""], g) { UseItemStyleForSubItems = false });
            }

            _list.EndUpdate();
        }

        for (var n = 0; n < items.Count && n < _list.Items.Count; n++)
        {
            var sub = _list.Items[n].SubItems[1];
            var text = "● " + items[n].Value;
            if (sub.Text != text) sub.Text = text;
            var color = ColorOf(items[n].Level);
            if (sub.ForeColor != color) sub.ForeColor = color;
        }
    }

    public static Color ColorOf(LinkLevel level) => level switch
    {
        LinkLevel.Ok => Color.ForestGreen,
        LinkLevel.Warn => Color.DarkOrange,
        LinkLevel.Bad => Color.Firebrick,
        _ => Color.Gray
    };
}
