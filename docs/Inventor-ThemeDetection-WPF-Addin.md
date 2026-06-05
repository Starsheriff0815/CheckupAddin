# Inventor Color Theme Detection for WPF/MVVM Add-ins


## 1. Inventor UI Themes Overview

Inventor 2021 introduced a formal two-theme UI system:

| Theme name (as returned by API) | User-visible label | Introduced |
|---|---|---|
| `"LightTheme"` | Light (default) | Inventor 2021 |
| `"DarkTheme"` | Dark | Inventor 2021 |

These are the only two themes. There is no "Sky Blue", "Classic", or third named theme in Inventor 2021–2026. The theme name strings are **not localized** — they are always the English identifiers regardless of the user's OS or Inventor language setting.

> **Do not confuse the UI theme with the 3D viewport color scheme.** `Application.ActiveColorScheme` controls background/model visualization colors in the canvas — it is a separate concept and is **not** a reliable indicator of the UI dark/light mode. See §4 for what to avoid.

Dark mode was briefly available in Inventor 2019, removed in 2020, then re-introduced properly in 2021 with the `ThemeManager` API. There is no public API for theme detection on Inventor 2018 or 2019.

---

## 2. API Availability by Version

| Inventor version | Recommended API | Notes |
|---|---|---|
| 2021–2026 | `app.ThemeManager.ActiveTheme.Name` | Official, strongly-typed. Returns `"LightTheme"` or `"DarkTheme"`. |
| 2019 (limited) | None | Dark mode existed briefly but no public detection API was provided. |
| 2018 and earlier | None | Light UI only; no theme detection needed. |

The `ThemeManager` property was added to the `Inventor.Application` object in the 2021 API release. If your add-in must support 2020 or earlier, wrap the call in a `try/catch` and default to Light — see the fallback pattern in §3.2.

---

## 3. Detection Patterns

### 3.1 Primary: `ThemeManager.ActiveTheme.Name` (Inventor 2021+)

```csharp
private static bool DetectDark(Inventor.Application app)
{
    try
    {
        string name = app.ThemeManager?.ActiveTheme?.Name ?? "";
        if (name.Length > 0)
            return name.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0;
    }
    catch { }

    return false; // default: Light
}
```

- Use null-conditional (`?.`) when accessing `ThemeManager` and `ActiveTheme` — the COM interop wrapper can return `null` on unexpected version mismatches.
- Matching `"Dark"` case-insensitively is more robust than an exact string equality check.
- The method returns `false` (Light) on any failure — Light is the safe default.

### 3.2 Fallback: `UserInterfaceManager.Theme` via late binding

This path was observed in some intermediate Inventor builds as a secondary route to the theme name. It is reached only if the primary path throws or returns an empty string. Use **dynamic/late binding** so the add-in does not fail to load on builds where the property does not exist:

```csharp
try
{
    dynamic theme = ((dynamic)app.UserInterfaceManager).Theme;
    string name = (theme?.Name ?? "").ToString().Trim();
    if (name.Length > 0)
        return name.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0;
}
catch { }
```

> This fallback is a defensive precaution. On all tested Inventor 2021–2026 builds, the primary `ThemeManager` path succeeds and this branch is never reached.

### 3.3 Combined detection (recommended)

```csharp
private static bool DetectDark(Inventor.Application app)
{
    // Pass 1: ThemeManager (Inventor 2021+)
    try
    {
        string n = app.ThemeManager?.ActiveTheme?.Name ?? "";
        if (n.Length > 0) return IsDarkName(n);
    }
    catch { }

    // Pass 2: late-binding fallback
    try
    {
        dynamic theme = ((dynamic)app.UserInterfaceManager).Theme;
        string n = (theme?.Name ?? "").ToString().Trim();
        if (n.Length > 0) return IsDarkName(n);
    }
    catch { }

    return false; // ultimate default: Light
}

private static bool IsDarkName(string name) =>
    name.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0;
```

---

## 4. What NOT to Use

