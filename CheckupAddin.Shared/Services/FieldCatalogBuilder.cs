using System.Diagnostics;
using Inventor;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Builds and caches the list of all selectable fields for the row dropdown.
    /// The catalog is rebuilt when the active document path changes; call InvalidateCache()
    /// after writing a value to ensure newly visible parameters appear on next refresh.
    /// </summary>
    /// <remarks>
    /// Field key format — must match FieldWriter.WriteFieldValue and FieldItem.Key conventions:
    ///   DOC:&lt;tag&gt;                                        — PartDocument properties (Material, Appearance, units…)
    ///   IPROP|&lt;setName&gt;|&lt;propName&gt;                        — standard PropertySet (set looked up by exact COM name)
    ///   UDEF:&lt;propName&gt;                                  — user-defined iProperties (any language variant of the set name)
    ///   PARAM:User:&lt;name&gt;                                — UserParameter
    ///   PARAM:Model:&lt;name&gt;                               — ModelParameter
    /// </remarks>
    public class FieldCatalogBuilder
    {
        private readonly Inventor.Application _app;
        private readonly CapabilityStore      _capStore;

        private readonly PropertyReader _propReader = new();

        // Cache: keyed by document full path — avoids rebuilding on every refresh call for the same document.
        private string _cachedDocPath = "";
        private List<FieldItem> _cachedCatalog;

        // Asset-library cache: material/appearance names are app-wide and don't change on value
        // writes, so we cache them for the panel session and skip the COM walk on rebuilds.
        // Staleness bound: one panel session (FieldCatalogBuilder is recreated on each window open).
        private IReadOnlyList<string> _cachedMatLibNames;
        private IReadOnlyList<string> _cachedAppLibNames;
        private bool _matLibCached;
        private bool _appLibCached;

        // PropertySet + Parameter structure cache: iProperty/parameter names don't change when a
        // value is written, yet InvalidateCache() would otherwise force a full COM walk on every
        // post-write rebuild. Cached per document path; cleared on doc-switch.
        private string _cachedPropStructPath = "";
        private List<FieldItem> _cachedPropStructItems;

        public FieldCatalogBuilder(Inventor.Application app = null, CapabilityStore capStore = null)
        {
            _app      = app;
            _capStore = capStore;
        }

        /// <summary>Returned by ResolveFieldValue when a SPECIAL:LOGIC: chain forms a closed loop. DoRefreshCore renders this as a user-visible warning.</summary>
        public const string CycleSentinel          = "#CYCLE";

        // Group constants are language keys — GroupNameConverter translates them for display.
        internal const string GRP_NONE        = "";
        private  const string GRP_SHEET_METAL = "Grp_SheetMetal";
        internal const string GRP_SPECIAL     = "Grp_Special";
        private const string GRP_DOCUMENT    = "Grp_Document";
        private const string GRP_IPROP       = "Grp_iProperties";
        private const string GRP_IPROP_CUST  = "Grp_iPropertiesCustom";
        private const string GRP_PARAM_USER  = "Grp_ParamUser";
        private const string GRP_PARAM_MODEL = "Grp_ParamModel";

        // Inventor localises standard property-set DisplayNames. This table maps each known
        // non-English display name to its canonical English form so IPROP keys are stable
        // across language installs. Every entry appears twice (DE→EN and EN→DE) so that
        // GetSetNameCandidates can try both directions when resolving a stored key.
        private static readonly Dictionary<string, string> _setNameNorm =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Design Tracking - Eigenschaften"] = "Design Tracking Properties",
                ["Design Tracking Properties"]      = "Design Tracking - Eigenschaften",
                ["Inventor-Zusammenfassung"]         = "Inventor Summary Information",
                ["Inventor Summary Information"]    = "Inventor-Zusammenfassung",
                ["Dokument-Zusammenfassung"]         = "Document Summary Information",
                ["Document Summary Information"]    = "Dokument-Zusammenfassung",
            };

        /// <summary>Returns the canonical (English) set name for storage in IPROP keys.
        /// Maps known German display names to their English equivalents; unknown names pass through.</summary>
        private static string NormalizeSetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            // Map German → English (e.g. "Design Tracking - Eigenschaften" → "Design Tracking Properties")
            if (_setNameNorm.TryGetValue(name, out string alt) &&
                string.Compare(name, alt, StringComparison.OrdinalIgnoreCase) > 0)
                return alt; // alt is alphabetically earlier → assume it is the English canonical form
            // Heuristic: if the name contains " - " it is likely a localised variant; try the alias
            if (name.Contains(" - ") && _setNameNorm.TryGetValue(name, out string mapped))
                return mapped;
            return name;
        }

        /// <summary>Returns the stored set name plus any known language-variant alias,
        /// so ReadStandardProperty / PropertySets[] can be tried in both languages.</summary>
        internal static string[] GetSetNameCandidates(string storedName)
        {
            if (string.IsNullOrEmpty(storedName)) return new[] { storedName ?? "" };
            return _setNameNorm.TryGetValue(storedName, out string alt)
                ? new[] { storedName, alt }
                : new[] { storedName };
        }

        /// <summary>
        /// Returns the cached catalog if the document path is unchanged, otherwise rebuilds.
        /// </summary>
        public List<FieldItem> GetCatalog(Document doc)
        {
            string docPath = "";
            try { if (doc != null) docPath = doc.FullFileName; } catch { }

            if (docPath != "" && docPath == _cachedDocPath && _cachedCatalog?.Count > 0)
                return _cachedCatalog;

            _cachedCatalog = BuildCatalog(doc);
            _cachedDocPath = docPath;
            return _cachedCatalog;
        }

        /// <summary>Forces a full rebuild on the next GetCatalog call (call after writing a value).</summary>
        public void InvalidateCache()
        {
            _cachedCatalog = null;
            _cachedDocPath = "";
        }

        private List<FieldItem> BuildCatalog(Document doc)
        {
            var _swBuild = Stopwatch.StartNew();
            string DocName() { try { return doc?.DisplayName ?? ""; } catch { return ""; } }
            string docPath = "";
            try { if (doc != null) docPath = doc.FullFileName; } catch { }

            var items = new List<FieldItem>();

            items.Add(new FieldItem("", LanguageLoader.Get("Field_None"), "", GRP_NONE, false));

            // ── Document values — build AllowedValues lists for asset-backed fields ──
            // Enumerated once per catalog build (triggered by document change, not every refresh).
            var materialNames   = BuildAssetNameList(doc, forMaterial: true);
            var appearanceNames = BuildAssetNameList(doc, forMaterial: false);

            long _assetMs = _swBuild.ElapsedMilliseconds;

            (string tag, string labelKey, bool writable)[] docFields =
            {
                ("Material",           "Field_Material",            true),
                ("Appearance",         "Field_Appearance",          true),
                ("UnitsLength",        "Field_UnitsLength",         false),
                ("UnitsAngle",         "Field_UnitsAngle",          false),
                ("UnitsTime",          "Field_UnitsTime",           false),
                ("UnitsMass",          "Field_UnitsMass",           false),
                ("LinearPrecision",    "Field_LinearPrecision",     false),
                ("AngularPrecision",   "Field_AngularPrecision",    false),
                ("ModelingDimDisplay", "Field_ModelingDimDisplay",  false),
                ("DefaultBOMStructure","Field_DefaultBOMStructure", false),
            };

            foreach (var (tag, labelKey, writable) in docFields)
            {
                string lbl = LanguageLoader.Get(labelKey);
                IReadOnlyList<string> allowed =
                    string.Equals(tag, "Material", StringComparison.Ordinal)   ? materialNames   :
                    string.Equals(tag, "Appearance", StringComparison.Ordinal) ? appearanceNames :
                    null;
                items.Add(new FieldItem($"DOC:{tag}", lbl, lbl, GRP_DOCUMENT, writable,
                    allowedValues: allowed));
            }

            if (doc == null)
            {
                PerfLogger.LogCatalogBuild(_assetMs, 0, _swBuild.ElapsedMilliseconds - _assetMs, false, DocName());
                return items;
            }

            // PropertySet + Parameter structure cache: names don't change on value writes;
            // cached per doc path so post-write rebuilds skip the COM walk entirely.
            bool _structHit = false;
            long _structMs  = 0;
            List<FieldItem> structItems;

            if (docPath != "" && docPath == _cachedPropStructPath && _cachedPropStructItems != null)
            {
                structItems = _cachedPropStructItems;
                _structHit  = true;
            }
            else
            {
                var _swStruct = Stopwatch.StartNew();
                structItems = BuildStructureItems(doc);
                _structMs   = _swStruct.ElapsedMilliseconds;
                if (docPath != "")
                {
                    _cachedPropStructPath  = docPath;
                    _cachedPropStructItems = structItems;
                }
            }
            items.AddRange(structItems);

            // ── Logic Sets: each Card Group with a TargetFieldKey gets a SPECIAL:LOGIC:{group.Id} entry ──
            // These appear in the Special group.
            // S: prefix is rendered in red by XAML via IsSpecialEntry; DropText holds only the label text.
            if (_capStore != null)
            {
                foreach (var cs in _capStore.CapabilitySets)
                {
                    foreach (var group in cs.Groups)
                    {
                        if (string.IsNullOrEmpty(group.TargetFieldKey)) continue;
                        // Only expose in dropdown when at least one Card/BasicLogic is enabled
                        if (!group.Cards.Any(c => c.Enabled)) continue;
                        string key = $"SPECIAL:LOGIC:{group.Id}";
                        if (items.Any(x => x.Key == key)) continue;
                        var targetItem = items.FirstOrDefault(x => x.Key == group.TargetFieldKey);
                        string targetLabel = targetItem != null ? targetItem.DropText : cs.Name;
                        // Append group name when a CapabilitySet has multiple groups
                        if (cs.Groups.Count > 1 && !string.IsNullOrEmpty(group.Name))
                            targetLabel = targetLabel + " · " + group.Name;
                        string dropText = group.IsExpert ? "⚡ " + targetLabel : targetLabel;
                        items.Add(new FieldItem(key, dropText, targetLabel, GRP_SPECIAL, isWritable: true));
                    }
                }
            }

            var _result = items.OrderBy(x => GroupOrder(x.GroupName))
                               .ThenBy(x => x.DropText, NaturalComparer)
                               .ToList();
            PerfLogger.LogCatalogBuild(_assetMs, _structMs,
                _swBuild.ElapsedMilliseconds - _assetMs - _structMs, _structHit, DocName());
            return _result;
        }

        /// <summary>
        /// Walks the document's PropertySets and Parameters via COM and returns the resulting
        /// FieldItems (IPROP / UDEF / PARAM). Extracted from BuildCatalog so the result can be
        /// cached per document path (cleared on doc-switch; survives InvalidateCache()).
        /// Uses a HashSet for O(1) dedup instead of the prior O(n) items.Any() scan.
        /// </summary>
        private List<FieldItem> BuildStructureItems(Document doc)
        {
            var structItems = new List<FieldItem>();
            var seenKeys    = new HashSet<string>(StringComparer.Ordinal);

            // ── iProperties — enumerate all PropertySets ──
            // NormalizeSetName and IsUserDefinedSet are hoisted per PropertySet (not per Property).
            try
            {
                foreach (PropertySet ps in doc.PropertySets)
                {
                    string setName   = ps.DisplayName ?? ps.Name ?? "Unknown";
                    bool   isUserDef = IsUserDefinedSet(setName);
                    string keySetName = isUserDef ? null : NormalizeSetName(setName);

                    foreach (Property prop in ps)
                    {
                        try
                        {
                            string propName = prop.Name;
                            if (string.IsNullOrWhiteSpace(propName)) continue;

                            if (isUserDef)
                            {
                                string key = $"UDEF:{propName}";
                                if (seenKeys.Add(key))
                                    structItems.Add(new FieldItem(key, propName, propName,
                                        GRP_IPROP_CUST, isWritable: true));
                            }
                            else
                            {
                                string key = $"IPROP|{keySetName}|{propName}";
                                if (seenKeys.Add(key))
                                    structItems.Add(new FieldItem(key, propName, propName,
                                        GRP_IPROP, isWritable: true));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // ── Parameters ──
            if (doc.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                AddParameters(structItems, seenKeys, ((PartDocument)doc).ComponentDefinition.Parameters);
            else if (doc.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
                AddParameters(structItems, seenKeys, ((AssemblyDocument)doc).ComponentDefinition.Parameters);

            return structItems;
        }

        private static readonly IComparer<string> NaturalComparer =
            Comparer<string>.Create((a, b) =>
            {
                if (a == null && b == null) return 0;
                if (a == null) return -1;
                if (b == null) return 1;
                int ia = 0, ib = 0;
                while (ia < a.Length && ib < b.Length)
                {
                    bool aDigit = char.IsDigit(a[ia]);
                    bool bDigit = char.IsDigit(b[ib]);
                    if (aDigit && bDigit)
                    {
                        int sa = ia, sb = ib;
                        while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                        while (ib < b.Length && char.IsDigit(b[ib])) ib++;
                        int na = int.Parse(a[sa..ia]);
                        int nb = int.Parse(b[sb..ib]);
                        int c = na.CompareTo(nb);
                        if (c != 0) return c;
                    }
                    else if (!aDigit && !bDigit)
                    {
                        int sa = ia, sb = ib;
                        while (ia < a.Length && !char.IsDigit(a[ia])) ia++;
                        while (ib < b.Length && !char.IsDigit(b[ib])) ib++;
                        int c = string.Compare(a[sa..ia], b[sb..ib],
                                               StringComparison.CurrentCultureIgnoreCase);
                        if (c != 0) return c;
                    }
                    else return aDigit ? 1 : -1;
                }
                return a.Length.CompareTo(b.Length);
            });

        private static int GroupOrder(string g) => g switch
        {
            ""                      => 0,   // (none) — action items
            "Grp_Special"           => 1,   // Logic groups + computed values → always at top
            "Grp_iPropertiesCustom" => 2,
            "Grp_ParamUser"         => 3,
            "Grp_SheetMetal"        => 4,
            "Grp_iProperties"       => 5,
            "Grp_Document"          => 6,
            "Grp_ParamModel"        => 7,
            _                       => 8,
        };

        private static void AddParameters(List<FieldItem> items, HashSet<string> seenKeys, Parameters parameters)
        {
            // User parameters first (typically the ones named by the designer).
            try
            {
                foreach (UserParameter up in parameters.UserParameters)
                {
                    try
                    {
                        string key = $"PARAM:User:{up.Name}";
                        if (seenKeys.Add(key))
                        {
                            var allowed = GetParamAllowedValues(up);
                            items.Add(new FieldItem(key, up.Name, up.Name,
                                GRP_PARAM_USER, isWritable: true, allowedValues: allowed));
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (ModelParameter mp in parameters.ModelParameters)
                {
                    try
                    {
                        string key = $"PARAM:Model:{mp.Name}";
                        if (seenKeys.Add(key))
                            items.Add(new FieldItem(key, mp.Name, mp.Name,
                                GRP_PARAM_MODEL, isWritable: true));
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads the key-value list from a user parameter's ExpressionList via late binding.
        /// Returns null if the parameter has no list or the API call fails.
        /// </summary>
        private static IReadOnlyList<string> GetParamAllowedValues(UserParameter up)
        {
            try
            {
                dynamic exprList = ((dynamic)up).ExpressionList;
                if (exprList == null) return null;

                var raw = exprList.GetExpressionList() as System.Array;
                if (raw == null || raw.Length == 0) return null;

                var result = new List<string>(raw.Length);
                foreach (var item in raw)
                {
                    string s = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                }
                return result.Count > 0 ? result : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Enumerates asset names (materials or appearances) visible for <paramref name="doc"/>:
        /// the union of the document's own assets and the active library's assets, deduplicated
        /// (OrdinalIgnoreCase, document entries winning) and sorted. Returns null if nothing found.
        /// The active-library walk is cached per panel session; the document-local part is always
        /// re-read (cheap). Returns null if nothing found.
        /// </summary>
        private IReadOnlyList<string> BuildAssetNameList(Document doc, bool forMaterial)
        {
            var docLocal = BuildDocLocalAssetNames(doc, forMaterial);
            var global   = GetGlobalLibraryAssetNames(forMaterial);
            var merged   = MergeAssetNames(docLocal, global);
            return merged.Count == 0 ? null : merged;
        }

        /// <summary>Property names tried in order — Inventor 2021+ uses "MaterialAssets"/"AppearanceAssets".</summary>
        private static string[] AssetProps(bool forMaterial) => forMaterial
            ? new[] { "MaterialAssets",  "Materials"   }
            : new[] { "AppearanceAssets", "Appearances" };

        /// <summary>Asset names already present in the document itself — cheap, always re-read.</summary>
        private IReadOnlyList<string> BuildDocLocalAssetNames(Document doc, bool forMaterial)
        {
            if (doc == null) return null;
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (string prop in AssetProps(forMaterial))
                if (TryEnumerateAssets(doc, prop, seen, names)) break;
            return names;
        }

        /// <summary>
        /// Asset names from the active material/appearance library — read once per panel session
        /// and cached; reused on every subsequent build. Survives InvalidateCache() by design
        /// (libraries don't change on value writes).
        /// </summary>
        private IReadOnlyList<string> GetGlobalLibraryAssetNames(bool forMaterial)
        {
            if (forMaterial  && _matLibCached) return _cachedMatLibNames;
            if (!forMaterial && _appLibCached) return _cachedAppLibNames;

            IReadOnlyList<string> names = BuildGlobalLibraryAssetNames(forMaterial);

            if (forMaterial) { _cachedMatLibNames = names; _matLibCached = true; }
            else             { _cachedAppLibNames = names; _appLibCached = true; }
            return names;
        }

        /// <summary>Walks the active library — mirrors Inventor's material/appearance picker.</summary>
        private IReadOnlyList<string> BuildGlobalLibraryAssetNames(bool forMaterial)
        {
            if (_app == null) return null;
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            try
            {
                AssetLibrary activeLib = forMaterial
                    ? _app.ActiveMaterialLibrary
                    : _app.ActiveAppearanceLibrary;
                if (activeLib != null)
                    foreach (string prop in AssetProps(forMaterial))
                        if (TryEnumerateAssets(activeLib, prop, seen, names)) break;
            }
            catch { }
            return names;
        }

        /// <summary>
        /// Merges document-local and global-library asset names: document entries first (they win
        /// case-insensitive collisions), then any new library entries, then sorted OrdinalIgnoreCase.
        /// Pure and unit-tested — reproduces the pre-split single-pass result exactly.
        /// </summary>
        internal static List<string> MergeAssetNames(IReadOnlyList<string> docLocal,
                                                     IReadOnlyList<string> global)
        {
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            if (docLocal != null)
                foreach (var n in docLocal) if (n != null && seen.Add(n)) names.Add(n);
            if (global != null)
                foreach (var n in global) if (n != null && seen.Add(n)) names.Add(n);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Calls <paramref name="property"/> on <paramref name="source"/> via late binding,
        /// iterates the result, and appends each item's DisplayName/Name to <paramref name="names"/>.
        /// Returns true if at least one name was found.
        /// </summary>
        private static bool TryEnumerateAssets(object source, string property,
            HashSet<string> seen, List<string> names)
        {
            try
            {
                var collection = Microsoft.VisualBasic.Interaction.CallByName(
                    source, property, Microsoft.VisualBasic.CallType.Get);
                if (collection == null) return false;
                if (collection is not System.Collections.IEnumerable enumerable) return false;

                bool found = false;
                foreach (var item in enumerable)
                {
                    try
                    {
                        // Inventor library assets expose user-visible name as "DisplayName";
                        // document-level assets may use "Name". Try DisplayName first.
                        string name = null;
                        foreach (string prop in new[] { "DisplayName", "Name" })
                        {
                            try
                            {
                                string v = Microsoft.VisualBasic.Interaction.CallByName(
                                    item, prop, Microsoft.VisualBasic.CallType.Get)?.ToString();
                                if (!string.IsNullOrWhiteSpace(v) && !LooksLikeGuid(v))
                                {
                                    name = v;
                                    break;
                                }
                            }
                            catch { }
                        }

                        // Strip "N:" numeric prefix returned by some COM collections.
                        if (name != null)
                        {
                            int colon = name.IndexOf(':');
                            if (colon > 0 && colon < 4 && int.TryParse(name[..colon], out _))
                                name = name[(colon + 1)..].Trim();
                        }

                        if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                            names.Add(name);
                        found = true;
                    }
                    catch { }
                }
                return found;
            }
            catch { return false; }
        }

        private static readonly System.Text.RegularExpressions.Regex _guidRegex =
            new(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$|" +
                @"^([0-9a-fA-F]{8}-){3}[0-9a-fA-F]{8}$");

        private static bool LooksLikeGuid(string s)
        {
            if (s == null || s.Length < 32) return false;
            return _guidRegex.IsMatch(s);
        }

        /// <summary>
        /// Returns true for property sets that represent the "user defined" / custom iProperties set.
        /// Checked in multiple languages because Inventor localises the set's DisplayName.
        /// </summary>
        internal static bool IsUserDefinedSet(string setName)
        {
            if (string.IsNullOrEmpty(setName)) return false;
            string lower = setName.ToLowerInvariant();
            return lower.Contains("user defined")
                || lower.Contains("benutzerdefiniert")
                || lower.Contains("custom");
        }

        /// <summary>
        /// Resolves a field key to its current display value from the document.
        /// SPECIAL:LOGIC: keys are resolved by following the group's TargetFieldKey (with a cycle guard).
        /// </summary>
        public string ResolveFieldValue(string fieldKey, Document doc)
            => ResolveFieldValueWithCycleGuard(fieldKey, doc, null);

        /// <summary>
        /// Reads all given field keys from the document in a single pass per PropertySet.
        /// Returns a dictionary fieldKey→value. SPECIAL:LOGIC: keys are followed to their terminal
        /// key first. Keys that cannot be resolved get PropertyReader.NotAvailable.
        /// Opens each PropertySet once for all rows — avoids N separate COM opens per PropertySet.
        /// </summary>
        public Dictionary<string, string> BatchReadValues(Document doc, IEnumerable<string> fieldKeys)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc == null) return result;

            var udefProps    = new List<(string origKey, string propName)>();
            var ipropBySet   = new Dictionary<string, List<(string origKey, string propName)>>(StringComparer.OrdinalIgnoreCase);
            var ipropShort   = new List<(string origKey, string propName)>();
            var paramUser    = new List<(string origKey, string paramName)>();
            var paramModel   = new List<(string origKey, string paramName)>();
            var docKeys      = new List<(string origKey, string docTag)>();

            foreach (string rawKey in fieldKeys)
            {
                if (string.IsNullOrEmpty(rawKey)) { result[rawKey] = ""; continue; }

                // Resolve SPECIAL:LOGIC: → terminal field key (cheap: CapStore dict lookup)
                string fk = rawKey;
                if (rawKey.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal))
                {
                    string terminal = ResolveTerminalFieldKey(rawKey);
                    if (terminal == null) { result[rawKey] = ""; continue; }
                    fk = terminal;
                    // Store a forwarding sentinel — filled after terminal key is resolved
                    if (!result.ContainsKey(rawKey)) result[rawKey] = null;
                }

                if (fk.StartsWith("UDEF:", StringComparison.Ordinal))
                    udefProps.Add((rawKey, fk["UDEF:".Length..]));
                else if (fk.StartsWith("IPROP|", StringComparison.Ordinal))
                {
                    var parts = fk.Split('|');
                    if (parts.Length >= 3)
                    {
                        if (!ipropBySet.TryGetValue(parts[1], out var list))
                            ipropBySet[parts[1]] = list = new List<(string, string)>();
                        list.Add((rawKey, parts[2]));
                    }
                    else if (parts.Length == 2)
                        ipropShort.Add((rawKey, parts[1]));
                }
                else if (fk.StartsWith("PARAM:User:", StringComparison.Ordinal))
                    paramUser.Add((rawKey, fk["PARAM:User:".Length..]));
                else if (fk.StartsWith("PARAM:Model:", StringComparison.Ordinal))
                    paramModel.Add((rawKey, fk["PARAM:Model:".Length..]));
                else if (fk.StartsWith("DOC:", StringComparison.Ordinal))
                    docKeys.Add((rawKey, fk["DOC:".Length..]));
                else
                    result[rawKey] = PropertyReader.NotAvailable;
            }

            // UDEF — open UserDefined PropertySet once, read all needed properties
            if (udefProps.Count > 0)
            {
                PropertySet udefPs = null;
                foreach (var candidate in PropertyReader.UserDefinedSetCandidates)
                    try { udefPs = doc.PropertySets[candidate]; if (udefPs != null) break; } catch { }

                foreach (var (origKey, propName) in udefProps)
                {
                    string v = PropertyReader.NotAvailable;
                    if (udefPs != null)
                        try { var p = udefPs[propName]; v = p?.Value?.ToString() ?? ""; } catch { }
                    result[origKey] = v;
                }
            }

            // IPROP — open each named PropertySet once, read all props for that set
            foreach (var kv in ipropBySet)
            {
                string setName = kv.Key;
                var    props   = kv.Value;
                PropertySet ps = null;
                foreach (var candidate in GetSetNameCandidates(setName))
                    try { ps = doc.PropertySets[candidate]; if (ps != null) break; } catch { }

                foreach (var (origKey, propName) in props)
                {
                    string v = PropertyReader.NotAvailable;
                    if (ps != null)
                        try { var p = ps[propName]; if (p != null) v = p.Value?.ToString() ?? PropertyReader.NotAvailable; } catch { }
                    // Language-fallback: key was stored in a different locale than current install
                    if (v == PropertyReader.NotAvailable)
                        v = _propReader.ReadStandardPropertyByName(doc, propName);
                    result[origKey] = v;
                }
            }

            // IPROP short-form (2-part): must scan all standard property sets by prop name
            foreach (var (origKey, propName) in ipropShort)
                result[origKey] = _propReader.ReadStandardPropertyByName(doc, propName);

            // PARAM — get Parameters object once, read all user + model params
            if (paramUser.Count > 0 || paramModel.Count > 0)
            {
                var parameters = PropertyReader.GetParameters(doc);

                foreach (var (origKey, paramName) in paramUser)
                {
                    string v = PropertyReader.NotAvailable;
                    if (parameters != null)
                        try
                        {
                            var up  = parameters.UserParameters[paramName];
                            object val = up.Value;
                            v = val is string s
                                ? (string.IsNullOrEmpty(s) ? PropertyReader.NotAvailable : s)
                                : PropertyReader.FormatParameterValue(doc, Convert.ToDouble(val), up.get_Units());
                        }
                        catch { }
                    result[origKey] = v;
                }

                foreach (var (origKey, paramName) in paramModel)
                {
                    string v = PropertyReader.NotAvailable;
                    if (parameters != null)
                        try
                        {
                            var mp  = parameters.ModelParameters[paramName];
                            object val = mp.Value;
                            v = val is string s
                                ? (string.IsNullOrEmpty(s) ? PropertyReader.NotAvailable : s)
                                : PropertyReader.FormatParameterValue(doc, Convert.ToDouble(val), mp.get_Units());
                        }
                        catch { }
                    result[origKey] = v;
                }
            }

            // DOC — individual reads (no PropertySet, already cheap)
            foreach (var (origKey, docTag) in docKeys)
                result[origKey] = _propReader.ReadDocumentValue(doc, docTag);

            // SPECIAL:LOGIC: forward — fill sentinel entries from their resolved terminal key value
            foreach (string key in result.Keys.Where(k => k.StartsWith("SPECIAL:LOGIC:", StringComparison.Ordinal)).ToList())
            {
                if (result[key] != null) continue; // already set (shouldn't happen but guard it)
                string terminal = ResolveTerminalFieldKey(key);
                result[key] = terminal != null && result.TryGetValue(terminal, out string tv)
                    ? tv ?? ""
                    : "";
            }

            return result;
        }

        private string ResolveFieldValueWithCycleGuard(string fieldKey, Document doc, HashSet<string> visitedLogicGroups)
        {
            if (string.IsNullOrWhiteSpace(fieldKey)) return "";

            // Logic Set row: pass through to the group's configured TargetFieldKey.
            // CYCLE GUARD (V1 safeguard): a Group whose TargetFieldKey points to its own SPECIAL:LOGIC:
            // alias (or any chain A→B→…→A) would otherwise recurse infinitely → stack overflow → Inventor crash.
            if (fieldKey.StartsWith("SPECIAL:LOGIC:"))
            {
                string groupId = fieldKey["SPECIAL:LOGIC:".Length..];
                if (visitedLogicGroups == null) visitedLogicGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!visitedLogicGroups.Add(groupId))
                {
                    Services.DiagLogger.Log("resolve", $"SKIP: SPECIAL:LOGIC:{groupId} cycle detected (visited chain: {string.Join(" → ", visitedLogicGroups)})");
                    return CycleSentinel; // DoRefreshCore renders this as a user-visible warning
                }
                var found = _capStore?.FindGroup(groupId);
                if (found == null || string.IsNullOrEmpty(found.Value.Group.TargetFieldKey)) return "";
                return ResolveFieldValueWithCycleGuard(found.Value.Group.TargetFieldKey, doc, visitedLogicGroups);
            }

            if (fieldKey.StartsWith("UDEF:"))
                return _propReader.ReadUserDefinedProperty(doc, fieldKey["UDEF:".Length..]);

            if (fieldKey.StartsWith("IPROP|"))
            {
                var parts = fieldKey.Split('|');
                // 3-part: IPROP|<setName>|<propName>  (internal catalog key format)
                // Try all language candidates for the set name; fall back to searching all sets by property
                // name so stored German keys resolve in English Inventor and vice-versa.
                if (parts.Length >= 3)
                {
                    string val = _propReader.ReadStandardProperty(doc, GetSetNameCandidates(parts[1]), new[] { parts[2] });
                    return val != PropertyReader.NotAvailable ? val : _propReader.ReadStandardPropertyByName(doc, parts[2]);
                }
                // 2-part: IPROP|<propName>  (short-form used in formula references and capability files)
                if (parts.Length == 2)
                    return _propReader.ReadStandardPropertyByName(doc, parts[1]);
                return "";
            }

            if (fieldKey.StartsWith("DOC:"))
                return _propReader.ReadDocumentValue(doc, fieldKey["DOC:".Length..]);

            if (fieldKey.StartsWith("PARAM:User:") || fieldKey.StartsWith("PARAM:Model:"))
            {
                int second = fieldKey.IndexOf(':', fieldKey.IndexOf(':') + 1);
                if (second < 0) return PropertyReader.NotAvailable;
                // Value-first display (the equation is revealed via the fx toggle — see ResolveFieldFormula).
                return _propReader.ReadParameterValue(doc, fieldKey[(second + 1)..]);
            }

            return PropertyReader.NotAvailable;
        }

        /// <summary>
        /// Returns the Inventor formula/equation behind a field's value, or "" when the field
        /// holds a literal. The UI uses this to decide whether to show the fx toggle and what to
        /// edit when it is pressed. Only iProperty (UDEF/IPROP) and parameter (PARAM) fields can
        /// be formula-driven; every other field kind returns "".
        ///
        /// For a SPECIAL:LOGIC: row this follows the group's TargetFieldKey (cycle-guarded) so the
        /// fx toggle can appear next to the Window-Picker on logic rows whose target is equation-
        /// driven — EXCEPT when the group auto-owns its target (BasicLogic writing to it, or Expert
        /// auto-eval), where a user-edited equation would be clobbered on the next refresh.
        /// </summary>
        public string ResolveFieldFormula(string fieldKey, Document doc)
            => ResolveFieldFormulaWithCycleGuard(fieldKey, doc, null);

        private string ResolveFieldFormulaWithCycleGuard(string fieldKey, Document doc, HashSet<string> visitedLogicGroups)
        {
            if (string.IsNullOrWhiteSpace(fieldKey) || doc == null) return "";

            if (fieldKey.StartsWith("SPECIAL:LOGIC:"))
            {
                string groupId = fieldKey["SPECIAL:LOGIC:".Length..];
                if (visitedLogicGroups == null) visitedLogicGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!visitedLogicGroups.Add(groupId)) return ""; // cycle → no fx (value path raises the ⚠ warning)

                var found = _capStore?.FindGroup(groupId);
                if (found == null) return "";
                var group = found.Value.Group;
                if (string.IsNullOrEmpty(group.TargetFieldKey)) return "";

                // Carve-out: target is auto-computed every refresh → editing its equation is futile.
                if (group.IsExpert || CardEngine.HasBasicLogicWritingTo(group, group.TargetFieldKey))
                    return "";

                return ResolveFieldFormulaWithCycleGuard(group.TargetFieldKey, doc, visitedLogicGroups);
            }

            if (fieldKey.StartsWith("UDEF:"))
                return _propReader.ReadUserDefinedExpression(doc, fieldKey["UDEF:".Length..]);

            if (fieldKey.StartsWith("IPROP|"))
            {
                var parts = fieldKey.Split('|');
                if (parts.Length >= 3)
                {
                    string expr = _propReader.ReadStandardExpression(doc, GetSetNameCandidates(parts[1]), new[] { parts[2] });
                    return !string.IsNullOrEmpty(expr) ? expr : _propReader.ReadStandardExpressionByName(doc, parts[2]);
                }
                if (parts.Length == 2)
                    return _propReader.ReadStandardExpressionByName(doc, parts[1]);
                return "";
            }

            if (fieldKey.StartsWith("PARAM:User:") || fieldKey.StartsWith("PARAM:Model:"))
            {
                int second = fieldKey.IndexOf(':', fieldKey.IndexOf(':') + 1);
                if (second < 0) return "";
                // ReadParameterExpression returns "" for a missing parameter (never the "n/a"
                // display sentinel), so a missing PARAM row stays non-formula and keeps its
                // greyed/strikethrough "missing" label (TDD §172). See sentinel discipline in
                // PropertyReader / TDD §5.16.
                string expr = _propReader.ReadParameterExpression(doc, fieldKey[(second + 1)..]);
                return PropertyReader.IsParameterFormula(expr) ? expr : "";
            }

            return "";
        }

        /// <summary>
        /// Walks a SPECIAL:LOGIC: key to the terminal real field key it ultimately targets
        /// (UDEF/IPROP/PARAM/DOC), guarding against cycles. Returns the key unchanged when it is
        /// already a real field; returns null when the chain is broken or cyclic. Used by the fx
        /// formula write so the equation lands on the actual property/parameter, not a logic alias.
        /// </summary>
        public string ResolveTerminalFieldKey(string fieldKey)
            => ResolveTerminalFieldKey(fieldKey, null);

        private string ResolveTerminalFieldKey(string fieldKey, HashSet<string> visitedLogicGroups)
        {
            if (string.IsNullOrWhiteSpace(fieldKey)) return null;
            if (!fieldKey.StartsWith("SPECIAL:LOGIC:")) return fieldKey;

            string groupId = fieldKey["SPECIAL:LOGIC:".Length..];
            if (visitedLogicGroups == null) visitedLogicGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!visitedLogicGroups.Add(groupId)) return null;

            var found = _capStore?.FindGroup(groupId);
            if (found == null || string.IsNullOrEmpty(found.Value.Group.TargetFieldKey)) return null;
            return ResolveTerminalFieldKey(found.Value.Group.TargetFieldKey, visitedLogicGroups);
        }
    }
}
