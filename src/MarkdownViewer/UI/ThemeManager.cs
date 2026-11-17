using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MarkdownViewer.UI;

public static class ThemeManager
{
    public static bool Apply(string theme, Window window)
    {
        var dark = theme == "Dark" || (theme == "System" && IsSystemDark());
        var colors = dark
            ? new[] { "#202123", "#191A1C", "#1C1D1F", "#E7E7E9", "#A4A8B1", "#34363B", "#303238", "#8BBCFF", "#293A50", "#191A1C", "#555B65", "#737C89", "#929BA7" }
            : new[] { "#FFFFFF", "#F3F4F5", "#F8F9FA", "#25272B", "#656C76", "#E2E5EA", "#E9ECF1", "#2765B3", "#E9F1FC", "#F3F4F5", "#B4BAC3", "#969EAA", "#747F8E" };
        var keys = new[] { "WindowBrush", "ChromeBrush", "SidebarBrush", "TextBrush", "MutedBrush", "LineBrush", "HoverBrush", "AccentBrush", "SelectedBrush", "ScrollTrackBrush", "ScrollThumbBrush", "ScrollThumbHoverBrush", "ScrollThumbPressedBrush" };
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            Application.Current.Resources[keys[i]] = brush;
        }
        Application.Current.Resources[SystemColors.ControlBrushKey] = Application.Current.Resources["ScrollTrackBrush"];
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            var enabled = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        }
        return dark;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException) { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
