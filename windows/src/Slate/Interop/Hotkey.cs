using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace Slate.Interop;

/// <summary>A parsed hotkey such as "Win+Space" or "Ctrl+Alt+K".</summary>
internal sealed record Hotkey(bool Win, bool Ctrl, bool Alt, bool Shift, uint Key, string Display)
{
    public const string DefaultText = "Win+Space";

    public static Hotkey Parse(string text)
    {
        bool win = false, ctrl = false, alt = false, shift = false;
        uint? key = null;
        var display = new List<string>();

        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "win" or "windows" or "super" or "meta":
                    win = true; display.Add("Win"); break;
                case "ctrl" or "control":
                    ctrl = true; display.Add("Ctrl"); break;
                case "alt":
                    alt = true; display.Add("Alt"); break;
                case "shift":
                    shift = true; display.Add("Shift"); break;
                default:
                    if (key != null) throw new FormatException($"Hotkey \"{text}\" has more than one non-modifier key.");
                    key = ParseKey(part, text);
                    display.Add(part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]);
                    break;
            }
        }

        if (key == null) throw new FormatException($"Hotkey \"{text}\" has no key, e.g. \"Win+Space\".");
        if (!win && !ctrl && !alt) throw new FormatException($"Hotkey \"{text}\" needs Win, Ctrl or Alt.");

        return new Hotkey(win, ctrl, alt, shift, key.Value, string.Join(" + ", display));
    }

    private static uint ParseKey(string name, string whole)
    {
        try
        {
            var k = (Key)new KeyConverter().ConvertFromInvariantString(name)!;
            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (vk != 0) return (uint)vk;
        }
        catch (Exception)
        {
            // fall through
        }
        throw new FormatException($"Unknown key \"{name}\" in hotkey \"{whole}\".");
    }

    /// <summary>True when exactly the required modifiers are held.</summary>
    public bool ModifiersHeld()
    {
        bool win = Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN);
        bool ctrl = Native.IsKeyDown(Native.VK_CONTROL);
        bool alt = Native.IsKeyDown(Native.VK_MENU);
        bool shift = Native.IsKeyDown(Native.VK_SHIFT);
        return win == Win && ctrl == Ctrl && alt == Alt && shift == Shift;
    }
}
