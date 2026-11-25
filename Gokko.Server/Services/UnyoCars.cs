using System.Text;

namespace Gokko.Server.Services;

/// <summary>
/// 運行番号ごとの編成の両数（Data/UnyoCars.csv）。自動運転の列車を出すとき・連結・解放の両数に使う。
///
/// CSV はスプレッドシートから書き出したものをそのまま置く。1行目の見出しから「運行番号」「両数」の列を探し、
/// ほかの列は読まない。表にない運行は6両。両数が空・0・11以上・数字でないときも6両にして、Problems に残す。
/// 運行番号は "24"・"24S" のように書く（24 と 24S は別の運行）。全角・小文字・「24S運行」も読む。
/// </summary>
public sealed class UnyoCars
{
    public const string FileName = "UnyoCars.csv";
    public const int Default = 6;

    private readonly Dictionary<string, int> _cars;

    /// <summary>読んだときに見つかったおかしな所（管理画面と記録に出す）。</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>表に書いてある運行の数。</summary>
    public int Count => _cars.Count;

    private UnyoCars(Dictionary<string, int> cars, List<string> problems)
    {
        _cars = cars;
        Problems = problems;
    }

    /// <summary>その運行の両数（表にない運行・運行番号がない列車は6両）。</summary>
    public int Of(string? unyo) => unyo != null && _cars.TryGetValue(unyo, out var c) ? c : Default;

    /// <summary>表に書いてある運行番号。</summary>
    public IEnumerable<string> Unyos => _cars.Keys;

    /// <param name="known">時刻表にある運行番号（渡せば、時刻表にない運行番号の行を Problems に残す）。</param>
    public static UnyoCars Load(string dataDir, IReadOnlyCollection<string>? known = null)
    {
        var path = Path.Combine(dataDir, FileName);
        var cars = new Dictionary<string, int>();
        var problems = new List<string>();
        if (!File.Exists(path))
        {
            problems.Add($"{FileName} がありません（どの運行も6両で出します）");
            return new UnyoCars(cars, problems);
        }

        List<List<string>> rows;
        try { rows = ParseCsv(File.ReadAllText(path, Encoding.UTF8)); }
        catch (Exception ex)
        {
            problems.Add($"{FileName} を読めませんでした（{ex.Message}。どの運行も6両で出します）");
            return new UnyoCars(cars, problems);
        }

        if (rows.Count == 0)
        {
            problems.Add($"{FileName} が空です（どの運行も6両で出します）");
            return new UnyoCars(cars, problems);
        }

        var head = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        var unyoCol = head.FindIndex(h => h == "運行番号" || h == "運行");
        var carsCol = head.FindIndex(h => h == "両数");
        if (unyoCol < 0 || carsCol < 0)
        {
            problems.Add($"{FileName} の1行目に「運行番号」と「両数」の見出しがありません（どの運行も6両で出します）");
            return new UnyoCars(cars, problems);
        }

        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Cell(int c) => c < r.Count ? r[c].Trim() : "";
            var unyoText = Cell(unyoCol);
            if (unyoText.Length == 0 && r.All(x => x.Trim().Length == 0)) continue; // 空の行
            if (Gokko.Common.Unyo.Normalize(unyoText) is not { } unyo)
            {
                problems.Add($"{i + 1}行目: 運行番号「{unyoText}」が運行番号の形（24・24S など）ではありません（読みません）");
                continue;
            }
            if (known != null && !known.Contains(unyo)) problems.Add($"運行{unyo}: 時刻表にない運行番号です（24 と 24S は別の運行です）");

            var carsText = Cell(carsCol);
            var n = Default;
            if (carsText.Length == 0) problems.Add($"運行{unyo}: 両数が空です（6両にします）");
            else if (!int.TryParse(carsText, out n)) { problems.Add($"運行{unyo}: 両数「{carsText}」が数字ではありません（6両にします）"); n = Default; }
            else if (n < 1 || n > 10) { problems.Add($"運行{unyo}: 両数 {n} は1〜10両の外です（6両にします）"); n = Default; }

            if (cars.ContainsKey(unyo)) problems.Add($"運行{unyo}: 2回書いてあります（あとの行の {n}両を使います）");
            cars[unyo] = n;
        }

        return new UnyoCars(cars, problems);
    }

    /// <summary>CSV を行と列に分ける（"" で囲んだ列の中のカンマ・改行・"" に対応）。</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else cell.Append(ch);
        }

        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
