using System;
using System.Collections.Generic;
using Slate.Config;

namespace Slate;

/// <summary>A named look. Picking one from the tray menu applies it to <c>appearance</c> and saves the config.</summary>
internal sealed record SlateTheme(string Name, Action<AppearanceConfig> Apply);

internal static class Themes
{
    /// <summary>
    /// Add themes here, one entry each; the tray's Themes menu lists them automatically.
    /// Keep this list in the same order as mac/Sources/Slate/Themes.swift.
    /// </summary>
    public static readonly IReadOnlyList<SlateTheme> All = new SlateTheme[]
    {
        // new("Midnight", a =>
        // {
        //     a.Background = ["#0F172A", "#020617"];
        //     a.GlowColor = "#38BDF8";
        // }),
    };
}
