using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ClipboardSnapper;

internal static class WindowPlacement
{
    public static void Apply(AppWindow window)
    {
        var display = GetCursorPos(out var cursor)
            ? DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
        if (window.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
        // AppWindow bounds and DisplayArea.WorkArea are physical pixels, not XAML DIPs.
        window.MoveAndResize(Calculate(display.WorkArea));
    }

    internal static RectInt32 Calculate(RectInt32 area)
    {
        var width = Math.Min(1300, area.Width);
        var height = Math.Min(860, area.Height);
        return new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
