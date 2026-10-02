using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Slate.Interop;

/// <summary>
/// Global hotkey via a low-level keyboard hook. Unlike RegisterHotKey this can take
/// combinations Windows reserves for itself, such as Win + Space.
/// </summary>
internal sealed class HotkeyHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // keep the delegate alive for the hook's lifetime
    private IntPtr _hook;
    private bool _swallowKeyUp;

    public Hotkey Hotkey { get; set; }

    /// <summary>Raised on the thread that created the hook (the UI thread). Keep handlers fast.</summary>
    public event Action? Pressed;

    public HotkeyHook(Hotkey hotkey)
    {
        Hotkey = hotkey;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & Native.LLKHF_INJECTED) == 0 && k.vkCode == Hotkey.Key)
            {
                int msg = wParam.ToInt32();
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;

                if (down && Hotkey.ModifiersHeld())
                {
                    // Tap an unassigned key so releasing Win/Alt doesn't open Start or a menu bar.
                    if (Hotkey.Win || Hotkey.Alt) Native.TapKey(Native.VK_MASK);

                    // Fire once per physical press, ignore auto-repeat.
                    if (!_swallowKeyUp) Pressed?.Invoke();
                    _swallowKeyUp = true;
                    return new IntPtr(1);
                }

                if (!down && _swallowKeyUp)
                {
                    _swallowKeyUp = false;
                    return new IntPtr(1);
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
