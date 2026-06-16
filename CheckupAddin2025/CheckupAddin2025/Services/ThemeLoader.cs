using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Detects Inventor's current UI color scheme and applies a matching ResourceDictionary.
    ///
    /// Detection order (all Inventor-specific — OS/Windows theme is intentionally NOT used):
    ///   1.  app.ThemeManager.ActiveTheme.Name — strongly-typed interop API, Inventor 2024+.
    ///   2.  app.UserInterfaceManager.Theme.Name via late binding — fallback.
    ///   3.  Default: Light.
    ///
    /// Properties intentionally NOT used (read 3D viewport color, not UI dark/light theme):
    ///   app.ActiveColorScheme, UserApplicationOptions.xml Colors/ColorSchemes.
    /// AppearanceManager does not exist on Inventor.Application.
    ///
    /// Theme colors live in Resources/DarkTheme.xaml and Resources/LightTheme.xaml.
    /// </summary>
    internal static class ThemeLoader
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR           = 35;
        // #2E3440 as COLORREF (0x00BBGGRR): R=0x2E G=0x34 B=0x40
        private const int DwmCaptionColorDark  = 0x40342E;
        // 0xFFFFFFFF tells DWM to restore the default caption color
        private const int DwmCaptionColorReset = unchecked((int)0xFFFFFFFF);

        private static bool? _cached;

        public static void ApplyTo(Window window, Inventor.Application app)
        {
            bool dark = DetectDark(app);
            _cached = dark;
            Apply(window, dark);
        }

        public static void ApplyTo(Window window)
        {
            Apply(window, _cached ?? false);
        }

        private static void Apply(Window window, bool dark)
        {
            string name = dark ? "Dark" : "Light";
            var uri = new Uri(
                "pack://application:,,,/CheckupAddIn2025;component/Resources/" + name + "Theme.xaml",
                UriKind.Absolute);
            var dicts = window.Resources.MergedDictionaries;
            for (int i = dicts.Count - 1; i >= 0; i--)
            {
                string src = dicts[i].Source?.OriginalString ?? "";
                if (src.Contains("DarkTheme.xaml") || src.Contains("LightTheme.xaml"))
                    dicts.RemoveAt(i);
            }
            dicts.Add(new ResourceDictionary { Source = uri });

            // Paint the OS title bar to match the WPF theme (Windows 10 19041+ / Windows 11).
            // EnsureHandle() forces HWND creation even before Show(), so the call succeeds when
            // ApplyTo is invoked during SetViewModel (before the window becomes visible).
            // Silently no-ops on older Windows builds.
            try
            {
                var helper = new WindowInteropHelper(window);
                IntPtr hwnd = helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();
                if (hwnd != IntPtr.Zero)
                {
                    int value = dark ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
                    int caption = dark ? DwmCaptionColorDark : DwmCaptionColorReset;
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                }
            }
            catch { }
        }

        private static bool DetectDark(Inventor.Application app)
        {
            try
            {
                string n = app.ThemeManager != null && app.ThemeManager.ActiveTheme != null
                    ? app.ThemeManager.ActiveTheme.Name ?? ""
                    : "";
                if (n.Length > 0) return IsDarkName(n);
            }
            catch { }

            try
            {
                dynamic theme = ((dynamic)app.UserInterfaceManager).Theme;
                string n = (theme != null ? theme.Name : null) ?? "";
                if (n.Length > 0) return IsDarkName(n.ToString().Trim());
            }
            catch { }

            return false;
        }

        private static bool IsDarkName(string name)
        {
            return name.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Dunk", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
