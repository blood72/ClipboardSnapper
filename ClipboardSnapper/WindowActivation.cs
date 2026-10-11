using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace ClipboardSnapper;

internal static class WindowActivation
{
    internal static void Activate(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE preserves the previous maximized state.
        window.Activate();
        var popup = GetLastActivePopup(handle);
        if (SetForegroundWindow(popup != 0 ? popup : handle)) return;
        var flash = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Handle = handle,
            Flags = 0xE, Count = 3 // FLASHW_TRAY | FLASHW_TIMERNOFG
        };
        FlashWindowEx(ref flash);
    }

    internal static void ShowError(string message) => MessageBox(0, message, "ClipboardSnapper", 0x10);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Handle;
        public uint Flags, Count, Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")]
    private static extern nint GetLastActivePopup(nint handle);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint owner, string message, string title, uint flags);
}
