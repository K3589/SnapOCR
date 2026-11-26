using SnapOCR.Ocr;
using Xunit;

namespace SnapOCR.Tests;

/// <summary>
/// 文本拼接测试。中文行间绝不能出现空格 —— 这是最容易写错、
/// 也最影响粘贴效果的地方。
/// </summary>
public class TextComposerTests
{
    [Fact]
    public void ChineseLines_JoinWithNewlineWithoutSpaces()
    {
        var text = TextComposer.Compose(new[] { "第一行文字", "第二行文字" });
        Assert.Equal("第一行文字\n第二行文字", text);
        Assert.DoesNotContain(" ", text);
    }

    [Fact]
    public void EnglishLines_KeepHardLineBreaks()
    {
        var text = TextComposer.Compose(new[] { "Hello world", "Second line" });
        Assert.Equal("Hello world\nSecond line", text);
    }

    [Fact]
    public void BlankLines_AreDropped()
    {
        var text = TextComposer.Compose(new[] { "A", "", "   ", "B" });
        Assert.Equal("A\nB", text);
    }

    [Fact]
    public void LinesAreTrimmed()
    {
        var text = TextComposer.Compose(new[] { "  前后有空格  ", "\t第二行\t" });
        Assert.Equal("前后有空格\n第二行", text);
    }

    [Fact]
    public void NullAndEmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextComposer.Compose(null));
        Assert.Equal(string.Empty, TextComposer.Compose(Array.Empty<string>()));
        Assert.Equal(string.Empty, TextComposer.Compose(new string?[] { null, null }));
    }

    [Fact]
    public void SingleLine_HasNoTrailingNewline()
    {
        Assert.Equal("只有一行", TextComposer.Compose(new[] { "只有一行" }));
    }

    [Fact]
    public void IsMostlyCjk_DetectsChinese()
    {
        Assert.True(TextComposer.IsMostlyCjk("这是一段中文"));
        Assert.False(TextComposer.IsMostlyCjk("This is English text"));
        Assert.True(TextComposer.IsMostlyCjk("这是一段中文，带一点 English"));
        Assert.False(TextComposer.IsMostlyCjk("只有一点点中文 in a much longer English sentence"));
    }

    [Fact]
    public void CjkSpacesBetweenHanCharacters_AreRemoved()
    {
        // 系统 OCR 对中文是逐字出结果的，这是实测输出的真实形状。
        Assert.Equal("公民身份证号码", TextComposer.Compose(new[] { "公 民 身 份 证 号 码" }));
        Assert.Equal("收藏夹栏", TextComposer.CollapseCjkSpaces("收 藏 夹 栏"));
        Assert.Equal("身份证号码：", TextComposer.CollapseCjkSpaces("身 份 证 号 码 ："));
    }

    [Fact]
    public void SpacesAroundLatinWords_AreKept()
    {
        Assert.Equal("DeepSeek 开放平台", TextComposer.CollapseCjkSpaces("DeepSeek 开 放 平 台"));
        Assert.Equal("Hello world", TextComposer.CollapseCjkSpaces("Hello world"));
        Assert.Equal("360103200406120006", TextComposer.CollapseCjkSpaces("360103200406120006"));
    }

    [Fact]
    public void MultipleSpacesBetweenHan_AreAllRemoved()
    {
        Assert.Equal("中文", TextComposer.CollapseCjkSpaces("中   文"));
    }

    [Fact]
    public void IsCjk_CoversPunctuation()
    {
        Assert.True(TextComposer.IsCjk('中'));
        Assert.True(TextComposer.IsCjk('，'));
        Assert.True(TextComposer.IsCjk('。'));
        Assert.False(TextComposer.IsCjk('A'));
    }
}
