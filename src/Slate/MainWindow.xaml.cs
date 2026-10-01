using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Slate.Config;
using Slate.Interop;

namespace Slate;

public enum DismissReason
{
    Escape,
    ClickAway,
    Ran,
    Hotkey,
    FocusFailed,
}

/// <summary>
/// The bar. Two states:
///  - idle: owned by the shell desktop window, bottom of the z-order, non-activating
///    (survives Win + D, never steals focus);
///  - active: unowned, topmost, foreground, keyboard focus in the input.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Color Violet = Color.FromRgb(0x8B, 0x5C, 0xF6);
    private const double IdleGlowLevel = 0.55;
    private const int MaxFocusAttempts = 5;

    private readonly History _history;
    private SlateConfig _config;
    private string _hotkeyDisplay = "Win + Space";

    private IntPtr _hwnd;
    private IntPtr _desktop;
    private IntPtr _previousForeground;
    private bool _active;
    private bool _summoning;
    private long _summonedAt;
    private int _focusAttempts;
    private uint _taskbarCreatedMsg;

    private readonly DispatcherTimer _guard;
    private Native.WinEventDelegate? _winEventProc; // keep alive
    private IntPtr _winEventHook;

    private readonly DropShadowEffect _glow = new() { ShadowDepth = 0 };
    private readonly GradientStop[] _shimmerStops = new GradientStop[3];

    /// <summary>Handles ":"-prefixed built-in commands.</summary>
    internal Action<string>? BuiltinHandler { get; set; }

    /// <summary>Shows a notification (title, message).</summary>
    internal Action<string, string>? Notify { get; set; }

    internal MainWindow(SlateConfig config, History history, string hotkeyDisplay)
    {
        InitializeComponent();
        _config = config;
        _history = history;

        GlowLayer.Effect = _glow;

        _guard = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _guard.Tick += (_, _) => KeepOnDesktop();

        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
        PreviewMouseLeftButtonDown += (_, _) => { if (!_active) Summon(); };
        Input.PreviewKeyDown += OnInputPreviewKeyDown;
        Input.TextChanged += (_, _) => UpdatePlaceholder();
        Closed += (_, _) => { if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook); };

        ApplyConfig(config, hotkeyDisplay);
    }

    public bool IsSummoned => _active;

    // ---------------------------------------------------------------------
    // Window plumbing
    // ---------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: no taskbar button, no Alt+Tab entry. No-activate while idle.
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, Native.WS_EX_APPWINDOW);

        _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        AttachToDesktop();

        _winEventProc = OnForegroundChanged;
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        _guard.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Explorer restarted: the desktop window is new, re-attach to it.
        if (msg == (int)_taskbarCreatedMsg && !_active)
            Dispatcher.BeginInvoke(AttachToDesktop);
        return IntPtr.Zero;
    }

    private void AttachToDesktop()
    {
        _desktop = Native.FindDesktopWindow();
        Native.SetOwner(_hwnd, _desktop);
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_active) return;

        // Show Desktop / clicking the desktop: make sure we're still owned by the window that's now in front.
        string cls = Native.GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW")
        {
            if (Native.FindDesktopWindow() != _desktop) AttachToDesktop();
            KeepOnDesktop();
        }
    }

    /// <summary>Safety net: if anything hid or minimized the bar while idle, bring it back without activating.</summary>
    private void KeepOnDesktop()
    {
        if (_active || _hwnd == IntPtr.Zero) return;
        if (!Native.IsWindow(_desktop)) AttachToDesktop();
        if (!Native.IsWindowVisible(_hwnd) || Native.IsIconic(_hwnd))
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
    }

    // ---------------------------------------------------------------------
    // Summon / dismiss
    // ---------------------------------------------------------------------

    public void Toggle()
    {
        if (_active) Dismiss(DismissReason.Hotkey);
        else Summon();
    }

    public void Summon()
    {
        if (_active || _hwnd == IntPtr.Zero) return;

        _active = true;
        _summonedAt = Environment.TickCount64;
        _focusAttempts = 0;
        _previousForeground = Native.GetForegroundWindow();

        // Detach from the desktop first; activating a desktop-owned window would drag the desktop forward.
        Native.SetExStyle(_hwnd, 0, Native.WS_EX_NOACTIVATE);
        Native.SetOwner(_hwnd, IntPtr.Zero);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);

        TakeFocus();
        AnimateState(active: true, pop: true);

        // Never trust that it worked: verify once input has been processed.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, VerifyFocus);
    }

    private void TakeFocus()
    {
        _summoning = true;
        try
        {
            Native.ForceForeground(_hwnd);
            Activate();
            Input.Focus();
            Keyboard.Focus(Input);
            Input.CaretIndex = Input.Text.Length;
        }
        finally
        {
            _summoning = false;
        }
    }

    private void VerifyFocus()
    {
        if (!_active) return;
        if (Native.GetForegroundWindow() == _hwnd && Input.IsKeyboardFocused) return;

        if (++_focusAttempts > MaxFocusAttempts)
        {
            // Better to back off visibly than to look focused while keys go elsewhere.
            Log.Write($"Focus failed; foreground is {Native.GetClassName(Native.GetForegroundWindow())}");
            Dismiss(DismissReason.FocusFailed);
            Notify?.Invoke("Slate", "Couldn't take keyboard focus. Press the hotkey again.");
            return;
        }

        TakeFocus();
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            VerifyFocus();
        };
        retry.Start();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_summoning || !_active) return;
        // Focus juggling during summon can produce a spurious deactivate.
        if (Environment.TickCount64 - _summonedAt < 200) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_active && Native.GetForegroundWindow() != _hwnd) Dismiss(DismissReason.ClickAway);
        });
    }

    public void Dismiss(DismissReason reason)
    {
        if (!_active) return;
        _active = false;

        _history.ResetNavigation();
        if (reason == DismissReason.Escape) Input.Clear();

        Native.SetWindowPos(_hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetOwner(_hwnd, _desktop);
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, 0);

        // Hand focus back to where the user was — except after running a command,
        // where the new terminal should take it.
        if (reason is DismissReason.Escape or DismissReason.Hotkey
            && _previousForeground != IntPtr.Zero && Native.IsWindow(_previousForeground))
        {
            Native.SetForegroundWindow(_previousForeground);
        }

        AnimateState(active: false, pop: false);
    }

    // ---------------------------------------------------------------------
    // Input
    // ---------------------------------------------------------------------

    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Submit();
                break;
            case Key.Escape:
                e.Handled = true;
                Dismiss(DismissReason.Escape);
                break;
            case Key.Up:
                e.Handled = true;
                SetInput(_history.Previous(Input.Text));
                break;
            case Key.Down:
                e.Handled = true;
                SetInput(_history.Next());
                break;
            case Key.Tab:
                e.Handled = true; // reserved for autocomplete (phase 2); don't let focus leave the box
                break;
        }
    }

    private void SetInput(string? text)
    {
        if (text == null) return;
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    private void Submit()
    {
        string text = Input.Text.Trim();
        if (text.Length == 0)
        {
            Dismiss(DismissReason.Escape);
            return;
        }

        if (text.StartsWith(':'))
        {
            Input.Clear();
            BuiltinHandler?.Invoke(text);
            Dismiss(DismissReason.Ran);
            return;
        }

        _history.Add(text);
        try
        {
            // We're the foreground process, so we may pass foreground rights to the terminal we start.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            CommandRunner.Run(text, _config);
        }
        catch (Exception ex)
        {
            Log.Error($"Running \"{text}\"", ex);
            Notify?.Invoke("Slate couldn't start the terminal", ex.Message);
            return;
        }

        PlayRunFlash();
        Input.Clear();
        Dismiss(DismissReason.Ran);
    }

    private void UpdatePlaceholder() =>
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------------
    // Config → visuals
    // ---------------------------------------------------------------------

    internal void ApplyConfig(SlateConfig config, string hotkeyDisplay)
    {
        _config = config;
        _hotkeyDisplay = hotkeyDisplay;
        var a = config.Appearance;

        double glowSize = Math.Max(0, a.GlowSize);
        double margin = glowSize + 10;
        double barWidth = Math.Max(200, a.Width);
        double barHeight = Math.Max(28, a.Height);

        Root.Margin = new Thickness(margin);
        Width = barWidth + margin * 2;
        Height = barHeight + margin * 2;

        var radius = new CornerRadius(Math.Max(0, a.CornerRadius));
        Bar.CornerRadius = radius;
        GlowLayer.CornerRadius = radius;
        FlashLayer.CornerRadius = radius;

        Bar.Background = Theme.Gradient(a.Background, a.BackgroundAngle, Color.FromRgb(0x1E, 0x0B, 0x45));
        Bar.BorderThickness = new Thickness(Math.Max(0, a.BorderThickness));
        Bar.BorderBrush = BuildBorderBrush(a);

        var glowColor = Theme.ParseColor(a.GlowColor, Violet);
        GlowLayer.Background = new SolidColorBrush(glowColor);
        _glow.Color = glowColor;
        _glow.BlurRadius = glowSize;
        _glow.Opacity = Math.Clamp(a.GlowOpacity, 0, 1);

        FlashLayer.Background = Theme.Solid(a.BorderHighlight, Colors.White);

        var font = new FontFamily(a.FontFamily);
        double fontSize = Math.Max(8, a.FontSize);
        var textBrush = Theme.Solid(a.TextColor, Colors.White);
        var promptColor = Theme.ParseColor(a.PromptColor, Violet);

        Prompt.Text = a.Prompt;
        Prompt.FontFamily = font;
        Prompt.FontSize = fontSize;
        Prompt.Foreground = new SolidColorBrush(promptColor);

        Input.FontFamily = font;
        Input.FontSize = fontSize;
        Input.Foreground = textBrush;
        Input.CaretBrush = new SolidColorBrush(promptColor);
        Input.SelectionBrush = new SolidColorBrush(glowColor);

        Placeholder.Text = a.Placeholder;
        Placeholder.FontFamily = font;
        Placeholder.FontSize = fontSize;
        Placeholder.Foreground = Theme.Solid(a.PlaceholderColor, Colors.Gray);
        UpdatePlaceholder();

        var placeholderColor = Theme.ParseColor(a.PlaceholderColor, Colors.Gray);
        Hint.Visibility = a.ShowHint ? Visibility.Visible : Visibility.Collapsed;
        Hint.BorderBrush = new SolidColorBrush(Theme.WithAlpha(Theme.ParseColor(a.BorderColor, Violet), 0.45));
        Hint.Background = new SolidColorBrush(Theme.WithAlpha(glowColor, 0.12));
        HintText.Text = string.IsNullOrWhiteSpace(a.HintText) ? _hotkeyDisplay : a.HintText;
        HintText.Foreground = new SolidColorBrush(placeholderColor);

        Position(margin, barHeight);
        StartIdleAnimations();
        AnimateState(_active, pop: false);
    }

    private Brush BuildBorderBrush(AppearanceConfig a)
    {
        var baseColor = Theme.ParseColor(a.BorderColor, Violet);
        var highlight = Theme.ParseColor(a.BorderHighlight, Colors.White);

        // [base … base, (base, highlight, base) moving band, base … base]
        _shimmerStops[0] = new GradientStop(baseColor, -0.4);
        _shimmerStops[1] = new GradientStop(highlight, -0.3);
        _shimmerStops[2] = new GradientStop(baseColor, -0.2);

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop(baseColor, 0));
        foreach (var stop in _shimmerStops) brush.GradientStops.Add(stop);
        brush.GradientStops.Add(new GradientStop(baseColor, 1));
        return brush;
    }

    private void Position(double margin, double barHeight)
    {
        var a = _config.Appearance;
        var wa = SystemParameters.WorkArea;

        Left = wa.Left + (wa.Width - Width) / 2 + a.OffsetX;
        Top = a.Anchor.Trim().ToLowerInvariant() switch
        {
            "bottom" => wa.Bottom - a.OffsetY - barHeight - margin,
            "center" => wa.Top + (wa.Height - Height) / 2 + a.OffsetY,
            _ => wa.Top + a.OffsetY - margin,
        };
    }

    // ---------------------------------------------------------------------
    // Animations
    // ---------------------------------------------------------------------

    private bool Animate(Func<AnimationConfig, bool> feature) =>
        _config.Animations.Enabled && feature(_config.Animations);

    private void StartIdleAnimations()
    {
        var anim = _config.Animations;
        var a = _config.Appearance;

        // Breathing glow.
        _glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        if (Animate(x => x.GlowPulse))
        {
            double peak = Math.Clamp(a.GlowOpacity, 0, 1);
            var breathe = new DoubleAnimation(peak * 0.5, peak, TimeSpan.FromSeconds(Math.Max(0.4, anim.GlowPulseSeconds / 2)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(breathe, 30);
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty, breathe);
        }

        // Highlight sweeping along the border.
        foreach (var stop in _shimmerStops) stop.BeginAnimation(GradientStop.OffsetProperty, null);
        if (Animate(x => x.BorderShimmer))
        {
            var sweep = TimeSpan.FromSeconds(1.8);
            var period = TimeSpan.FromSeconds(Math.Max(sweep.TotalSeconds, anim.BorderShimmerSeconds));
            for (int i = 0; i < _shimmerStops.Length; i++)
            {
                double delta = (i - 1) * 0.1;
                var frames = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
                frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(-0.3 + delta, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(1.3 + delta, KeyTime.FromTimeSpan(sweep),
                    new SineEase { EasingMode = EasingMode.EaseInOut }));
                frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.3 + delta, KeyTime.FromTimeSpan(period)));
                Timeline.SetDesiredFrameRate(frames, 30);
                _shimmerStops[i].BeginAnimation(GradientStop.OffsetProperty, frames);
            }
        }
    }

    private void AnimateState(bool active, bool pop)
    {
        var a = _config.Appearance;
        var duration = TimeSpan.FromMilliseconds(_config.Animations.Enabled ? Math.Max(1, _config.Animations.SummonDurationMs) : 1);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(active ? 1 : Math.Clamp(a.IdleOpacity, 0.05, 1), duration) { EasingFunction = ease });
        GlowLayer.BeginAnimation(OpacityProperty,
            new DoubleAnimation(active ? 1 : IdleGlowLevel, duration) { EasingFunction = ease });

        if (pop && Animate(x => x.SummonPop))
        {
            var scale = new DoubleAnimation(0.965, 1, duration)
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut },
            };
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }
    }

    private void PlayRunFlash()
    {
        if (!Animate(x => x.RunFlash)) return;

        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.22, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        FlashLayer.BeginAnimation(OpacityProperty, flash);

        var nudge = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        nudge.KeyFrames.Add(new EasingDoubleKeyFrame(7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        nudge.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)),
            new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }));
        PromptShift.BeginAnimation(TranslateTransform.XProperty, nudge);
    }
}
