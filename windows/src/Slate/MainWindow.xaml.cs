using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    // Up to ~1 s: shell panels (Start, Search, tray overflow) take a few hundred ms to close.
    private const int MaxFocusAttempts = 15;
    private static readonly string[] ShellFlyouts =
    {
        "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

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

    // Geometry in DIPs, kept from the last ApplyConfig for positioning.
    private double _margin;
    private double _barHeight;

    // Completion: Tab/Shift+Tab cycle through matches for the last word; ghost text previews the first.
    private readonly Projects _projects = new();
    private readonly GitHubRepos _github = new();
    private readonly Completer _completer;
    private bool _dismissedShellFlyout;
    private IReadOnlyList<string> _cycle = Array.Empty<string>();
    private int _cycleIndex = -1;
    private int _tokenStart;
    private bool _completing;
    private string? _ghostMatch;
    private bool _overlayQueued;

    /// <summary>Handles ":"-prefixed built-in commands.</summary>
    internal Action<string>? BuiltinHandler { get; set; }

    /// <summary>Shows a notification (title, message).</summary>
    internal Action<string, string>? Notify { get; set; }

    /// <summary>Persists the config (used after dragging the bar).</summary>
    internal Action<SlateConfig>? SaveConfig { get; set; }

    internal MainWindow(SlateConfig config, History history, string hotkeyDisplay)
    {
        InitializeComponent();
        _config = config;
        _history = history;
        _completer = new Completer(_projects);

        GlowLayer.Effect = _glow;

        _guard = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _guard.Tick += (_, _) => KeepOnDesktop();

        SourceInitialized += OnSourceInitialized;
        Activated += (_, _) => QueueOverlay();
        Deactivated += OnDeactivated;
        PreviewMouseLeftButtonDown += (_, _) => { if (!_active) Summon(); };
        Bar.MouseLeftButtonDown += OnBarMouseDown;
        Input.PreviewKeyDown += OnInputPreviewKeyDown;
        Input.TextChanged += OnInputTextChanged;
        Input.SelectionChanged += (_, _) => QueueOverlay();
        Input.SizeChanged += (_, _) => QueueOverlay();
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

        Position();
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
        _dismissedShellFlyout = false;
        _previousForeground = Native.GetForegroundWindow();
        _completer.Refresh(_config);
        _github.RefreshIfStale();

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

        // Start menu, Search, the tray overflow and other shell panels refuse to give up focus.
        // Close them with Esc first, the same as the user would, then take focus.
        if (!_dismissedShellFlyout && Array.IndexOf(ShellFlyouts, Native.GetClassName(Native.GetForegroundWindow())) >= 0)
        {
            _dismissedShellFlyout = true;
            Native.TapKey(Native.VK_ESCAPE);
        }

        TakeFocus();
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
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
                e.Handled = true; // also keeps focus from leaving the box
                CycleCompletion(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case Key.Right or Key.End when _ghostMatch != null && Input.CaretIndex == Input.Text.Length:
                e.Handled = true;
                AcceptGhost();
                break;
        }
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_completing) _cycleIndex = -1; // user typed: start a fresh completion next Tab
        UpdatePlaceholder();
        UpdateGhost();
        QueueOverlay();
    }

    // ---------------------------------------------------------------------
    // Completion
    // ---------------------------------------------------------------------

    /// <summary>The word being typed, if the caret is at the end and it's an argument (not the command itself).</summary>
    private bool TryGetToken(out int start, out string token)
    {
        string text = Input.Text;
        start = 0;
        token = string.Empty;
        if (Input.CaretIndex != text.Length) return false;

        int lastSpace = text.LastIndexOf(' ');
        if (lastSpace < 0) return false;

        start = lastSpace + 1;
        token = text[start..];
        return true;
    }

    /// <summary>GitHub repo names after "git clone " / "gc ", folder names everywhere else.</summary>
    private IReadOnlyList<string> Completions(string token)
    {
        var words = Input.Text.Split(' ');
        bool cloning = (words.Length == 2 && words[0] == "gc") || (words.Length == 3 && words[0] == "git" && words[1] == "clone");
        return cloning ? _github.Completions(token) : _completer.Match(token);
    }

    private void CycleCompletion(int direction)
    {
        if (_cycleIndex < 0)
        {
            if (!TryGetToken(out _tokenStart, out string token)) return;
            _cycle = Completions(token.Trim('\''));
            if (_cycle.Count == 0) return;
            _cycleIndex = direction > 0 ? 0 : _cycle.Count - 1;
        }
        else
        {
            _cycleIndex = (_cycleIndex + direction + _cycle.Count) % _cycle.Count;
        }
        ReplaceToken(_tokenStart, Completer.Quote(_cycle[_cycleIndex]));
    }

    private void AcceptGhost()
    {
        if (_ghostMatch == null || !TryGetToken(out int start, out _)) return;
        ReplaceToken(start, _ghostMatch);
        _cycleIndex = -1;
        UpdateGhost();
    }

    private void ReplaceToken(int start, string value)
    {
        _completing = true;
        try
        {
            Input.Text = Input.Text[..start] + value;
            Input.CaretIndex = Input.Text.Length;
        }
        finally
        {
            _completing = false;
        }
    }

    private void UpdateGhost()
    {
        _ghostMatch = null;
        string token = string.Empty;
        if (_cycleIndex < 0 && TryGetToken(out _, out token) && token.Length > 0)
        {
            string? match = Completions(token).FirstOrDefault();
            // Only names that need no quoting can be previewed as a plain suffix.
            if (match != null && match.Length > token.Length
                && match.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                && Completer.Quote(match) == match)
            {
                _ghostMatch = match;
            }
        }

        Ghost.Text = _ghostMatch?[token.Length..] ?? string.Empty;
        Ghost.Visibility = _ghostMatch != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------------
    // Overlay: ghost text + smooth caret
    // ---------------------------------------------------------------------

    private bool SmoothCaretOn => Animate(x => x.SmoothCaret);

    private void QueueOverlay()
    {
        if (_overlayQueued) return;
        _overlayQueued = true;
        // After layout, so the TextBox's character rects are current.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _overlayQueued = false;
            UpdateOverlay();
        });
    }

    private void UpdateOverlay()
    {
        if (Ghost.Visibility == Visibility.Visible)
        {
            var end = Input.GetRectFromCharacterIndex(Input.Text.Length);
            if (!end.IsEmpty)
            {
                var p = Input.TranslatePoint(end.TopLeft, Overlay);
                Canvas.SetLeft(Ghost, p.X);
                Canvas.SetTop(Ghost, p.Y);
            }
        }

        bool show = SmoothCaretOn && IsActive && Input.IsKeyboardFocused && Input.SelectionLength == 0;
        if (!show)
        {
            Caret.BeginAnimation(OpacityProperty, null);
            Caret.Opacity = 0;
            return;
        }

        var r = Input.GetRectFromCharacterIndex(Input.CaretIndex);
        if (r.IsEmpty) return;
        var pos = Input.TranslatePoint(r.TopLeft, Overlay);

        Caret.Height = r.Height;
        Canvas.SetTop(Caret, pos.Y);
        if (double.IsNaN(Canvas.GetLeft(Caret)))
        {
            Canvas.SetLeft(Caret, pos.X);
        }
        else
        {
            Caret.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(pos.X, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        // Solid while typing, then a soft fade in and out.
        var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100), RepeatBehavior = RepeatBehavior.Forever };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(0.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        Caret.BeginAnimation(OpacityProperty, blink);
    }

    // ---------------------------------------------------------------------
    // Drag to reposition
    // ---------------------------------------------------------------------

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks inside the text box are handled by the box; this is the frame, prompt and hint.
        if (e.ClickCount != 1 || _hwnd == IntPtr.Zero) return;

        Native.GetWindowRect(_hwnd, out var before);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return; // button already released
        }
        Native.GetWindowRect(_hwnd, out var after);

        if (after.Left != before.Left || after.Top != before.Top) SavePosition(after);
        if (_active) Keyboard.Focus(Input);
    }

    /// <summary>Converts the dragged window position back into monitor + anchor offsets and saves them.</summary>
    private void SavePosition(Native.RECT r)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = System.Windows.Forms.Screen.FromHandle(_hwnd);
        var wa = screen.WorkingArea;
        double s = Native.ScaleAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        double winW = r.Right - r.Left;
        double winH = r.Bottom - r.Top;

        var a = _config.Appearance;
        a.Monitor = screen.Primary ? 0 : Array.FindIndex(screens, x => x.DeviceName == screen.DeviceName) + 1;
        a.OffsetX = Math.Round((r.Left - (wa.Left + (wa.Width - winW) / 2)) / s);
        a.OffsetY = Math.Round(a.Anchor.Trim().ToLowerInvariant() switch
        {
            "bottom" => (wa.Bottom - r.Top) / s - _barHeight - _margin,
            "center" => (r.Top - wa.Top - (wa.Height - winH) / 2) / s,
            _ => (r.Top - wa.Top) / s + _margin,
        });

        SaveConfig?.Invoke(_config);
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

        // "git clone ladder" / "gc ladder": find the repo on GitHub and clone it into the default folder.
        if (CloneTarget(text) is string repo)
        {
            _history.Add(text);
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            PlayRunFlash();
            Input.Clear();
            Dismiss(DismissReason.Ran);
            Clone(repo);
            return;
        }

        // Built-in shortcut ("cc slate", "vs new airlink"): find the folder, run the command inside it.
        string command = text;
        string? folder = null;
        int space = text.IndexOf(' ');
        string key = space < 0 ? text : text[..space];
        if (_config.Shortcuts.TryGetValue(key, out var shortcut))
        {
            string query = space < 0 ? string.Empty : text[(space + 1)..].Trim();

            // Options after the folder go to the command: "cc slate -r" → claude -r, inside slate.
            string args = string.Empty;
            int dash = query.StartsWith('-') ? 0 : query.IndexOf(" -", StringComparison.Ordinal);
            if (dash >= 0)
            {
                args = query[dash..].Trim();
                query = query[..dash].Trim();
            }

            if (query.Length > 0)
            {
                folder = _projects.Resolve(query, _config);
                if (folder == null)
                {
                    Notify?.Invoke("Slate", $"No folder matches \"{query}\". Open it once in a terminal, or add its parent to projectRoots.");
                    return; // keep the text so it can be fixed
                }
                _projects.Visited(folder, _config);
            }
            command = args.Length == 0 || shortcut.Trim().Length == 0 ? shortcut : $"{shortcut} {args}";
        }

        _history.Add(text);
        try
        {
            // We're the foreground process, so we may pass foreground rights to the terminal we start.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            CommandRunner.Run(command, _config, folder,
                error => Dispatcher.BeginInvoke(() => Notify?.Invoke("Slate", error)));
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

    // ---------------------------------------------------------------------
    // GitHub clone
    // ---------------------------------------------------------------------

    /// <summary>The repo in "git clone &lt;name&gt;" or "gc &lt;name&gt;", if it isn't already a URL or path.</summary>
    internal static string? CloneTarget(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string target;
        if (words.Length == 2 && words[0] == "gc") target = words[1];
        else if (words.Length == 3 && words[0] == "git" && words[1] == "clone") target = words[2];
        else return null;
        bool urlOrPath = target.Contains("://") || target.Contains('@') || target.EndsWith(".git")
                         || target.StartsWith('.') || target.StartsWith('~') || target.Contains('\\') || target.Contains(':');
        return urlOrPath ? null : target;
    }

    private void Clone(string query)
    {
        var config = _config;
        System.Threading.Tasks.Task.Run(() =>
        {
            IReadOnlyList<string>? matches = null;
            string? error = null;
            try { matches = _github.Find(query); }
            catch (Exception ex) { error = ex.Message; }

            Dispatcher.BeginInvoke(() =>
            {
                if (error != null) Notify?.Invoke("Slate", error);
                else if (matches!.Count == 0) Notify?.Invoke("Slate", $"None of your GitHub repos is called \"{query}\".");
                else if (matches.Count > 1)
                    Notify?.Invoke($"Several repos match \"{query}\"",
                        string.Join(", ", matches.Take(4)) + (matches.Count > 4 ? ", …" : "") + ". Type git clone owner/name.");
                else RunClone(matches[0], config);
            });
        });
    }

    /// <summary>Clones into the default folder in a terminal (so progress and errors are visible) and stays in the new repo.</summary>
    private void RunClone(string repo, SlateConfig config)
    {
        string name = repo[(repo.LastIndexOf('/') + 1)..];
        string baseDir = CommandRunner.ResolveWorkingDirectory(config.WorkingDirectory);
        _projects.Visited(System.IO.Path.Combine(baseDir, name), config); // so "cc <name>" works right after
        string quotedRepo = "'" + repo.Replace("'", "''") + "'";
        string quotedName = "'" + name.Replace("'", "''") + "'";
        try
        {
            CommandRunner.Run($"gh repo clone {quotedRepo}; if ($LASTEXITCODE -eq 0) {{ Set-Location {quotedName} }}", config, null,
                error => Dispatcher.BeginInvoke(() => Notify?.Invoke("Slate", error)));
        }
        catch (Exception ex)
        {
            Log.Error($"Cloning {repo}", ex);
            Notify?.Invoke("Slate couldn't start the terminal", ex.Message);
        }
    }

    /// <summary>A folder for ":cd &lt;name&gt;": a project found the way "cc &lt;name&gt;" finds one.</summary>
    internal string? ResolveFolder(string query) => _projects.Resolve(query, _config);

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

        _margin = margin;
        _barHeight = barHeight;
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
        Input.SelectionBrush = new SolidColorBrush(glowColor);

        // The smooth caret replaces the native one; the native one stays as the fallback.
        Input.CaretBrush = SmoothCaretOn ? Brushes.Transparent : new SolidColorBrush(promptColor);
        Caret.Fill = new SolidColorBrush(promptColor);

        Ghost.FontFamily = font;
        Ghost.FontSize = fontSize;
        Ghost.Foreground = Theme.Solid(a.PlaceholderColor, Colors.Gray);
        UpdateGhost();
        QueueOverlay();

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

        // Folder chip: folder icon + where commands run, tinted like the prompt so it reads as live state.
        string folder = CommandRunner.ResolveWorkingDirectory(config.WorkingDirectory);
        var chipBrush = new SolidColorBrush(Theme.WithAlpha(promptColor, 0.85));
        FolderChip.Visibility = a.ShowFolder ? Visibility.Visible : Visibility.Collapsed;
        FolderChip.Background = new SolidColorBrush(Theme.WithAlpha(promptColor, 0.14));
        FolderChip.ToolTip = folder;
        FolderIcon.Foreground = chipBrush;
        FolderText.Foreground = chipBrush;
        FolderText.Text = ShortFolder(folder);
        VersionText.Visibility = a.ShowVersion ? Visibility.Visible : Visibility.Collapsed;
        VersionText.Text = $"v{Updater.CurrentVersion.ToString(3)}";
        VersionText.Foreground = new SolidColorBrush(Theme.WithAlpha(promptColor, 0.9));
        VersionGlow.Color = glowColor;

        Position();
        StartIdleAnimations();
        AnimateState(_active, pop: false);
    }

    /// <summary>"~\\code\\slate"; long paths keep their end: "…\\secpo\\slate".</summary>
    private static string ShortFolder(string path)
    {
        string s = CommandRunner.Abbreviate(path);
        if (s.Length <= 28) return s;
        var parts = s.TrimEnd('\\').Split('\\');
        return parts.Length >= 2 ? $"…\\{parts[^2]}\\{parts[^1]}" : s;
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

    /// <summary>
    /// Places the window on the configured monitor. Done in physical pixels because monitors
    /// can have different scaling; offsets in the config are DIPs on the target monitor.
    /// </summary>
    private void Position()
    {
        if (_hwnd == IntPtr.Zero) return; // called again from SourceInitialized

        var a = _config.Appearance;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = a.Monitor >= 1 && a.Monitor <= screens.Length
            ? screens[a.Monitor - 1]
            : System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
        var wa = screen.WorkingArea;
        double s = Native.ScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);

        double winW = Width * s;
        double winH = Height * s;
        double x = wa.Left + (wa.Width - winW) / 2 + a.OffsetX * s;
        double y = a.Anchor.Trim().ToLowerInvariant() switch
        {
            "bottom" => wa.Bottom - (a.OffsetY + _barHeight + _margin) * s,
            "center" => wa.Top + (wa.Height - winH) / 2 + a.OffsetY * s,
            _ => wa.Top + (a.OffsetY - _margin) * s,
        };

        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
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

    /// <summary>
    /// Renders the bar, as it looks when summoned, to a PNG on a dark backdrop without showing a window.
    /// Used for README screenshots and for previewing a theme: Slate.exe --render-preview out.png "text".
    /// </summary>
    internal void RenderPreview(string path, string text, Color backdrop)
    {
        var a = _config.Appearance;

        // Freeze every animation at a flattering frame.
        Root.BeginAnimation(OpacityProperty, null);
        GlowLayer.BeginAnimation(OpacityProperty, null);
        _glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        Root.Opacity = 1;
        GlowLayer.Opacity = 1;
        _glow.Opacity = Math.Clamp(a.GlowOpacity, 0, 1);
        for (int i = 0; i < _shimmerStops.Length; i++)
        {
            _shimmerStops[i].BeginAnimation(GradientStop.OffsetProperty, null);
            _shimmerStops[i].Offset = 0.32 + (i - 1) * 0.1;
        }

        Input.Text = text;
        Caret.Opacity = 0;

        var size = new Size(Width, Height);
        Root.Measure(size);
        Root.Arrange(new Rect(size));
        Root.UpdateLayout();

        const double scale = 2; // crisp on high-DPI screens and GitHub
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(size));
        rtb.Render(background);
        rtb.Render(Root);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
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
