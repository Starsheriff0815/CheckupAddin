using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using CheckupAddIn.ViewModels;
using CheckupAddIn.Views;

namespace CheckupAddIn.DesignHarness
{
    /// <summary>
    /// Build-time DESIGNER harness (Task #36). Launches the add-in's real windows with dummy
    /// data — no Inventor — and offers live tweaks of theme, font, window size, and per-key
    /// theme colors. One shared theme ResourceDictionary instance is attached to every open
    /// preview window, so a color edit updates them all at once (the harness shell itself is
    /// untouched). "Export" writes color changes back to the active theme's source .xaml.
    /// </summary>
    public partial class HarnessWindow : Window
    {
        private readonly List<Window> _open = new();
        private readonly Dictionary<int, Window> _openByKind = new();   // picker index → its open window (reload, don't stack)
        private Window _active;
        private ResourceDictionary _themeDict;          // shared instance across all preview windows
        private string _activeTheme = "Dark";           // "Dark" | "Light"
        private readonly ObservableCollection<ColorItem> _colors = new();
        private readonly HashSet<string> _changed = new();
        private readonly ObservableCollection<LabelItem> _labels = new();
        private readonly HashSet<string> _changedLabels = new();
        private ResourceDictionary _langDict;            // shared language dict across all preview windows
        private string _activeLang = "DE";               // "DE" | "EN"
        private bool _fontChanged, _sizeChanged;         // pending typography edits for export

        public HarnessWindow()
        {
            InitializeComponent();
            ColorList.ItemsSource = _colors;
            LabelList.ItemsSource = _labels;
            // Defer the pack-URI theme load until the window is up (pack scheme + referenced
            // assembly are reliably ready then) and guard it so a failure can't crash startup.
            Loaded += (_, __) =>
            {
                try
                {
                    UiStateStore.DesignMode = true;   // windows open at factory sizes, not persisted ones
                    LanguageLoader.Detect();          // populate strings so InfoPanelBuilder (Info windows) shows real text
                    LoadTheme();
                    RefreshColors();
                    LoadLang();
                    RefreshLabels();
                    Status($"Ready — {_colors.Count} colors, {_labels.Count} labels ({_activeLang}). Pick a window and click Open.");
                }
                catch (Exception ex) { Status("Theme load failed: " + ex.Message); }
            };
        }

        // ── Preview launch ───────────────────────────────────────────────
        private void OpenBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int kind = WindowPicker.SelectedIndex;
                // Reload semantics: if this window type is already open, close it first so we
                // replace it in place instead of stacking another copy on screen.
                if (_openByKind.TryGetValue(kind, out var existing)) { try { existing.Close(); } catch { } }

                Window w = CreateWindow(kind);
                if (w == null) return;
                w.Tag = kind;   // so size-export knows which window XAML to write

                AttachTheme(w);
                AttachLang(w);
                w.FontFamily = CurrentFontFamily();
                w.FontSize   = FontSizeSlider.Value;

                _open.Add(w);
                _openByKind[kind] = w;
                _active = w;
                w.Closed += (_, __) =>
                {
                    _open.Remove(w);
                    if (_openByKind.TryGetValue(kind, out var cur) && ReferenceEquals(cur, w)) _openByKind.Remove(kind);
                    if (ReferenceEquals(_active, w)) _active = null;
                };

                // Reflect the active window's live size in the W/H fields: factory size on open, and
                // updating as the user drag-resizes — so they tune visually then fine-tune exact numbers.
                w.Activated   += (_, __) => { _active = w; UpdateSizeFields(w); };
                w.SizeChanged += (_, __) => { if (ReferenceEquals(_active, w)) UpdateSizeFields(w); };