| API / approach | Why it is wrong |
|---|---|
| `app.ActiveColorScheme` | Returns the **3D viewport** background color scheme (e.g. "Presentation", "Sky Blue"). Not related to the UI Light/Dark theme. |
| `UserApplicationOptions.xml` → `Colors/ColorSchemes` | Also 3D viewport schemes. |
| Windows registry `UIStyle\Ribbon\RibbonAppearance` | Unreliable; Inventor does not always update this synchronously. |
| `SystemParameters.HighContrast` / `Registry.GetValue("HKCU\\...AppsUseLightTheme")` | These read the **Windows OS** theme, not Inventor's theme. A user can run Inventor Dark inside a Light Windows session and vice versa. **Always read from Inventor's own API.** |
| `AppearanceManager.ActiveColorScheme` | Same as `ActiveColorScheme` — viewport only. |

---

## 5. Theme Change Events

**Inventor does not expose a theme-change event.** There is no equivalent of `SystemEvents.UserPreferenceChanged` for Inventor's UI theme.

Practical consequence: if the user changes Inventor's theme while the add-in window is open, the add-in will not automatically update its colors. The recommended approach is **detect once at window creation**; the user can close and reopen the add-in window to pick up the new theme. This is the same behavior as most native Inventor dialog panels.

If real-time reactivity is required, the only option is polling `ThemeManager.ActiveTheme.Name` on a timer — but this is not recommended for production add-ins.

---

## 6. WPF Integration: ResourceDictionary Swap

### 6.1 Architecture

Keep all theme-sensitive colors in two `ResourceDictionary` XAML files:

```
Resources/
  LightTheme.xaml   ← all brushes for the Light theme
  DarkTheme.xaml    ← all brushes for the Dark theme
```

Every WPF control binds to a **named brush key** using `{DynamicResource KeyName}`. At load time, `ThemeLoader` removes any previously merged theme dictionary and adds the correct one. Because the controls use `DynamicResource`, they update automatically when the dictionary changes.

### 6.2 ThemeLoader pattern

```csharp
internal static class ThemeLoader
{
    private static bool? _cached;

    // Call this once when the window is first shown (pass the Inventor Application).
    public static void ApplyTo(Window window, Inventor.Application app)
    {
        bool dark = DetectDark(app);
        _cached = dark;
        Apply(window, dark);
    }

    // Call this for child windows that open after the main window (reuses cached result).
    public static void ApplyTo(Window window)
    {
        Apply(window, _cached ?? false);
    }

    private static void Apply(Window window, bool dark)
    {
        string name = dark ? "Dark" : "Light";

        // Build the pack URI for the theme resource dictionary.
        // Replace "YourAssemblyName" with the value of your Assembly Name in project properties.
        var uri = new Uri(
            $"pack://application:,,,/YourAssemblyName;component/Resources/{name}Theme.xaml",
            UriKind.Absolute);

        var dicts = window.Resources.MergedDictionaries;

        // Remove any previously applied theme dict.
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            string src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("DarkTheme.xaml") || src.Contains("LightTheme.xaml"))
                dicts.RemoveAt(i);
        }

        dicts.Add(new ResourceDictionary { Source = uri });
    }

    private static bool DetectDark(Inventor.Application app)
    {
        try
        {
            string n = app.ThemeManager?.ActiveTheme?.Name ?? "";
            if (n.Length > 0) return n.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { }

        try
        {
            dynamic theme = ((dynamic)app.UserInterfaceManager).Theme;
            string n = (theme?.Name ?? "").ToString().Trim();
            if (n.Length > 0) return n.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { }

        return false;
    }
}
```

### 6.3 Call site

Apply the theme immediately after `InitializeComponent()`:

```csharp
public void SetViewModel(MyViewModel vm)
{
    DataContext = vm;
    ThemeLoader.ApplyTo(this, vm.AppInstance);   // must come after InitializeComponent()
}
```

For child windows (dialogs) that open after the main window, use the no-argument overload which reuses the cached result:

```csharp
var dialog = new MyDialog();
dialog.Owner = this;
ThemeLoader.ApplyTo(dialog);   // uses cached dark/light decision
dialog.ShowDialog();
```

