using System.Text;

namespace P2PChat.UI.Views;

/// <summary>
/// 轻量控制台屏幕：整屏按行差分绘制 + 显示宽度工具（东亚全角字符按 2 列计算）
/// 无任何第三方 UI 依赖，AOT 友好。
/// </summary>
internal sealed class ConsoleScreen
{
    private const int LeftPanelWidth = 30;
    private string[] _frame = [];
    private int _width, _height;
    private bool _drawn;

    /// <summary>绘制整屏：标题栏 / 左栏联系人 / 右栏聊天记录 / 输入行 / 状态行；返回时光标停在输入行末尾</summary>
    public void Draw(string dhtStatus, string chatTitle, IReadOnlyList<string> contactLines,
        IReadOnlyList<string> chatMessages, int scrollFromEnd, string input, string session)
    {
        var w = Math.Max(Console.WindowWidth, 20);
        var h = Math.Max(Console.WindowHeight, 6);
        var leftW = Math.Min(LeftPanelWidth, Math.Max(12, w / 3));
        var rightW = w - leftW - 1;
        var bodyRows = h - 4;

        var chatLines = new List<string>();
        foreach (var message in chatMessages) WrapInto(chatLines, message, Math.Max(2, rightW - 1));
        var start = Math.Max(0, Math.Max(0, chatLines.Count - bodyRows) - scrollFromEnd);

        var lines = new string[h];
        lines[0] = Fit($"P2PChat - P2P DHT Chat    {dhtStatus}", w);
        lines[1] = Fit("联系人", leftW) + "|" + Fit($"聊天: {chatTitle}", rightW);
        for (var i = 0; i < bodyRows; i++)
            lines[2 + i] = Fit(i < contactLines.Count ? contactLines[i] : "", leftW) + "|"
                + Fit(start + i < chatLines.Count ? chatLines[start + i] : "", rightW);
        lines[h - 2] = Fit("输入> " + input, w);
        lines[h - 1] = Fit($"Tab=切换联系人  F1=帮助  F10=退出  PgUp/PgDn=滚动  /help 命令  会话:{session}", w);

        for (var i = 0; i < h; i++)
        {
            if (_drawn && _width == w && _height == h && i < _frame.Length && _frame[i] == lines[i]) continue;
            Console.SetCursorPosition(0, i);
            Console.Write(lines[i]);
        }
        _frame = lines;
        _width = w;
        _height = h;
        _drawn = true;
        Console.SetCursorPosition(Math.Min(6 + TextWidth(input), w - 1), h - 2);
    }

    public static string Short(string text, int length = 8) => text.Length <= length ? text : text[..length];

    public static int TextWidth(string text)
    {
        var width = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var hi = char.IsHighSurrogate(text[i]) && i + 1 < text.Length;
            width += CharWidth(hi ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i]);
            if (hi) i++;
        }
        return width;
    }

    /// <summary>按显示宽度截断并补齐到恰好 width 列</summary>
    public static string Fit(string text, int width)
    {
        if (width <= 0) return string.Empty;
        var sb = new StringBuilder(width);
        var used = 0;
        for (var i = 0; i < text.Length && used < width; i++)
        {
            var hi = char.IsHighSurrogate(text[i]) && i + 1 < text.Length;
            var charWidth = CharWidth(hi ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i]);
            if (used + charWidth > width) break;
            sb.Append(text, i, hi ? 2 : 1);
            if (hi) i++;
            used += charWidth;
        }
        return sb.Append(' ', width - used).ToString();
    }

    /// <summary>按显示宽度折行（\n 保留为分段）</summary>
    public static void WrapInto(List<string> destination, string text, int width)
    {
        if (width < 2) width = 2;
        foreach (var paragraph in text.Split('\n'))
        {
            var sb = new StringBuilder();
            var used = 0;
            for (var i = 0; i < paragraph.Length; i++)
            {
                var hi = char.IsHighSurrogate(paragraph[i]) && i + 1 < paragraph.Length;
                var charWidth = CharWidth(hi ? char.ConvertToUtf32(paragraph[i], paragraph[i + 1]) : paragraph[i]);
                if (used + charWidth > width) { destination.Add(sb.ToString()); sb.Clear(); used = 0; }
                sb.Append(paragraph, i, hi ? 2 : 1);
                if (hi) i++;
                used += charWidth;
            }
            destination.Add(sb.ToString());
        }
    }

    private static int CharWidth(int cp) => cp >= 0x1100 && (cp <= 0x115F || cp is 0x2329 or 0x232A
        || (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) || (cp >= 0xAC00 && cp <= 0xD7A3)
        || (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFE30 && cp <= 0xFE6F) || (cp >= 0xFF00 && cp <= 0xFF60)
        || (cp >= 0xFFE0 && cp <= 0xFFE6) || (cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x20000 && cp <= 0x3FFFD)) ? 2 : 1;
}