                // No auto ApplySize() — that overrode each window's factory size with the panel value.
                w.Show();
                w.Activate();
                UpdateSizeFields(w);
                Status($"Opened '{((ComboBoxItem)WindowPicker.SelectedItem).Content}'. Theme={_activeTheme}; {_open.Count} open.");
            }
            catch (Exception ex) { Status("Open failed: " + ex.Message); }
        }

        private Window CreateWindow(int index)
        {
            switch (index)
            {
                case 0:
                    return new CheckupWindow { DataContext = new CheckupViewModel() };

                case 1: // Logics-Constructor (Catalog + Capabilities tabs) — seed data from the harness bin
                {
                    string local = Path.Combine(Path.GetTempPath(), "CheckupHarness");
                    Directory.CreateDirectory(local);
                    var catStore = CatalogStore.Load(AppContext.BaseDirectory, local);
                    var capStore = CapabilityStore.Load(AppContext.BaseDirectory, local);
                    // Harness-only: unlock the loaded (Demo) catalogs/capabilities IN MEMORY so a designer
                    // can exercise collapse/rearrange/edit. Never persisted — the harness only ever Load()s;
                    // the source .catalog.json / .capability.json files are not written.
                    foreach (var c in catStore.Catalogs)       c.IsLocked = false;
                    foreach (var s in capStore.CapabilitySets) s.IsLocked = false;
                    var fields = new CheckupViewModel().FieldCatalog;   // dummy target fields
                    var vm = new CatalogBuilderViewModel(catStore, capStore, fields);
                    // Ensure the designer sees capability groups immediately; normally the app restores
                    // the last-used set from the registry, but on a clean machine that lookup returns
                    // nothing and the Capabilities tab would show an empty "no set selected" state.
                    if (vm.SelectedCapabilitySet == null && vm.CapabilitySets.Count > 0)
                        vm.SelectedCapabilitySet = vm.CapabilitySets[0];
                    var w = new CatalogBuilderWindow();
                    w.Initialize(vm, null);
                    return w;
                }

                case 2: // Catalog Picker — empty content is enough to preview chrome/theme
                    return new CatalogPickerWindow(
                        new List<CatalogDropdownItem>(), new List<CatalogTabEntry>(), "demo", null);

                case 3: // Info — Main window help (real content from the language catalog)
                    return new InfoDialog(InfoPanelBuilder.BuildMainWindowHelp(),
                        "MainAddin", "Win_Title_CheckupInfo", 520, 480);

                case 4: // Info — Logics-Constructor (Roles / Logics)
                    return new InfoDialog(InfoPanelBuilder.BuildRoleHelp(),
                        "RoleHelp", "Win_Title_LogicConstructorInfo", 600, 700);

                case 5: // Info — Logics-Constructor (Cards)
                    return new InfoDialog(InfoPanelBuilder.BuildCardHelp(),
                        "CardHelp", "Win_Title_LogicConstructorInfo", 650, 750);

                case 6:
                    return new InputDialog("Sample value");

                case 7:
                    return new PresetPickerDialog(Array.Empty<PresetData>());

                default:
                    return null;
            }
        }

        // ── Theme (shared dict) ──────────────────────────────────────────
        private void LoadTheme()
        {
            _themeDict = new ResourceDictionary
            {
                Source = new Uri($"/CheckupAddIn;component/Resources/{_activeTheme}Theme.xaml", UriKind.Relative)
            };
        }

        private void AttachTheme(Window w)
        {
            StripThemeDicts(w);
            w.Resources.MergedDictionaries.Add(_themeDict);
        }

        private static void StripThemeDicts(Window w)
        {
            var md = w.Resources.MergedDictionaries;
            for (int i = md.Count - 1; i >= 0; i--)
            {
                string src = md[i].Source?.OriginalString ?? "";
                if (src.Contains("DarkTheme") || src.Contains("LightTheme")) md.RemoveAt(i);
            }
        }

        private void Theme_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;   // ignore the Checked event raised during InitializeComponent
            _activeTheme = (ThemeLight?.IsChecked == true) ? "Light" : "Dark";
            LoadTheme();
            foreach (var w in _open) AttachTheme(w);   // strip old + attach new shared dict
            _changed.Clear();
            RefreshColors();
            Status($"Theme switched to {_activeTheme}. ({_open.Count} window(s) updated)");
        }

        // ── Font ─────────────────────────────────────────────────────────
        private FontFamily CurrentFontFamily()
        {
            if (FontFamilyBox?.SelectedItem is ComboBoxItem it)
                try { return new FontFamily((string)it.Content); } catch { }
            return new FontFamily("Segoe UI");
        }

        private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            var ff = CurrentFontFamily();
            foreach (var w in _open) w.FontFamily = ff;
            if (_themeDict != null) _themeDict["CheckupFontFamily"] = ff;   // drives DynamicResource + export
            _fontChanged = true;
        }

        private void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            if (FontSizeLabel != null) FontSizeLabel.Text = $"{(int)e.NewValue} pt";
            foreach (var w in _open) w.FontSize = e.NewValue;
            if (_themeDict != null) _themeDict["CheckupBaseFontSize"] = e.NewValue;
            _sizeChanged = true;
        }

        // ── Size (applies to the active/last-opened window) ──────────────
        private void ApplySize_Click(object sender, RoutedEventArgs e) => ApplySize();
        private void ApplySize()
        {
            if (_active == null) return;
            if (double.TryParse(WidthBox.Text, out double w)  && w > 0) _active.Width  = w;
            if (double.TryParse(HeightBox.Text, out double h) && h > 0) _active.Height = h;
        }

        // Writes the active window's current W/H back to its Window-root XAML (factory size in code).
        // Only windows whose size is a root XAML attribute are supported; Info dialogs get their size
        // from caller code, so they're reported as not-exportable. Targets the 2026 variant's XAML
        // (window XAML isn't part of the theme/language linking — fan-out is a consolidation concern).
        private void ExportSize_Click(object sender, RoutedEventArgs e)
        {
            if (_active == null) { Status("Open a window first."); return; }
            int kind = _active.Tag is int k ? k : -1;
            bool hasHeight = true;
            string fileName = kind switch
            {
                0 => "CheckupWindow.xaml",
                1 => "CatalogBuilderWindow.xaml",
                2 => "CatalogPickerWindow.xaml",
                7 => "PresetPickerDialog.xaml",
                6 => null,   // InputDialog: SizeToContent=Height → width only
                _ => "<info>"
            };
            if (fileName == "<info>")
            {
                Status("Info dialog size is set by its caller in code (not a root XAML attribute) — adjust there.");
                return;
            }
            if (kind == 6) { fileName = "InputDialog.xaml"; hasHeight = false; }

            try
            {
                var root = FindRepoRoot();
                string file = root == null ? null
                    : Path.Combine(root, "CheckupAddin2026", "CheckupAddin2026", "Views", fileName);
                if (file == null || !File.Exists(file)) { Status("Window XAML not found: " + (file ?? "<null>")); return; }

                string text = File.ReadAllText(file);
                int wVal = (int)Math.Round(_active.ActualWidth);
                int hVal = (int)Math.Round(_active.ActualHeight);
                int n = 0;
                // Lookbehind avoids matching MinWidth/MinHeight/MaxWidth; first match = the Window root.
                var up = new Regex("(?<![A-Za-z])Width=\"[0-9]+\"").Replace(text, $"Width=\"{wVal}\"", 1);
                if (up != text) { text = up; n++; }
                if (hasHeight)
                {
                    up = new Regex("(?<![A-Za-z])Height=\"[0-9]+\"").Replace(text, $"Height=\"{hVal}\"", 1);
                    if (up != text) { text = up; n++; }
                }
                File.WriteAllText(file, text);
                Status($"Exported size {wVal}×{(hasHeight ? hVal.ToString() : "auto")} to {fileName} ({n} attr). " +
                       "(2026 XAML only; window XAML is per-variant — update 2024/2025/2027 separately if needed)");
            }
            catch (Exception ex) { Status("Size export failed: " + ex.Message); }
        }

        // Mirrors the active window's current size into the W/H fields. Skips a field while the user
        // is typing in it (keyboard focus) so manual fine-tuning isn't clobbered by the SizeChanged echo.
        private void UpdateSizeFields(Window w)
        {
            if (w == null) return;
            if (!WidthBox.IsKeyboardFocused)  WidthBox.Text  = ((int)Math.Round(w.ActualWidth)).ToString();
            if (!HeightBox.IsKeyboardFocused) HeightBox.Text = ((int)Math.Round(w.ActualHeight)).ToString();
        }

        // ── Colors (live, shared across all open windows) ────────────────
        private void RefreshColors()
        {
            _colors.Clear();
            if (_themeDict == null) return;
            foreach (var key in _themeDict.Keys.OfType<string>().OrderBy(k => k))
                if (_themeDict[key] is SolidColorBrush b)
                    _colors.Add(new ColorItem(key, b.Color));
        }

        private void ColorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColorList.SelectedItem is ColorItem ci) HexBox.Text = ci.Hex;
        }

        private void ApplyColor_Click(object sender, RoutedEventArgs e)
        {
            if (ColorList.SelectedItem is not ColorItem ci) { Status("Select a color in the list."); return; }
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(HexBox.Text.Trim());
                _themeDict[ci.Key] = new SolidColorBrush(color);   // shared → every open window updates
                ci.Update(color);
                _changed.Add(ci.Key);
                Status($"Applied {ci.Key} = {ci.Hex}. ({_changed.Count} unsaved)");
            }
            catch { Status("Invalid hex. Use #RRGGBB or #AARRGGBB."); }
        }

        // ── Export edited colors back to the theme source .xaml ──────────
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_changed.Count == 0 && !_fontChanged && !_sizeChanged) { Status("No theme changes to export."); return; }
            try
            {
                string file = ThemeFilePath();
                if (file == null || !File.Exists(file)) { Status("Theme source not found: " + (file ?? "<null>")); return; }

                string text = File.ReadAllText(file);
                int n = 0;
                foreach (var key in _changed)
                {
                    var item = _colors.FirstOrDefault(c => c.Key == key);
                    if (item == null) continue;
                    var rx = new Regex("(<SolidColorBrush\\s+x:Key=\"" + Regex.Escape(key) + "\"\\s+Color=\")#[0-9A-Fa-f]+(\")");
                    string updated = rx.Replace(text, "${1}" + item.Hex + "$2", 1);
                    if (updated != text) { text = updated; n++; }
                }
                if (_fontChanged && FontFamilyBox.SelectedItem is ComboBoxItem fi)
                {
                    var rx = new Regex("(<FontFamily x:Key=\"CheckupFontFamily\">)[^<]*(</FontFamily>)");
                    string updated = rx.Replace(text, "${1}" + (string)fi.Content + "$2", 1);
                    if (updated != text) { text = updated; n++; }
                }
                if (_sizeChanged)
                {
                    var rx = new Regex("(<sys:Double x:Key=\"CheckupBaseFontSize\">)[^<]*(</sys:Double>)");
                    string updated = rx.Replace(text, "${1}" + ((int)FontSizeSlider.Value) + "$2", 1);
                    if (updated != text) { text = updated; n++; }
                }
                File.WriteAllText(file, text);
                Status($"Exported {n} theme change(s) to {Path.GetFileName(file)} (colors + font/size). " +
                       "(all 4 variants link to this file — rebuild propagates the change)");
                _changed.Clear();
                _fontChanged = _sizeChanged = false;
            }
            catch (Exception ex) { Status("Export failed: " + ex.Message); }
        }

        // ── Labels (language catalog: live + export) ─────────────────────
        private void LoadLang()
        {
            _langDict = new ResourceDictionary();
            try
            {
                string file = LangFilePath();
                if (file != null && File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Name.StartsWith("_")) continue;
                        if (p.Value.ValueKind == JsonValueKind.String)
                            _langDict[p.Name] = p.Value.GetString();
                    }
                }
            }
            catch { }
            _langDict["_LanguageMarker"] = true;   // lets StripLangDicts replace it on a language switch
        }

        private void AttachLang(Window w)
        {
            StripLangDicts(w);
            if (_langDict != null) w.Resources.MergedDictionaries.Add(_langDict);
        }

        private static void StripLangDicts(Window w)
        {
            var md = w.Resources.MergedDictionaries;
            for (int i = md.Count - 1; i >= 0; i--)
            {
                string src = md[i].Source?.OriginalString ?? "";
                if (src.Contains("Language") || md[i].Contains("_LanguageMarker")) md.RemoveAt(i);
            }
        }

        private void RefreshLabels()
        {
            _labels.Clear();
            if (_langDict == null) return;
            foreach (var key in _langDict.Keys.OfType<string>().Where(k => !k.StartsWith("_")).OrderBy(k => k))
                _labels.Add(new LabelItem(key, _langDict[key] as string ?? ""));
        }

        private void Lang_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _activeLang = (LangBox.SelectedIndex == 1) ? "EN" : "DE";
            LoadLang();
            foreach (var w in _open) AttachLang(w);
            _changedLabels.Clear();
            RefreshLabels();
            Status($"Language {_activeLang}: {_labels.Count} labels. ({_open.Count} window(s) updated)");
        }

        private void LabelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LabelList.SelectedItem is LabelItem li)
            {
                LabelKeyText.Text  = li.Key;
                LabelValueBox.Text = li.Value;
            }
        }

        private void ApplyLabel_Click(object sender, RoutedEventArgs e)
        {
            if (LabelList.SelectedItem is not LabelItem li) { Status("Select a label in the list."); return; }
            if (_langDict == null) return;
            _langDict[li.Key] = LabelValueBox.Text;   // live: DynamicResource labels update on every open window
            li.Update(LabelValueBox.Text);
            _changedLabels.Add(li.Key);
            Status($"Applied label {li.Key}. ({_changedLabels.Count} unsaved)");
        }

        private void ExportLabels_Click(object sender, RoutedEventArgs e)
        {
            if (_changedLabels.Count == 0) { Status("No label changes to export."); return; }
            try
            {
                string file = LangFilePath();
                if (file == null || !File.Exists(file)) { Status("Language source not found: " + (file ?? "<null>")); return; }

                string text = File.ReadAllText(file);
                int n = 0;
                foreach (var key in _changedLabels)
                {
                    var item = _labels.FirstOrDefault(l => l.Key == key);
                    if (item == null) continue;
                    string esc = item.Value
                        .Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
                    var rx = new Regex("(\"" + Regex.Escape(key) + "\"\\s*:\\s*\")(?:\\\\.|[^\"\\\\])*(\")");
                    string updated = rx.Replace(text, "${1}" + esc.Replace("$", "$$") + "$2", 1);
                    if (updated != text) { text = updated; n++; }
                }
                File.WriteAllText(file, text);
                Status($"Exported {n}/{_changedLabels.Count} label(s) to {Path.GetFileName(file)}. " +
                       "(all 4 variants linked to this JSON; rebuild picks it up)");
                _changedLabels.Clear();
            }
            catch (Exception ex) { Status("Label export failed: " + ex.Message); }
        }

        private string LangFilePath()
        {
            var root = FindRepoRoot();
            return root == null ? null
                : Path.Combine(root, "CheckupAddin2026", "CheckupAddin2026", "Resources",
                               "Languages", $"Addin_Language_File_{_activeLang}.json");
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "CheckupAddin2026"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private string ThemeFilePath()
        {
            var root = FindRepoRoot();
            return root == null ? null
                : Path.Combine(root, "CheckupAddin2026", "CheckupAddin2026", "Resources", $"{_activeTheme}Theme.xaml");
        }

        private void Status(string msg) => StatusText.Text = msg;

        // ── Color row model ──────────────────────────────────────────────
        private sealed class ColorItem : INotifyPropertyChanged
        {
            public string Key { get; }
            public string Hex { get; private set; }
            public Brush Brush { get; private set; }

            public ColorItem(string key, Color c) { Key = key; Update(c); }

            public void Update(Color c)
            {
                Hex = c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
                Brush = new SolidColorBrush(c);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hex)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        // ── Label row model ──────────────────────────────────────────────
        private sealed class LabelItem : INotifyPropertyChanged
        {
            public string Key { get; }
            public string Value { get; private set; }
            public LabelItem(string key, string val) { Key = key; Value = val; }
            public void Update(string val)
            {
                Value = val;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
