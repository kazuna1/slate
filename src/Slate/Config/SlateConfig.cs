using System.Collections.Generic;

namespace Slate.Config;

public sealed class SlateConfig
{
    /// <summary>"Win+Space", "Ctrl+Alt+K", ...</summary>
    public string Hotkey { get; set; } = "Win+Space";

    /// <summary>"auto" (pwsh if your profile lives there, else Windows PowerShell), "powershell", "pwsh", or a full path.</summary>
    public string Shell { get; set; } = "auto";

    /// <summary>"wt" (new Windows Terminal window), "wt-tab" (tab in the last WT window), or "console".</summary>
    public string Terminal { get; set; } = "wt";

    public string WorkingDirectory { get; set; } = "%USERPROFILE%";

    public int HistorySize { get; set; } = 500;

    public AppearanceConfig Appearance { get; set; } = new();

    public AnimationConfig Animations { get; set; } = new();
}

public sealed class AppearanceConfig
{
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 64;

    /// <summary>"top", "center" or "bottom" of the primary screen's work area.</summary>
    public string Anchor { get; set; } = "top";
    public double OffsetX { get; set; } = 0;
    public double OffsetY { get; set; } = 48;

    public double CornerRadius { get; set; } = 18;
    public double BorderThickness { get; set; } = 1.5;

    /// <summary>Gradient stops for the bar, evenly spaced. One entry = solid color.</summary>
    public List<string> Background { get; set; } = ["#2E1065", "#1E0B45", "#12062B"];
    public double BackgroundAngle { get; set; } = 0;

    public string BorderColor { get; set; } = "#7C4DFF";
    public string BorderHighlight { get; set; } = "#E9D5FF";

    public string GlowColor { get; set; } = "#8B5CF6";
    public double GlowSize { get; set; } = 30;
    public double GlowOpacity { get; set; } = 0.8;

    public string TextColor { get; set; } = "#F5F0FF";
    public string PlaceholderColor { get; set; } = "#8B7BB3";
    public string PromptColor { get; set; } = "#C084FC";

    public string Prompt { get; set; } = "❯";
    public string Placeholder { get; set; } = "run anything...";

    public string FontFamily { get; set; } = "Cascadia Code, Cascadia Mono, Consolas";
    public double FontSize { get; set; } = 20;

    public bool ShowHint { get; set; } = true;
    /// <summary>Overrides the hint text; defaults to the hotkey, e.g. "Win + Space".</summary>
    public string? HintText { get; set; }

    /// <summary>Bar opacity while it sits on the desktop (1 when summoned).</summary>
    public double IdleOpacity { get; set; } = 0.9;
}

public sealed class AnimationConfig
{
    /// <summary>Master switch.</summary>
    public bool Enabled { get; set; } = true;

    public bool SummonPop { get; set; } = true;
    public int SummonDurationMs { get; set; } = 220;

    public bool GlowPulse { get; set; } = true;
    public double GlowPulseSeconds { get; set; } = 3.5;

    public bool BorderShimmer { get; set; } = true;
    public double BorderShimmerSeconds { get; set; } = 6;

    public bool RunFlash { get; set; } = true;
}
