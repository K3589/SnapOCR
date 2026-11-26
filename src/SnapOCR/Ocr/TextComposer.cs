using System.Text;

namespace SnapOCR.Ocr;

/// <summary>
/// 把 OcrResult.Lines 拼回纯文本。纯函数，方便单测。
/// 规则：
///   1. 行间只用 \n，绝不插入空格 —— 中文行间加空格是最常见的「AI 味」错误；
///   2. 每行首尾空白裁掉；
///   3. 空行丢弃，避免复制出一堆空段落；
///   4. 抹掉 CJK 字符之间的空格 —— 系统 OCR 对中文是逐字输出的
///      （"公 民 身 份 号 码"），不处理的话粘贴出来完全没法用。
/// </summary>
public static class TextComposer
{
    public static string Compose(IEnumerable<string?>? lines)
    {
        if (lines is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var raw in lines)
        {
            if (raw is null)
            {
                continue;
            }

            var line = CollapseCjkSpaces(raw.Trim());
            if (line.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 删除「中日韩字符 + 空格 + 中日韩字符」中间的那个空格。
    /// 只处理 ASCII 空格；中文与英文之间的空格保留（"DeepSeek 开放平台" 仍然可读）。
    /// </summary>
    public static string CollapseCjkSpaces(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains(' ', StringComparison.Ordinal))
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == ' ' && builder.Length > 0)
            {
                // 整段空格一起看：OCR 偶尔会吐出两个以上空格。
                var nextIndex = i;
                while (nextIndex < line.Length && line[nextIndex] == ' ')
                {
                    nextIndex++;
                }

                if (nextIndex < line.Length && IsCjk(builder[^1]) && IsCjk(line[nextIndex]))
                {
                    i = nextIndex - 1;
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>是否 CJK 字符。用于判断某段文本要不要按「逐字」规则处理。</summary>
    public static bool IsCjk(char c)
        => c >= 0x2E80 && c <= 0x9FFF        // CJK 部首扩展、标点（3000-303F）、假名与统一表意文字
           || c >= 0xF900 && c <= 0xFAFF     // 兼容表意文字
           || c >= 0xFF00 && c <= 0xFFEF;    // 全角符号

    /// <summary>整段文本里 CJK 字符是否占多数（用于选择排版策略）。</summary>
    public static bool IsMostlyCjk(string text)
    {
        var cjk = 0;
        var letters = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            letters++;
            if (IsCjk(c))
            {
                cjk++;
            }
        }

        return letters > 0 && cjk * 2 >= letters;
    }
}