### 6.4 XAML binding example

```xml
<Window Background="{DynamicResource MyWindowBackground}">
    <TextBlock Foreground="{DynamicResource MyPrimaryText}"
               Text="Hello Inventor"/>
    <Button Background="{DynamicResource MyButtonBackground}"
            Foreground="{DynamicResource MyButtonForeground}"
            Content="Click me"/>
</Window>
```

Define `MyWindowBackground` (and all other keys) in both `LightTheme.xaml` and `DarkTheme.xaml` with the appropriate color values. Both files **must define every key** — a missing key in one theme causes a `DynamicResource` to fall back to its default (usually transparent/black) and produces visible visual bugs.

---

## 7. DWM Title Bar Color (Windows 11)

By default a dark-themed WPF window still shows a white/light system title bar, which looks jarring. Use the Desktop Window Manager (DWM) API to match the title bar to your theme:

```csharp
[DllImport("dwmapi.dll")]
private static extern int DwmSetWindowAttribute(
    IntPtr hwnd, int attr, ref int attrValue, int attrSize);

private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // dark caption text/buttons
private const int DWMWA_CAPTION_COLOR           = 35; // custom caption background color (Win11 only)

// Call this after the window handle is created (e.g. in Loaded event or after ShowDialog).
private static void ApplyDwmTitleBar(Window window, bool dark)
{
    try
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Toggle dark caption text/buttons.
        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        // Set caption background color (Windows 11 22H2+).
        // COLORREF format: 0x00BBGGRR (note byte order reversal from HTML #RRGGBB).
        // Example dark: #2E3440 → R=0x2E, G=0x34, B=0x40 → COLORREF 0x00403 42E
        int captionColor = dark
            ? 0x40342E                          // dark header #2E3440
            : unchecked((int)0xFFFFFFFF);       // 0xFFFFFFFF = reset to system default
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
    }
    catch { }
    // Swallow silently — DWM attributes are a cosmetic enhancement.
    // They are not available on Windows 10 and fail gracefully.
}
```

**COLORREF byte order:** Windows COLORREF values are `0x00BBGGRR`, not `0x00RRGGBB`. To convert from an HTML color:
- `#2E3440` → R=`0x2E`, G=`0x34`, B=`0x40` → COLORREF `0x00403 42E`

**`DWMWA_CAPTION_COLOR` (attribute 35)** is only available on Windows 11 build 22000 and later. On Windows 10 the `DwmSetWindowAttribute` call returns an error code, which is safely swallowed.

**`DWMWA_USE_IMMERSIVE_DARK_MODE` (attribute 20)** is available from Windows 10 build 18985 (October 2019) and later.

---

## 8. Color Token Reference

The following table lists the resource key names, their purpose, and example values from a reference implementation. You may use different color values — the key names must match exactly between the C# `DynamicResource` references, `LightTheme.xaml`, and `DarkTheme.xaml`.

**Every key listed must be defined in both theme files.**

