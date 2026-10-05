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
        // The original look; picking it restores the defaults.
        new("Violet", a =>
        {
            var d = new AppearanceConfig();
            a.Background = d.Background;
            a.BorderColor = d.BorderColor;
            a.BorderHighlight = d.BorderHighlight;
            a.GlowColor = d.GlowColor;
            a.GlowOpacity = d.GlowOpacity;
            a.TextColor = d.TextColor;
            a.PlaceholderColor = d.PlaceholderColor;
            a.PromptColor = d.PromptColor;
            a.Decoration = "";
        }),
        new("Dark", a =>
        {
            a.Background = ["#1C1C1F", "#111113", "#09090B"];
            a.BorderColor = "#3F3F46";
            a.BorderHighlight = "#D4D4D8";
            a.GlowColor = "#000000";
            a.GlowOpacity = 0.75;
            a.TextColor = "#FAFAFA";
            a.PlaceholderColor = "#71717A";
            a.PromptColor = "#E4E4E7";
            a.Decoration = "";
        }),
        new("Light", a =>
        {
            a.Background = ["#FFFFFF", "#F7F7F8", "#F0F0F2"];
            a.BorderColor = "#D4D4D8";
            a.BorderHighlight = "#71717A";
            a.GlowColor = "#000000";
            a.GlowOpacity = 0.22;
            a.TextColor = "#18181B";
            a.PlaceholderColor = "#8A8A93";
            a.PromptColor = "#3F3F46";
            a.Decoration = "";
        }),
        new("Forest", a =>
        {
            a.Background = ["#123321", "#0C2617", "#07170E"];
            a.BorderColor = "#3F8F4F";
            a.BorderHighlight = "#BBF7D0";
            a.GlowColor = "#22C55E";
            a.GlowOpacity = 0.55;
            a.TextColor = "#ECFDF3";
            a.PlaceholderColor = "#7FA88C";
            a.PromptColor = "#6EE787";
            a.Decoration = "vines";
        }),
    };
}
