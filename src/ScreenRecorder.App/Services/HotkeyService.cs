using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;

namespace ScreenRecorder.App.Services;

public class HotkeyService
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const int HOTKEY_ID = 9000;

    public void Register(Window window)
    {
        var helper = new WindowInteropHelper(window);
        RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, 0x52); // Ctrl + Alt + R
    }

    public void Unregister(Window window)
    {
        var helper = new WindowInteropHelper(window);
        UnregisterHotKey(helper.Handle, HOTKEY_ID);
    }
}