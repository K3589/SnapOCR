using System.Globalization;

namespace SnapOCR.Settings;

/// <summary>修饰键，位值与 Win32 的 MOD_* 完全一致，可直接 cast 给 RegisterHotKey。</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>常用虚拟键码，避免在业务代码里出现魔法数字。</summary>
public static class VirtualKeys
{
    public const uint Back = 0x08;
    public const uint Tab = 0x09;
    public const uint Enter = 0x0D;
    public const uint Escape = 0x1B;
    public const uint Space = 0x20;
    public const uint PageUp = 0x21;
    public const uint PageDown = 0x22;
    public const uint End = 0x23;
    public const uint Home = 0x24;
    public const uint Left = 0x25;
    public const uint Up = 0x26;
    public const uint Right = 0x27;
    public const uint Down = 0x28;
    public const uint PrintScreen = 0x2C;
    public const uint Insert = 0x2D;
    public const uint Delete = 0x2E;
    public const uint T = 0x54;
}

/// <summary>一个全局热键：修饰键 + 虚拟键码。</summary>
public readonly record struct HotkeyDefinition(HotkeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>默认热键 Win+Shift+T。</summary>
    public static HotkeyDefinition Default => new(HotkeyModifiers.Win | HotkeyModifiers.Shift, VirtualKeys.T);

    /// <summary>Win 组合被系统占用时的降级热键 Ctrl+Shift+T。</summary>
    public static HotkeyDefinition Fallback => new(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeys.T);

    /// <summary>解析 "Win+Shift+T" / "Ctrl+Alt+O" 这类字符串。</summary>
    public static bool TryParse(string? text, out HotkeyDefinition definition)
    {
        definition = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            // 至少要有「一个修饰键 + 一个主键」，否则会抢走普通按键。
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "win":
                case "windows":
                case "meta":
                case "super":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                default:
                    return false;
            }
        }

        if (modifiers == HotkeyModifiers.None)
        {
            return false;
        }

        if (!TryParseKey(parts[^1], out var virtualKey))
        {
            return false;
        }

        definition = new HotkeyDefinition(modifiers, virtualKey);
        return true;
    }

    /// <summary>解析主键名（字母 / 数字 / F1-F24 / 若干命名键）。</summary>
    public static bool TryParseKey(string? name, out uint virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var key = name.Trim();

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z')
            {
                virtualKey = c;
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        var upper = key.ToUpperInvariant();
        if (upper.StartsWith("0X", StringComparison.Ordinal)
            && uint.TryParse(upper[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexKey)
            && hexKey is >= 0x01 and <= 0xFE
            && !IsModifierVirtualKey(hexKey))
        {
            virtualKey = hexKey;
            return true;
        }

        if (upper.Length is >= 2 and <= 3 && upper[0] == 'F' && int.TryParse(upper[1..], out var fn) && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + fn - 1);
            return true;
        }

        if (upper.Length == 7 && upper.StartsWith("NUMPAD", StringComparison.Ordinal) && upper[6] is >= '0' and <= '9')
        {
            virtualKey = (uint)(0x60 + (upper[6] - '0'));
            return true;
        }

        virtualKey = upper switch
        {
            "SPACE" => VirtualKeys.Space,
            "TAB" => VirtualKeys.Tab,
            "ENTER" or "RETURN" => VirtualKeys.Enter,
            "ESC" or "ESCAPE" => VirtualKeys.Escape,
            "BACKSPACE" or "BACK" => VirtualKeys.Back,
            "DELETE" or "DEL" => VirtualKeys.Delete,
            "INSERT" or "INS" => VirtualKeys.Insert,
            "HOME" => VirtualKeys.Home,
            "END" => VirtualKeys.End,
            "PAGEUP" or "PGUP" => VirtualKeys.PageUp,
            "PAGEDOWN" or "PGDN" => VirtualKeys.PageDown,
            "PRINTSCREEN" or "PRTSC" => VirtualKeys.PrintScreen,
            "LEFT" => VirtualKeys.Left,
            "RIGHT" => VirtualKeys.Right,
            "UP" => VirtualKeys.Up,
            "DOWN" => VirtualKeys.Down,
            "SEMICOLON" or "OEM1" => 0xBA,
            "EQUALS" or "OEMPLUS" => 0xBB,
            "COMMA" or "OEMCOMMA" => 0xBC,
            "MINUS" or "OEMMINUS" => 0xBD,
            "PERIOD" or "OEMPERIOD" => 0xBE,
            "SLASH" or "OEM2" => 0xBF,
            "GRAVE" or "OEM3" => 0xC0,
            "LBRACKET" or "OEM4" => 0xDB,
            "BACKSLASH" or "OEM5" => 0xDC,
            "RBRACKET" or "OEM6" => 0xDD,
            "QUOTE" or "OEM7" => 0xDE,
            _ => 0,
        };

        return virtualKey != 0;
    }

    private static bool IsModifierVirtualKey(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12
        or 0x5B or 0x5C
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>虚拟键码 → 显示名。</summary>
    public static string GetKeyName(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return "Numpad" + (virtualKey - 0x60);
        }

        return virtualKey switch
        {
            VirtualKeys.Space => "Space",
            VirtualKeys.Tab => "Tab",
            VirtualKeys.Enter => "Enter",
            VirtualKeys.Escape => "Esc",
            VirtualKeys.Back => "Backspace",
            VirtualKeys.Delete => "Delete",
            VirtualKeys.Insert => "Insert",
            VirtualKeys.Home => "Home",
            VirtualKeys.End => "End",
            VirtualKeys.PageUp => "PageUp",
            VirtualKeys.PageDown => "PageDown",
            VirtualKeys.PrintScreen => "PrintScreen",
            VirtualKeys.Left => "Left",
            VirtualKeys.Right => "Right",
            VirtualKeys.Up => "Up",
            VirtualKeys.Down => "Down",
            0xBA => "Semicolon",
            0xBB => "Equals",
            0xBC => "Comma",
            0xBD => "Minus",
            0xBE => "Period",
            0xBF => "Slash",
            0xC0 => "Grave",
            0xDB => "LBracket",
            0xDC => "Backslash",
            0xDD => "RBracket",
            0xDE => "Quote",
            _ => "0x" + virtualKey.ToString("X2"),
        };
    }

    /// <summary>规范形式，同时用于 JSON 存储，例如 "Win+Shift+T"。</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(GetKeyName(VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>界面展示用，带空格更好读：Win + Shift + T。</summary>
    public string ToDisplayString() => ToString().Replace("+", " + ", StringComparison.Ordinal);
}
