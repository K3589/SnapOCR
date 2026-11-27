using SnapOCR.Settings;
using Xunit;

namespace SnapOCR.Tests;

/// <summary>热键解析/格式化测试。设置里的字符串是唯一的持久化格式，必须能往返。</summary>
public class HotkeyDefinitionTests
{
    [Fact]
    public void Default_IsWinShiftT()
    {
        var definition = HotkeyDefinition.Default;
        Assert.Equal(HotkeyModifiers.Win | HotkeyModifiers.Shift, definition.Modifiers);
        Assert.Equal(0x54u, definition.VirtualKey);
        Assert.Equal("Win+Shift+T", definition.ToString());
    }

    [Fact]
    public void Fallback_IsCtrlShiftT()
    {
        var definition = HotkeyDefinition.Fallback;
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, definition.Modifiers);
        Assert.Equal("Ctrl+Shift+T", definition.ToString());
    }

    [Theory]
    [InlineData("Win+Shift+T", "Win+Shift+T")]
    [InlineData("win+shift+t", "Win+Shift+T")]
    [InlineData("Ctrl+Alt+O", "Ctrl+Alt+O")]
    [InlineData("Alt+Shift+F9", "Alt+Shift+F9")]
    [InlineData("Ctrl+Shift+Space", "Ctrl+Shift+Space")]
    [InlineData("Win+PrintScreen", "Win+PrintScreen")]
    public void TryParse_AcceptsCanonicalAndLooseInput(string input, string canonical)
    {
        Assert.True(HotkeyDefinition.TryParse(input, out var definition));
        Assert.Equal(canonical, definition.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("T")]              // 没有修饰键
    [InlineData("Ctrl")]           // 只有修饰键
    [InlineData("Ctrl+Foo")]       // 未知键名
    [InlineData("Hyper+T")]        // 未知修饰键
    public void TryParse_RejectsInvalidInput(string input)
    {
        Assert.False(HotkeyDefinition.TryParse(input, out _));
    }

    [Fact]
    public void TryParse_RejectsSingleModifierOnly()
    {
        Assert.False(HotkeyDefinition.TryParse("Shift+Shift", out _));
    }

    [Theory]
    [InlineData("Ctrl+Alt+J")]
    [InlineData("Win+Shift+T")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+Shift+2")]
    public void ToString_RoundTrips(string text)
    {
        Assert.True(HotkeyDefinition.TryParse(text, out var first));
        Assert.True(HotkeyDefinition.TryParse(first.ToString(), out var second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void ToDisplayString_AddsSpaces()
    {
        var definition = new HotkeyDefinition(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x54);
        Assert.Equal("Win + Shift + T", definition.ToDisplayString());
    }

    [Fact]
    public void ModifierValues_MatchWin32()
    {
        // 这些位值要直接喂给 RegisterHotKey，必须和 Win32 的 MOD_* 对齐。
        Assert.Equal(0x0001u, (uint)HotkeyModifiers.Alt);
        Assert.Equal(0x0002u, (uint)HotkeyModifiers.Control);
        Assert.Equal(0x0004u, (uint)HotkeyModifiers.Shift);
        Assert.Equal(0x0008u, (uint)HotkeyModifiers.Win);
    }

    [Fact]
    public void GetKeyName_HandlesLettersDigitsFunctionKeys()
    {
        Assert.Equal("T", HotkeyDefinition.GetKeyName(0x54));
        Assert.Equal("7", HotkeyDefinition.GetKeyName(0x37));
        Assert.Equal("F12", HotkeyDefinition.GetKeyName(0x7B));
        Assert.Equal("Space", HotkeyDefinition.GetKeyName(0x20));
        Assert.Equal("Numpad5", HotkeyDefinition.GetKeyName(0x65));
        Assert.Equal("Grave", HotkeyDefinition.GetKeyName(0xC0));
    }

    [Theory]
    [InlineData(0x60u)] // Numpad0
    [InlineData(0x69u)] // Numpad9
    [InlineData(0xC0u)] // OEM3 (`~)
    [InlineData(0xBDu)] // OEMMinus
    [InlineData(0xBBu)] // OEMPlus
    [InlineData(0xDFu)] // 未命名扩展键，走 0xXX 兜底往返
    public void RecordedNonAlphaKeys_RoundTripThroughToStringAndTryParse(uint virtualKey)
    {
        var original = new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift, virtualKey);
        Assert.True(HotkeyDefinition.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("Ctrl+0x00")]
    [InlineData("Ctrl+0x10")] // VK_SHIFT
    [InlineData("Ctrl+0x5B")] // VK_LWIN
    [InlineData("Ctrl+0xFF")]
    [InlineData("Ctrl+0xZZ")]
    public void TryParse_RejectsInvalidOrModifierHexKeys(string input)
    {
        Assert.False(HotkeyDefinition.TryParse(input, out _));
    }
}
