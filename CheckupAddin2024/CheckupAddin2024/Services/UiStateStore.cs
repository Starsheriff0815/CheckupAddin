using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Persists non-preset UI state (window size, active preset index) to the Windows Registry.
    /// All reads and writes are silent — missing values fall back to defaults.
    /// Uses HKCU only; no elevation required.
    /// </summary>
    internal static class UiStateStore
    {
        private const string RegKey       = @"Software\Checkup 2024";
        private const string ColWidthsKey = RegKey + @"\ColWidths";

        public static void SaveActivePresetIndex(int index)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("ActivePresetIndex", index, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static int LoadActivePresetIndex()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var val = key?.GetValue("ActivePresetIndex");
                    if (val is int i && i >= 0 && i < 3) return i;
                }
            }
            catch { }
            return 0;
        }

        public static void SaveWindowSize(double width, double height)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key?.SetValue("WindowWidth",  (int)Math.Round(width),  RegistryValueKind.DWord);
                    key?.SetValue("WindowHeight", (int)Math.Round(height), RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        // Returns true and sets width/height when valid saved values exist.
        // Minimum sanity bounds: 200 × 150 px.
        public static bool TryLoadWindowSize(out double width, out double height)
        {
            width = height = 0;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (key == null) return false;
                    var w = key.GetValue("WindowWidth");
                    var h = key.GetValue("WindowHeight");
                    if (w is int wi && h is int hi && wi >= 200 && hi >= 150)
                    {
                        width  = wi;
                        height = hi;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void ClearWindowSizes()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    if (key == null) return;
                    foreach (var name in new[] {
                        "WindowWidth", "WindowHeight",
                        "LogicDropdownWidth", "LogicDropdownHeight",
                        "InfoDialog_MainAddin_Width", "InfoDialog_MainAddin_Height",
                        "CatalogBuilderWidth", "CatalogBuilderHeight",
                        "CatalogPickerWidth", "CatalogPickerHeight" })
                    {
                        try { key.DeleteValue(name, false); } catch { }
                    }
                }
                Registry.CurrentUser.DeleteSubKeyTree(ColWidthsKey, false);
            }
            catch { }
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unknown";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        // ── Logic Dropdown popup size (per context key) ──

        public static void SaveLogicDropdownSize(string contextKey, double width, double height)
        {
            if (string.IsNullOrEmpty(contextKey)) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key?.SetValue("LogicDropdown_" + contextKey + "_W", (int)Math.Round(width),  RegistryValueKind.DWord);
                    key?.SetValue("LogicDropdown_" + contextKey + "_H", (int)Math.Round(height), RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static bool TryLoadLogicDropdownSize(string contextKey, out double width, out double height)
        {
            width = height = 0;
            if (string.IsNullOrEmpty(contextKey)) return false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (key == null) return false;
                    var w = key.GetValue("LogicDropdown_" + contextKey + "_W");
                    var h = key.GetValue("LogicDropdown_" + contextKey + "_H");
                    if (w is int wi) width  = wi;
                    if (h is int hi) height = hi;
                    return width > 0 || height > 0;
                }
            }
            catch { }
            return false;
        }

        public static void SaveLogicDropdownColumnWidths(string contextKey, double[] widths)
        {
            if (string.IsNullOrEmpty(contextKey) || widths == null || widths.Length == 0) return;
            try
            {
                var parts = new string[widths.Length];
                for (int i = 0; i < widths.Length; i++)
                    parts[i] = ((int)Math.Round(widths[i])).ToString(System.Globalization.CultureInfo.InvariantCulture);
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("LogicDropdown_" + contextKey + "_Cols", string.Join(",", parts), RegistryValueKind.String);
            }
            catch { }
        }

        public static bool TryLoadLogicDropdownColumnWidths(string contextKey, out double[] widths)
        {
            widths = null;
            if (string.IsNullOrEmpty(contextKey)) return false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (key == null) return false;
                    var val = key.GetValue("LogicDropdown_" + contextKey + "_Cols") as string;
                    if (val == null) return false;
                    var parts = val.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                    widths = new double[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        int w;
                        if (int.TryParse(parts[i], System.Globalization.NumberStyles.Integer,
                                         System.Globalization.CultureInfo.InvariantCulture, out w))
                            widths[i] = w;
                        else
                            widths[i] = 0;
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        // ── Catalog Builder window size ──

        public static void SaveCatalogBuilderSize(double width, double height)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key?.SetValue("CatalogBuilderWidth",  (int)Math.Round(width),  RegistryValueKind.DWord);
                    key?.SetValue("CatalogBuilderHeight", (int)Math.Round(height), RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static bool TryLoadCatalogBuilderSize(out double width, out double height)
        {
            width = height = 0;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (key == null) return false;
                    var w = key.GetValue("CatalogBuilderWidth");
                    var h = key.GetValue("CatalogBuilderHeight");
                    if (w is int wi && h is int hi && wi >= 600 && hi >= 400)
                    {
                        width  = wi;
                        height = hi;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ── Catalog picker window size + last tab ──

        public static void SaveCatalogPickerSize(double width, double height)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key?.SetValue("CatalogPickerWidth",  (int)Math.Round(width),  RegistryValueKind.DWord);
                    key?.SetValue("CatalogPickerHeight", (int)Math.Round(height), RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static bool TryLoadCatalogPickerSize(out double width, out double height)
        {
            width = height = 0;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (key == null) return false;
                    var w = key.GetValue("CatalogPickerWidth");
                    var h = key.GetValue("CatalogPickerHeight");
                    if (w is int wi && h is int hi && wi >= 280 && hi >= 200)
                    { width = wi; height = hi; return true; }
                }
            }
            catch { }
            return false;
        }

        public static void SaveCatalogPickerLastTab(string catalogId, string tabId)
        {
            if (string.IsNullOrEmpty(catalogId)) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("CatalogPickerTab_" + Sanitize(catalogId), tabId ?? "", RegistryValueKind.String);
            }
            catch { }
        }

        public static string LoadCatalogPickerLastTab(string catalogId)
        {
            if (string.IsNullOrEmpty(catalogId)) return "";
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                    return key?.GetValue("CatalogPickerTab_" + Sanitize(catalogId)) as string ?? "";
            }
            catch { return ""; }
        }

        // ── Catalog Builder last-selected ids + active tab ──

        public static void SaveLastCatalogId(string id)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("LastCatalogId", id ?? "", RegistryValueKind.String);
            }
            catch { }
        }

        public static string LoadLastCatalogId()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                    return key?.GetValue("LastCatalogId") as string ?? "";
            }
            catch { return ""; }
        }

        public static void SaveLastCapabilitySetId(string id)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("LastCapabilitySetId", id ?? "", RegistryValueKind.String);
            }
            catch { }
        }

        public static string LoadLastCapabilitySetId()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                    return key?.GetValue("LastCapabilitySetId") as string ?? "";
            }
            catch { return ""; }
        }

        public static void SaveCatalogBuilderActiveTab(bool isCapabilitiesTab)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("CatBuilderActiveTab", isCapabilitiesTab ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool LoadCatalogBuilderActiveTab()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var val = key?.GetValue("CatBuilderActiveTab");
                    if (val is int i) return i != 0;
                }
            }
            catch { }
            return false; // default: Catalogs tab
        }

        public static void ClearCatalogBuilderPanelStates()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true))
                {
                    key?.DeleteValue("CatBuilderBasicLogicsOpen", throwOnMissingValue: false);
                    key?.DeleteValue("CatBuilderCardPanelOpen",   throwOnMissingValue: false);
                }
            }
            catch { }
        }

        public static void SaveCatalogBuilderBasicLogicsPanel(bool isOpen)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("CatBuilderBasicLogicsOpen", isOpen ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool LoadCatalogBuilderBasicLogicsPanel()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var val = key?.GetValue("CatBuilderBasicLogicsOpen");
                    if (val is int i) return i != 0;
                }
            }
            catch { }
            return false; // default: panel closed
        }

        public static void SaveCatalogBuilderCardPanel(bool isOpen)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("CatBuilderCardPanelOpen", isOpen ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool LoadCatalogBuilderCardPanel()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var val = key?.GetValue("CatBuilderCardPanelOpen");
                    if (val is int i) return i != 0;
                }
            }
            catch { }
            return true; // default: panel open
        }

        // ── Catalog Builder per-catalog column widths ──

        public static void SaveCatalogColumnWidths(string catalogId, Dictionary<string, double> widths)
        {
            if (string.IsNullOrEmpty(catalogId)) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(ColWidthsKey + @"\" + catalogId))
                {
                    if (key == null) return;
                    foreach (var name in key.GetValueNames()) key.DeleteValue(name, false);
                    foreach (var kvp in widths)
                        key.SetValue(kvp.Key, (int)Math.Round(kvp.Value), RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static Dictionary<string, double> LoadCatalogColumnWidths(string catalogId)
        {
            var result = new Dictionary<string, double>();
            if (string.IsNullOrEmpty(catalogId)) return result;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(ColWidthsKey + @"\" + catalogId))
                {
                    if (key == null) return result;
                    foreach (var name in key.GetValueNames())
                        if (key.GetValue(name) is int px && px >= 20)
                            result[name] = px;
                }
            }
            catch { }
            return result;
        }

        // ── P3-A Field Selector: pinned fields (Favoriten zone) ──

        public static void SaveFieldSelPinnedFields(string semicolonSeparated)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("FieldSelPinnedFields", semicolonSeparated ?? "", RegistryValueKind.String);
            }
            catch { }
        }

        public static string LoadFieldSelPinnedFields()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                    return key?.GetValue("FieldSelPinnedFields") as string ?? "";
            }
            catch { return ""; }
        }

        public static void ClearFieldSelUserPrefs()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    if (key == null) return;
                    key.DeleteValue("FieldSelPinnedFields", false);
                    var names = key.GetValueNames();
                    foreach (var n in names)
                        if (n.StartsWith("FieldSelGroupCollapsed_"))
                            key.DeleteValue(n, false);
                }
            }
            catch { }
        }

        // ── F1 Field Selector: per-group collapsed state ──

        public static void SaveFieldSelGroupCollapsed(string groupName, bool isCollapsed)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("FieldSelGroupCollapsed_" + Sanitize(groupName), isCollapsed ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool LoadFieldSelGroupCollapsed(string groupName, bool defaultCollapsed = false)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var val = key?.GetValue("FieldSelGroupCollapsed_" + Sanitize(groupName));
                    if (val is int i) return i != 0;
                }
            }
            catch { }
            return defaultCollapsed;
        }

        // ── F1 Collapsibility: per-group IsCollapsed state (CatalogBuilder groups) ──

        public static void SaveGroupCollapsed(string groupId, bool isCollapsed)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("GroupCollapsed_" + Sanitize(groupId), isCollapsed ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool LoadGroupCollapsed(string groupId, bool defaultCollapsed = false)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var val = key?.GetValue("GroupCollapsed_" + Sanitize(groupId));
                    if (val is int i) return i != 0;
                }
            }
            catch { }
            return defaultCollapsed;
        }

        // ── F1 Collapsibility: per-card IsCollapsed state (CatalogBuilder cards) ──

        public static void SaveCardCollapsed(string cardId, bool isCollapsed)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("CardCollapsed_" + Sanitize(cardId), isCollapsed ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool LoadCardCollapsed(string cardId, bool defaultCollapsed = false)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var val = key?.GetValue("CardCollapsed_" + Sanitize(cardId));
                    if (val is int i) return i != 0;
                }
            }
            catch { }
            return defaultCollapsed;
        }

        // ── Field Selector popup size ──

        public static void SaveFieldSelectorDropdownSize(double width, double height)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                    key?.SetValue("FieldSelectorDropdownHeight", (int)Math.Round(height), RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool TryLoadFieldSelectorDropdownSize(out double width, out double height)
        {
            width = 0;
            height = 0;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var h = key?.GetValue("FieldSelectorDropdownHeight");
                    if (h is int hi && hi >= 80) { height = hi; return true; }
                }
            }
            catch { }
            return false;
        }

        // ── Info dialog size (per context key, e.g. "MainAddin") ──

        public static void SaveInfoDialogSize(string contextKey, double width, double height)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key?.SetValue("InfoDialog_" + contextKey + "_Width",  (int)Math.Round(width),  RegistryValueKind.DWord);
                    key?.SetValue("InfoDialog_" + contextKey + "_Height", (int)Math.Round(height), RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static bool TryLoadInfoDialogSize(string contextKey, out double width, out double height)
        {
            width = height = 0;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (key == null) return false;
                    var w = key.GetValue("InfoDialog_" + contextKey + "_Width");
                    var h = key.GetValue("InfoDialog_" + contextKey + "_Height");
                    if (w is int wi && h is int hi && wi >= 320 && hi >= 200)
                    { width = wi; height = hi; return true; }
                }
            }
            catch { }
            return false;
        }
    }
}
