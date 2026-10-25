using SimOCR.Interop;

namespace SimOCR.Interop;

/// <summary>
/// 一个全局热键组合。持久化格式为字符串，例如 "F1"、"Ctrl+Alt+A"。
/// </summary>
internal sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static readonly HotkeyGesture None = new(HotkeyModifiers.None, 0);

    public bool IsEmpty => VirtualKey == 0;

    public string Display
    {
        get
        {
            if (IsEmpty)
            {
                return "(未设置)";
            }

            var parts = new List<string>(4);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyName(VirtualKey));
            return string.Join('+', parts);
        }
    }

    /// <summary>规范化成 "Ctrl+Alt+A" 形式，用于写回 config.json。</summary>
    public (string Modifiers, string Key) ToSettingsParts()
    {
        var mods = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) mods.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) mods.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) mods.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) mods.Add("Win");

        return (mods.Count == 0 ? "None" : string.Join('+', mods), KeyName(VirtualKey));
    }

    public override string ToString() => Display;

    /// <summary>
    /// 解析 "Ctrl+Alt+A" / "F1" / "Control, Alt+A" / "shift f4" 这类字符串。
    /// 修饰键分隔符容忍 + 、逗号、竖线、分号与空格（手工编辑 config.json 时容易写成各种形式）。
    /// 无法解析时返回 <see cref="None"/>。
    /// </summary>
    public static HotkeyGesture Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        var modifiers = HotkeyModifiers.None;
        uint vk = 0;

        var tokens = text
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(t => t.Split([' ', ',', '|', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var rawToken in tokens)
        {
            switch (rawToken.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win":
                case "windows":
                case "meta":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
                case "none":
                    continue;
            }

            var candidate = ToVirtualKey(rawToken);
            if (candidate != 0)
            {
                vk = candidate;
            }
        }

        return vk == 0 ? None : new HotkeyGesture(modifiers, vk);
    }

    /// <summary>
    /// 是否允许注册为全局热键。安全规则：**无修饰键时只允许功能键/PrintScreen**，
    /// 否则会把普通字母数字键从整个系统里抢走（曾因配置写成 "Control, Alt" 被误解析为裸 A 键）。
    /// </summary>
    public bool IsSafeAsGlobalHotkey()
    {
        if (IsEmpty)
        {
            return false;
        }

        if (Modifiers != HotkeyModifiers.None && Modifiers != HotkeyModifiers.NoRepeat)
        {
            return true;
        }

        return VirtualKey is (>= 0x70 and <= 0x87) or 0x2C or 0x13; // F1..F24 / PrintScreen / Pause
    }

    private static uint ToVirtualKey(string token)
    {
        var t = token.Trim().ToUpperInvariant();
        if (t.Length == 0)
        {
            return 0;
        }

        // F1..F24
        if (t[0] == 'F' && int.TryParse(t.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            return (uint)(0x70 + (fn - 1));
        }

        // 单字母 / 单数字
        if (t.Length == 1)
        {
            var c = t[0];
            if (c is >= 'A' and <= 'Z')
            {
                return c;
            }

            if (c is >= '0' and <= '9')
            {
                return c;
            }
        }

        return t switch
        {
            "PRINTSCREEN" or "PRTSC" or "SNAPSHOT" => 0x2C,
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "OEM3" or "`" => 0xC0,
            "-" => 0xBD,
            "=" => 0xBB,
            "[" => 0xDB,
            "]" => 0xDD,
            "\\" => 0xDC,
            ";" => 0xBA,
            "'" => 0xDE,
            "," => 0xBC,
            "." => 0xBE,
            "/" => 0xBF,
            _ => 0,
        };
    }

    private static string KeyName(uint vk) => vk switch
    {
        >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        0x2C => "PrintScreen",
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        _ => $"0x{vk:X2}",
    };
}