| Resource Key | Purpose | Dark value | Light value |
|---|---|---|---|
| `CheckupWindowBackground` | Main window + client area background | `#3B4453` | `#F0F0F0` |
| `CheckupRowBackground0` | Alternating row (even) | `#3B4453` | `#F0F0F0` |
| `CheckupRowBackground1` | Alternating row (odd) | `#353D4C` | `#EFF2F7` |
| `CheckupPrimaryText` | Primary label / value text | `#F5F5F5` | `#000000` |
| `CheckupSecondaryText` | Dimmed / secondary text | `#8090A8` | `#5C5C5C` |
| `CheckupLabelText` | Field label text | `#9AAABB` | `#323232` |
| `CheckupErrorText` | Error / invalid value highlight | `#FF6B6B` | `#CC0000` |
| `CheckupSeparator` | Row and column separator lines | `#4A5365` | `#D0D0D0` |
| `CheckupButtonBackground` | Standard button surface | `#2E3645` | `#E8E8E8` |
| `CheckupButtonBorder` | Standard button border | `#4A5570` | `#C0C0C0` |
| `CheckupButtonForeground` | Button label text | `#F5F5F5` | `#000000` |
| `CheckupSpecialButtonBackground` | Accent button — amber tint (e.g. Style Purger) | `#3D2A14` | `#FFEBD2` |
| `CheckupDestructiveButtonBackground` | Destructive button — red tint (e.g. Reset, Delete) | `#3D1820` | `#FFD2D2` |
| `CheckupApplyButtonBackground` | Confirm/Apply button — blue tint | `#1A3A5C` | `#DCEBFF` |
| `CheckupCancelButtonBackground` | Cancel/Close button — red tint | `#3D1820` | `#FFD2D2` |
| `CheckupActionItemForeground` | Dropdown action-item text (Add/Remove row etc.) | `#4CC2FF` | `#1A6FBF` |
| `CheckupGroupHeaderBackground` | Group header row background | `#2E3440` | `#EEEEEE` |
| `CheckupGroupHeaderForeground` | Group header label text | `#6A7A90` | `#505050` |
| `CheckupDragHandleFill` | Drag handle dot / grip color | `#5A6880` | `#AAAAAA` |
| `CheckupDragHighlight` | Drag-over row border accent | `#0696D7` | `#66BCE3` |
| `CheckupPresetActiveBorder` | Active-state button border (accent blue) | `#0696D7` | `#0696D7` |
| `CheckupPresetActiveBackground` | Active-state button background tint (10% alpha) | `#1A0696D7` | `#140696D7` |
| `CheckupSelectedCardBackground` | Selected / highlighted card background | `#1A3660` | `#66BCE3` |
| `CheckupSelectedRowBackground` | Drag-over row fill background | `#0A3D6E` | `#C0DCF5` |
| `CheckupLinkStripe` | Linked-field left-edge stripe | `#5BA3DE` | `#4E9BD6` |
| `CheckupSyncStripe` | Synced-field left-edge stripe | `#C8985A` | `#A06020` |
| `CheckupComboItemBackground` | ComboBox / dropdown item background | `#3B4453` | `#F0F0F0` |
| `CheckupComboItemHoverBackground` | ComboBox / dropdown item hover | `#0878B8` | `#D8E8FB` |
| `CheckupTabActiveBackground` | Active tab button background | `#3B4453` | `#F3F3F3` |
| `CheckupTabInactiveBackground` | Inactive tab button background | `#222933` | `#E0E0E0` |
| `CheckupScrollBarThumb` | Scrollbar thumb | `#5A6880` | `#AAAAAA` |
| `CheckupScrollBarThumbHover` | Scrollbar thumb on hover | `#8090A8` | `#888888` |
| `CheckupComboBoxBackground` | Popup/dropdown panel border fill | `#3B4453` | `#F0F0F0` |
| `CheckupEditableBackground` | TextBox editable background inside custom panels | `#2E3645` | `#FFFFFF` |

### WPF system brush overrides (dark theme only)

WPF built-in controls (ComboBox popups, tooltips, scrollbars) use system brush keys that default to Windows Light colors even inside a dark window. Override them in `DarkTheme.xaml` scoped to the window's resources:

```xml
<!-- In DarkTheme.xaml -->
<SolidColorBrush x:Key="{x:Static SystemColors.WindowBrushKey}"        Color="#3B4453"/>
<SolidColorBrush x:Key="{x:Static SystemColors.WindowTextBrushKey}"    Color="#F5F5F5"/>
<SolidColorBrush x:Key="{x:Static SystemColors.ControlBrushKey}"       Color="#3B4453"/>
<SolidColorBrush x:Key="{x:Static SystemColors.ControlTextBrushKey}"   Color="#F5F5F5"/>
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}"     Color="#0696D7"/>
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="#D8EEEB"/>
```

Because these are merged into `window.Resources.MergedDictionaries` (not `Application.Resources`), the overrides are **scoped to your add-in window only** and do not affect Inventor's own UI.

**In `LightTheme.xaml`** only the highlight pair needs overriding (the default system brushes are already light):

```xml
<!-- In LightTheme.xaml -->
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}"     Color="#66BCE3"/>
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="#FFFFFF"/>
```

### Dark palette anchor colors

The dark theme in this reference implementation is built around four anchor colors:

| Role | Hex | RGB |
|---|---|---|
| Header / DWM title bar | `#2E3440` | rgb(46, 52, 64) |
| Window / client area | `#3B4453` | rgb(59, 68, 83) |
| Accent / selection highlight | `#0696D7` | rgb(6, 150, 215) |
| Primary text | `#F5F5F5` | rgb(245, 245, 245) |

---

## 9. TextBox Dark Theme

WPF `TextBox` does not inherit window brush overrides automatically on all versions. Add an explicit implicit style in each theme file:

```xml
<!-- In DarkTheme.xaml -->
<Style TargetType="TextBox">
    <Setter Property="Background"     Value="#2E3645"/>
    <Setter Property="Foreground"     Value="#F5F5F5"/>
    <Setter Property="BorderBrush"    Value="#4A5570"/>
    <Setter Property="CaretBrush"     Value="#F5F5F5"/>
    <Setter Property="SelectionBrush" Value="#0696D7"/>
</Style>

<!-- In LightTheme.xaml -->
<Style TargetType="TextBox">
    <Setter Property="Background"  Value="#FFFFFF"/>
    <Setter Property="Foreground"  Value="#000000"/>
    <Setter Property="BorderBrush" Value="#ABADB3"/>
</Style>
```

These implicit styles apply to all `TextBox` instances inside the window without requiring explicit `Style=` attributes on every element.

---

## 10. Adding a Third Theme

The detection method returns `bool` (`isDark`). Supporting a third theme (e.g. a custom high-contrast variant) requires:

1. Change `DetectDark` to return an enum: `ThemeKind { Light, Dark, HighContrast }`.
2. Add a `HighContrastTheme.xaml` resource dictionary with the same key set.
3. Update `Apply()` to select the correct URI based on the enum value.

Since Inventor itself does not expose a third theme in its API (as of 2026), this path would be add-in-side only — useful if you want to offer a user-selectable override (e.g. a settings toggle) independently of Inventor's own setting.

---

## 11. Further Reading

| Topic | URL |
|---|---|
| ThemeManager API introduction (Inventor 2021 release notes) | https://adndevblog.typepad.com/manufacturing/2020/06/whats-new-in-inventor-2021-api-release.html |
| iLogic dark/light theme switcher example | https://clintbrown.co.uk/2020/05/09/ilogic-theme-switcher/ |
| Autodesk blog: theme-aware add-in patterns | https://blog.autodesk.io/handling-dark-and-light-theme-support-in-autodesk-inventor-add-ins/ |
| Inventor 2026 API: `ThemeManager.Themes` property | https://help.autodesk.com/view/INVNTOR/2026/ENU/?guid=ThemeManager_Themes |
| Inventor community: add-in Light/Dark forum thread | https://forums.autodesk.com/t5/inventor-programming-forum/addins-using-the-inventor-themes-light-dark/td-p/13356690 |
| DWMWA_USE_IMMERSIVE_DARK_MODE (Windows SDK) | https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute |
| DWMWA_CAPTION_COLOR (Windows 11 22H2+) | https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute |

---

## 12. Quick Checklist

When adding theme support to a new add-in:

- [ ] `LightTheme.xaml` and `DarkTheme.xaml` both define **every** resource key
- [ ] All WPF color references use `{DynamicResource KeyName}` — no hardcoded `Color=` or `Foreground="#..."`
- [ ] `ThemeLoader.ApplyTo(window, app)` called **after** `InitializeComponent()`, before the window is shown
- [ ] Child windows use `ThemeLoader.ApplyTo(window)` (no-argument overload) to reuse the cached decision
- [ ] System brush overrides (`SystemColors.WindowBrushKey` etc.) present in `DarkTheme.xaml`
- [ ] Implicit `TextBox` style present in both theme files
- [ ] DWM title bar attribute set **after the window handle exists** (Loaded event, not constructor)
- [ ] Detection does **not** read Windows OS dark mode, `ActiveColorScheme`, or any registry value
