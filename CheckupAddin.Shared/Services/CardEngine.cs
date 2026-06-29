using System;
using System.Collections.Generic;
using System.Linq;
using CheckupAddIn.Models;

namespace CheckupAddIn.Services
{
    /// <summary>One distinct tab from the catalog's TAB column, for use in the CatalogPickerWindow tab strip.</summary>
    public sealed class CatalogTabEntry
    {
        public string TabId   { get; }
        public string Label   { get; }
        public string SortKey { get; }
        public CatalogTabEntry(string tabId, string label, string sortKey)
        { TabId = tabId ?? ""; Label = label ?? ""; SortKey = sortKey ?? ""; }
    }

    /// <summary>
    /// Stateless helpers that interpret CapabilityCard lists against a CatalogData.
    /// </summary>
    public static class CardEngine
    {
        // ── Card type identifiers ──────────────────────────────────────────────
        public const string CardTypeDropdown = "Dropdown";
        public const string CardTypeSync     = "Sync";
        public const string CardTypeLink     = "Link";
        /// <summary>Button card: adds a 📋 picker button in the Checkup row that opens a CatalogPickerWindow.</summary>
        public const string CardTypeButton   = "Button";
        /// <summary>Search card: live-filter text input; shows a filtered dropdown as the user types.</summary>
        public const string CardTypeSearch   = "Search";
        // ── Shared param keys ─────────────────────────────────────────────────
        /// <summary>Sync card: companion field key to write to.</summary>
        public const string ParamCompanionFieldKey = "CompanionFieldKey";

        /// <summary>Sync card: which catalog role value to write to the companion field (default "SEC").</summary>
        public const string ParamCompanionRole = "CompanionRole";

        /// <summary>Link card: field key of the companion row that is locked below this Logic row.</summary>
        public const string ParamLinkPartnerFieldKey = "PartnerFieldKey";

        /// <summary>Dropdown card: which catalog role appears as the secondary text column (default "SEC"; empty = hidden).</summary>
        public const string ParamSecRole = "SecRole";

        /// <summary>Dropdown card: which catalog role appears as the item tooltip (default "AUX"; empty = hidden).</summary>
        public const string ParamTooltipRole = "TooltipRole";

        /// <summary>Search card: comma-separated list of catalog role badges to match against when filtering
        /// (e.g. "PRI,SEC"). Empty = match PRI and SEC by default.</summary>
        public const string ParamSearchRoles = "SearchRoles";

        /// <summary>Multi-Pick card: adds a picker button that allows selecting multiple catalog items.
        /// The selected PRI values are joined with <see cref="ParamPrimaryTokenSeparator"/> and written
        /// to the group's TargetFieldKey. Optionally writes companion values to <see cref="ParamCompanionFieldKey"/>.</summary>
        public const string CardTypeMultiPick = "MultiPick";

        /// <summary>Multi-Pick card: character(s) used to join selected PRI values into the target field (default "-").</summary>
        public const string ParamPrimaryTokenSeparator = "PrimaryTokenSeparator";

        /// <summary>Multi-Pick card: character(s) used to join companion role values into the companion field (default ", ").</summary>
        public const string ParamCompanionTokenSeparator = "CompanionTokenSeparator";

        /// <summary>Pair Transform card: splits the current field value into tokens, looks up each token by
        /// <see cref="ParamLookupRole"/>, outputs the <see cref="ParamOutputRole"/> value, and writes the
        /// joined result to <see cref="ParamCompanionFieldKey"/>. Fires on inline-edit Apply (not picker).</summary>
        public const string CardTypePairTransform = "PairTransform";

        /// <summary>Pair Transform card: separator used to split the source value into tokens (default "-").</summary>
        public const string ParamSourceTokenSeparator = "SourceTokenSeparator";

        /// <summary>Pair Transform card: catalog role badge each source token is looked up as (default "PRI").</summary>
        public const string ParamLookupRole = "LookupRole";

        /// <summary>Pair Transform card: catalog role badge whose value is output for each matched token (default "SEC").</summary>
        public const string ParamOutputRole = "OutputRole";

        /// <summary>Pair Transform card: separator used to join the output tokens (default ", ").</summary>
        public const string ParamOutputTokenSeparator = "OutputTokenSeparator";

        /// <summary>Prefix/Suffix card: prepends and/or appends fixed text to the raw field value before display.
        /// In Remove mode the prefix/suffix is stripped instead of added.</summary>
        public const string CardTypePrefixSuffix = "PrefixSuffix";

        /// <summary>PrefixSuffix card: text to prepend to (or strip from the start of) the field value.</summary>
        public const string ParamPrefix = "Prefix";

        /// <summary>PrefixSuffix card: text to append to (or strip from the end of) the field value.</summary>
        public const string ParamSuffix = "Suffix";

        /// <summary>PrefixSuffix card: when "true" the card strips prefix/suffix from stored values instead of adding them.</summary>
        public const string ParamIsRemoveMode = "IsRemoveMode";

        /// <summary>Sort card: sorts a multi-token field value by the catalog's SRT column(s) on Apply.</summary>
        public const string CardTypeSort = "Sort";

        /// <summary>Sort card: separator used to split and rejoin the field value into tokens (default "-").</summary>
        public const string ParamSortTokenSeparator = "SortTokenSeparator";

        /// <summary>Sort card: catalog role badge each token is looked up as (default "PRI").</summary>
        public const string ParamSortLookupRole = "SortLookupRole";

        // ── Basic Logic (BL) card ─────────────────────────────────────────────
        /// <summary>Basic Logic card: stores one formula expression evaluated on Apply.
        /// Output is written to <see cref="ParamFormulaTargetFieldKey"/> (or the group's TargetFieldKey if empty).
        /// BLs are added via the 5 template buttons in the right panel (CONCATENATE / IF / LOOKUP / FORMAT / ROUND).</summary>
        public const string CardTypeBasicLogic = "BasicLogic";

        /// <summary>BL card: the formula text. See <see cref="FormulaEngine"/> for syntax.</summary>
        public const string ParamFormula = "Formula";

        /// <summary>BL card: optional override of the field that receives the formula result. Empty = group's TargetFieldKey.</summary>
        public const string ParamFormulaTargetFieldKey = "FormulaTargetFieldKey";

        /// <summary>Sort card: when "true" reverses the sort order (descending instead of ascending).</summary>
        public const string ParamSortInvert = "SortInvert";

        // ── Display column params (Dropdown / Button cards) ───────────────────
        /// <summary>Maximum number of additional display-only columns configurable per Dropdown or Button card.</summary>
        public const int MaxDisplayColumns = 7;

        /// <summary>Returns the Card.Params key for display column slot <paramref name="n"/> (0-based).</summary>
        public static string DisplayRoleKey(int n) => $"Display_{n}_Role";

        // ── Role badge ↔ enum mapping ──────────────────────────────────────────

        /// <summary>Returns the two-or-three-letter badge abbreviation for a catalog column role.</summary>
        public static string RoleBadge(ColumnRole role) => role switch
        {
            ColumnRole.PrimaryDisplay   => "PRI",
            ColumnRole.SecondaryDisplay => "SEC",
            ColumnRole.TabId            => "TAB",
            ColumnRole.GroupId          => "GRP",
            ColumnRole.SortKey          => "SRT",
            ColumnRole.GroupSortKey     => "GST",
            ColumnRole.TabSortKey       => "TST",
            ColumnRole.Auxiliary        => "AUX",
            ColumnRole.Generation       => "GEN",
            _                           => "",
        };

        private static ColumnRole RoleFromBadge(string badge) => badge?.ToUpperInvariant() switch
        {
            "PRI" => ColumnRole.PrimaryDisplay,
            "SEC" => ColumnRole.SecondaryDisplay,
            "TAB" => ColumnRole.TabId,
            "GRP" => ColumnRole.GroupId,
            "SRT" => ColumnRole.SortKey,
            "GST" => ColumnRole.GroupSortKey,
            "TST" => ColumnRole.TabSortKey,
            "AUX" => ColumnRole.Auxiliary,
            "GEN" => ColumnRole.Generation,
            _     => ColumnRole.None,
        };

        /// <summary>
        /// T43 — generation scope. True when <paramref name="entry"/> is visible under the active generation.
        /// A Generation-roled (GEN) cell tags a row to one generation; a BLANK cell is universal (matches any
        /// scope). When no generation is active, or the catalog has no Generation column, every entry is in
        /// scope — so the filter is inert until a catalog opts in AND a scope is supplied.
        /// </summary>
        internal static bool EntryInGenerationScope(CatalogData catalog, CatalogEntry entry, string activeGeneration)
        {
            if (string.IsNullOrWhiteSpace(activeGeneration) || catalog == null || entry == null) return true;
            string genKey = GetColumnKey(catalog, ColumnRole.Generation, 1);
            if (string.IsNullOrEmpty(genKey)) return true;                          // catalog has no Generation column
            if (!entry.Values.TryGetValue(genKey, out string g) || string.IsNullOrWhiteSpace(g))
                return true;                                                        // blank cell = universal
            return string.Equals(g.Trim(), activeGeneration.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// T43 — category scope. True when <paramref name="entry"/>'s category cell (resolved via
        /// <paramref name="categoryColumn"/> — role badge or key/label) <b>starts with</b> any of the
        /// comma-separated <paramref name="categoryValues"/> (case-insensitive). Starts-with handles both a
        /// clean category column ("Material") and a "CategoryNNN" convention ("Material033"). A blank filter or
        /// unresolved column means "no filter" (in scope); a present filter with no match excludes the entry.
        /// </summary>
        internal static bool EntryInCategory(CatalogData catalog, CatalogEntry entry, string categoryColumn, string categoryValues)
        {
            if (string.IsNullOrWhiteSpace(categoryValues) || catalog == null || entry == null) return true;
            string catKey = ResolveColumnKey(catalog, categoryColumn);
            if (string.IsNullOrEmpty(catKey)) return true;
            if (!entry.Values.TryGetValue(catKey, out string cv) || cv == null) return false;
            string cell = cv.Trim();
            foreach (var raw in categoryValues.Split(','))
            {
                string c = raw.Trim();
                if (c.Length > 0 && cell.StartsWith(c, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// T43 — derive the active generation from a typed short. Splits <paramref name="sourceShort"/> on
        /// <paramref name="sourceSeparator"/>; if ANY token matches (by PRI) a catalog row whose
        /// <paramref name="signalCategoryColumn"/> value is one of the comma-separated
        /// <paramref name="signalCategoryValuesCsv"/>, returns <paramref name="generationWhenPresent"/> (e.g.
        /// Paneel); otherwise <paramref name="generationWhenAbsent"/> (e.g. Blech). The signal is data-driven
        /// (a catalog category such as Füllung/Schaum), NOT a hard-coded token list. Detection runs BEFORE
        /// scoping, so it is itself unscoped.
        /// </summary>
        public static string DetectGeneration(
            string sourceShort, CatalogData catalog, string sourceSeparator,
            string signalCategoryColumn, string signalCategoryValuesCsv,
            string generationWhenPresent, string generationWhenAbsent)
        {
            if (catalog == null || string.IsNullOrEmpty(sourceShort)) return generationWhenAbsent;
            string catKey = ResolveColumnKey(catalog, signalCategoryColumn);   // role badge OR column key/label
            string priKey = GetColumnKey(catalog, ColumnRole.PrimaryDisplay, 1);
            if (string.IsNullOrEmpty(catKey) || string.IsNullOrEmpty(priKey)) return generationWhenAbsent;

            var signal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in (signalCategoryValuesCsv ?? "").Split(','))
            { string vv = v.Trim(); if (vv.Length > 0) signal.Add(vv); }
            if (signal.Count == 0) return generationWhenAbsent;

            var tokenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in sourceShort.Split(new[] { sourceSeparator ?? "-" }, StringSplitOptions.RemoveEmptyEntries))
            { string tt = t.Trim(); if (tt.Length > 0) tokenSet.Add(tt); }

            foreach (var entry in catalog.Entries)
            {
                if (!entry.Values.TryGetValue(priKey, out string pri) || string.IsNullOrEmpty(pri) || !tokenSet.Contains(pri))
                    continue;
                if (entry.Values.TryGetValue(catKey, out string cat) && cat != null &&
                    signal.Any(s => cat.Trim().StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                    return generationWhenPresent;
            }
            return generationWhenAbsent;
        }

        // ── Basic helpers ─────────────────────────────────────────────────────

        public static bool HasCard(CardGroup group, string cardType)
            => group?.Cards.Any(c => c.Enabled && c.Type == cardType) == true;

        /// <summary>
        /// Resolves a column name-or-role-badge to the internal column key of <paramref name="catalog"/>.
        /// Matches by column label first (case-insensitive), then by role badge.
        /// Returns null when not found.
        /// </summary>
        public static string ResolveColumnKey(CatalogData catalog, string nameOrBadge)
        {
            if (catalog == null || string.IsNullOrEmpty(nameOrBadge)) return null;
            var byLabel = catalog.Columns.FirstOrDefault(c =>
                string.Equals(c.Label, nameOrBadge, StringComparison.OrdinalIgnoreCase));
            if (byLabel != null) return byLabel.Key;
            ParseRoleBadge(nameOrBadge, out ColumnRole role, out int index);
            if (role != ColumnRole.None)
            {
                var byRole = catalog.Columns.FirstOrDefault(c => c.Role == role && c.RoleIndex == index)
                          ?? catalog.Columns.FirstOrDefault(c => c.Role == role);
                if (byRole != null) return byRole.Key;
            }
            return null;
        }

        /// <summary>
        /// Searches <paramref name="catalog"/> for an entry where <paramref name="searchColNameOrBadge"/>
        /// equals <paramref name="key"/> (case-insensitive) and returns the corresponding
        /// <paramref name="returnColNameOrBadge"/> value. Returns null when not found.
        /// </summary>
        public static string LookupByColumn(CatalogData catalog, string key,
            string searchColNameOrBadge, string returnColNameOrBadge)
        {
            if (catalog == null) return null;
            string searchKey = ResolveColumnKey(catalog, searchColNameOrBadge);
            string returnKey = ResolveColumnKey(catalog, returnColNameOrBadge);
            if (searchKey == null || returnKey == null) return null;
            foreach (var entry in catalog.Entries)
            {
                if (entry.Values.TryGetValue(searchKey, out string sv) &&
                    string.Equals(sv, key, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Values.TryGetValue(returnKey, out string rv);
                    return rv ?? "";
                }
            }
            return null;
        }

        /// <summary>Returns the partner field key from the first enabled Link card in <paramref name="group"/>,
        /// or null if no Link card is present.</summary>
        public static string GetLinkPartnerFieldKey(CardGroup group)
        {
            if (group == null) return null;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeLink) continue;
                if (card.Params.TryGetValue(ParamLinkPartnerFieldKey, out string key) && !string.IsNullOrEmpty(key))
                    return key;
            }
            return null;
        }

        /// <summary>
        /// Returns the partner field keys for ALL enabled Link cards in <paramref name="group"/>,
        /// in card order. Empty when no Link cards are present.
        /// </summary>
        public static IReadOnlyList<string> GetAllLinkPartnerFieldKeys(CardGroup group)
        {
            if (group == null) return Array.Empty<string>();
            var keys = new List<string>();
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeLink) continue;
                if (card.Params.TryGetValue(ParamLinkPartnerFieldKey, out string key) && !string.IsNullOrEmpty(key))
                    keys.Add(key);
            }
            return keys;
        }

        /// <summary>
        /// Returns the CatalogId from the first enabled Dropdown, Button, or Search card in
        /// <paramref name="group"/>, or null when no such card is configured.
        /// </summary>
        public static string GetPrimaryCatalogId(CardGroup group)
        {
            if (group == null) return null;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled) continue;
                if (card.Type == CardTypeDropdown    || card.Type == CardTypeButton   ||
                    card.Type == CardTypeSearch      || card.Type == CardTypeMultiPick ||
                    card.Type == CardTypePairTransform || card.Type == CardTypeSort ||
                    card.Type == CardTypeCompose)
                    return string.IsNullOrEmpty(card.CatalogId) ? null : card.CatalogId;
            }
            return null;
        }

        /// <summary>
        /// Looks up the value of <paramref name="roleBadge"/> column for the entry whose PRI value
        /// matches <paramref name="priValue"/>. Supports indexed badges such as "AUX2". Returns null when not found.
        /// </summary>
        public static string LookupRoleValue(CatalogData catalog, string priValue, string roleBadge)
        {
            if (catalog == null || string.IsNullOrEmpty(priValue) || string.IsNullOrEmpty(roleBadge))
                return null;
            string priKey  = GetColumnKey(catalog, ColumnRole.PrimaryDisplay, 1);
            ParseRoleBadge(roleBadge, out ColumnRole role, out int roleIdx);
            string roleKey = role != ColumnRole.None ? GetColumnKey(catalog, role, roleIdx) : null;
            if (priKey == null || roleKey == null) return null;

            foreach (var entry in catalog.Entries)
            {
                entry.Values.TryGetValue(priKey, out string val);
                if (string.Equals(val, priValue, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Values.TryGetValue(roleKey, out string result);
                    return result ?? "";
                }
            }
            return null;
        }

        /// <summary>Convenience overload — looks up the SEC column value for a PRI value.</summary>
        public static string LookupSecValue(CatalogData catalog, string priValue)
            => LookupRoleValue(catalog, priValue, "SEC");

        // ── Dropdown card ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds the inline dropdown item list for a Dropdown card.
        /// The secondary text column is determined by <paramref name="secRoleBadge"/> (default "SEC");
        /// the tooltip column by <paramref name="tooltipRoleBadge"/> (default "AUX").
        /// Additional visual-only display columns can be supplied via <paramref name="extraDisplayRoles"/>.
        /// Pass an empty string to suppress either core column.
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetDropdownItems(
            CatalogData catalog,
            string secRoleBadge     = "SEC",
            string tooltipRoleBadge = "AUX",
            IReadOnlyList<string> extraDisplayRoles = null,
            IReadOnlyList<string> searchRoles = null)
        {
            if (catalog == null) return Array.Empty<CatalogDropdownItem>();
            string priKey     = GetColumnKey(catalog, ColumnRole.PrimaryDisplay, 1);
            if (priKey == null) return Array.Empty<CatalogDropdownItem>();

            string secKey     = GetRoleKey(catalog, secRoleBadge);
            string tooltipKey = GetRoleKey(catalog, tooltipRoleBadge);
            string grpKey     = GetColumnKey(catalog, ColumnRole.GroupId,      1);
            string tstKey     = GetColumnKey(catalog, ColumnRole.TabSortKey,   1);
            string tabKey     = GetColumnKey(catalog, ColumnRole.TabId,        1); // fallback sort when no TST
            string gstKey     = GetColumnKey(catalog, ColumnRole.GroupSortKey, 1);
            string srtKey     = GetColumnKey(catalog, ColumnRole.SortKey,      1);

            // Resolve extra display column keys once (may contain nulls when role not in catalog)
            string[] extraKeys = null;
            if (extraDisplayRoles != null && extraDisplayRoles.Count > 0)
            {
                extraKeys = new string[extraDisplayRoles.Count];
                for (int i = 0; i < extraDisplayRoles.Count; i++)
                    extraKeys[i] = GetRoleKey(catalog, extraDisplayRoles[i]);
            }

            // Resolve search role keys (for Search card live filter)
            string[] searchKeys = null;
            if (searchRoles != null && searchRoles.Count > 0)
            {
                searchKeys = new string[searchRoles.Count];
                for (int i = 0; i < searchRoles.Count; i++)
                    searchKeys[i] = GetRoleKey(catalog, searchRoles[i]);
            }

            bool hasSort = tstKey != null || tabKey != null || gstKey != null || srtKey != null;

            var staged = new List<(CatalogDropdownItem item, string tst, string gst, string srt, int orig)>(catalog.Entries.Count);
            int orig = 0;
            foreach (var entry in catalog.Entries)
            {
                if (!entry.Values.TryGetValue(priKey, out string pri) || string.IsNullOrEmpty(pri))
                { orig++; continue; }

                string sec = null, tip = null, grp = null, tab = null, tst = null, gst = null, srt = null;
                if (secKey     != null) entry.Values.TryGetValue(secKey,     out sec);
                if (tooltipKey != null) entry.Values.TryGetValue(tooltipKey, out tip);
                if (grpKey     != null) entry.Values.TryGetValue(grpKey,     out grp);
                if (tabKey     != null) entry.Values.TryGetValue(tabKey,     out tab);
                if (tstKey     != null) entry.Values.TryGetValue(tstKey,     out tst);
                else                                                                tst = (tab ?? "").Split(',')[0].Trim(); // Task #40: first tab token (was whole TAB cell) when no TST column
                if (gstKey     != null) entry.Values.TryGetValue(gstKey,     out gst);
                if (srtKey     != null) entry.Values.TryGetValue(srtKey,     out srt);

                List<string> extras = null;
                if (extraKeys != null)
                {
                    extras = new List<string>(extraKeys.Length);
                    foreach (var ek in extraKeys)
                    {
                        string ev = null;
                        if (ek != null) entry.Values.TryGetValue(ek, out ev);
                        extras.Add(ev ?? "");
                    }
                }

                List<string> searchVals = null;
                if (searchKeys != null)
                {
                    searchVals = new List<string>(searchKeys.Length);
                    foreach (var sk in searchKeys)
                    {
                        string sv = null;
                        if (sk != null) entry.Values.TryGetValue(sk, out sv);
                        searchVals.Add(sv ?? "");
                    }
                }

                staged.Add((new CatalogDropdownItem(pri, sec, tip, grp, tab, extras, searchVals), tst ?? "", gst ?? "", srt ?? "", orig));
                orig++;
            }

            if (hasSort)
            {
                staged.Sort((a, b) =>
                {
                    int c = CompareValues(a.tst, b.tst);
                    if (c != 0) return c;
                    c = CompareValues(a.gst, b.gst);
                    if (c != 0) return c;
                    c = CompareValues(a.srt, b.srt);
                    if (c != 0) return c;
                    return a.orig.CompareTo(b.orig);
                });
            }

            return staged.Select(s => s.item).ToList();
        }

        private static int CompareValues(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
            if (string.IsNullOrEmpty(a)) return 1;
            if (string.IsNullOrEmpty(b)) return -1;
            if (double.TryParse(a, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double da) &&
                double.TryParse(b, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double db))
                return da.CompareTo(db);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads the SecRole / TooltipRole / Display_N_Role params from the first enabled Dropdown card in
        /// <paramref name="group"/> and calls <see cref="GetDropdownItems"/>.
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetDropdownItemsForCard(
            CardGroup group, CatalogData catalog)
        {
            if (group == null) return GetDropdownItems(catalog);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeDropdown) continue;
                card.Params.TryGetValue(ParamSecRole,     out string sec);
                card.Params.TryGetValue(ParamTooltipRole, out string tip);
                return GetDropdownItems(catalog,
                    string.IsNullOrEmpty(sec) ? "SEC" : sec,
                    string.IsNullOrEmpty(tip) ? "AUX" : tip,
                    ReadDisplayRoles(card));
            }
            return GetDropdownItems(catalog);
        }

        /// <summary>
        /// Returns the ordered list of distinct tabs from the catalog's TAB column,
        /// sorted by TST (tab sort key). Empty when the catalog has no TAB column.
        /// </summary>
        public static IReadOnlyList<CatalogTabEntry> GetPickerTabs(CatalogData catalog)
        {
            if (catalog == null) return Array.Empty<CatalogTabEntry>();
            string tabKey = GetColumnKey(catalog, ColumnRole.TabId,      1);
            if (tabKey == null) return Array.Empty<CatalogTabEntry>();
            string tstKey = GetColumnKey(catalog, ColumnRole.TabSortKey, 1);

            var seen  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sorts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in catalog.Entries)
            {
                if (!entry.Values.TryGetValue(tabKey, out string cell) || string.IsNullOrEmpty(cell)) continue;
                string tstRaw = tstKey != null && entry.Values.TryGetValue(tstKey, out string tv) ? tv : null;
                foreach (var tab in CatalogDropdownItem.SplitTabIds(cell))   // Task #40: one cell may list several tabs ("A, B")
                {
                    bool isNew = !seen.ContainsKey(tab);
                    if (isNew) seen[tab] = tab;
                    // An explicit TST (typically from a definition row) wins regardless of row order;
                    // otherwise a freshly-seen tab falls back to sorting by its own name.
                    if (!string.IsNullOrEmpty(tstRaw)) sorts[tab] = tstRaw;
                    else if (isNew) sorts[tab] = tab;
                }
            }

            return seen.Keys
                .OrderBy(t => sorts[t], StringComparer.OrdinalIgnoreCase)
                .Select(t => new CatalogTabEntry(t, t, sorts[t]))
                .ToList();
        }

        /// <summary>
        /// Like <see cref="GetDropdownItemsForCard"/>, but reads SecRole/TooltipRole/Display_N_Role from the first
        /// enabled Button card (falls back to Dropdown card params, then catalog defaults).
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetButtonItemsForCard(
            CardGroup group, CatalogData catalog)
        {
            if (group == null) return GetDropdownItems(catalog);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeButton) continue;
                card.Params.TryGetValue(ParamSecRole,     out string sec);
                card.Params.TryGetValue(ParamTooltipRole, out string tip);
                return GetDropdownItems(catalog,
                    string.IsNullOrEmpty(sec) ? "SEC" : sec,
                    string.IsNullOrEmpty(tip) ? "AUX" : tip,
                    ReadDisplayRoles(card));
            }
            return GetDropdownItemsForCard(group, catalog);
        }

        /// <summary>
        /// Builds the item list for a Search card. Reads SecRole/TooltipRole/Display_N_Role/SearchRoles
        /// from the first enabled Search card. SearchRoles (comma-separated) determines which catalog
        /// columns are matched against the user's typed filter text.
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetSearchItemsForCard(
            CardGroup group, CatalogData catalog)
        {
            if (group == null) return GetDropdownItems(catalog);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeSearch) continue;
                card.Params.TryGetValue(ParamSecRole,     out string sec);
                card.Params.TryGetValue(ParamTooltipRole, out string tip);
                card.Params.TryGetValue(ParamSearchRoles, out string searchRolesStr);
                IReadOnlyList<string> searchRoles = null;
                if (!string.IsNullOrEmpty(searchRolesStr))
                {
                    var parts = searchRolesStr.Split(',');
                    var list  = new List<string>(parts.Length);
                    foreach (var p in parts) { string t = p.Trim(); if (!string.IsNullOrEmpty(t)) list.Add(t); }
                    if (list.Count > 0) searchRoles = list;
                }
                return GetDropdownItems(catalog,
                    string.IsNullOrEmpty(sec) ? "SEC" : sec,
                    string.IsNullOrEmpty(tip) ? "AUX" : tip,
                    ReadDisplayRoles(card),
                    searchRoles);
            }
            return GetDropdownItems(catalog);
        }

        // ── Multi-Pick card ───────────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled Multi-Pick card.</summary>
        public static bool HasMultiPickCard(CardGroup group)
            => group?.Cards.Any(c => c.Enabled && c.Type == CardTypeMultiPick) == true;

        /// <summary>
        /// Builds the catalog item list for a Multi-Pick card (same items as Button card).
        /// </summary>
        public static IReadOnlyList<CatalogDropdownItem> GetMultiPickItemsForCard(
            CardGroup group, CatalogData catalog)
        {
            if (group == null) return GetDropdownItems(catalog);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeMultiPick) continue;
                card.Params.TryGetValue(ParamSecRole,     out string sec);
                card.Params.TryGetValue(ParamTooltipRole, out string tip);
                return GetDropdownItems(catalog,
                    string.IsNullOrEmpty(sec) ? "SEC" : sec,
                    string.IsNullOrEmpty(tip) ? "AUX" : tip,
                    ReadDisplayRoles(card));
            }
            return GetDropdownItems(catalog);
        }

        /// <summary>
        /// Reads the Multi-Pick card configuration from the first enabled Multi-Pick card in
        /// <paramref name="group"/>. Returns defaults when no card is found.
        /// </summary>
        public static (string PrimarySep, string CompanionFieldKey, string CompanionRole, string CompanionSep)
            GetMultiPickConfig(CardGroup group)
        {
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled || card.Type != CardTypeMultiPick) continue;
                    card.Params.TryGetValue(ParamPrimaryTokenSeparator,   out string priSep);
                    card.Params.TryGetValue(ParamCompanionFieldKey,        out string compField);
                    card.Params.TryGetValue(ParamCompanionRole,            out string compRole);
                    card.Params.TryGetValue(ParamCompanionTokenSeparator,  out string compSep);
                    return (
                        string.IsNullOrEmpty(priSep)   ? "-"   : priSep,
                        compField ?? "",
                        string.IsNullOrEmpty(compRole) ? "SEC" : compRole,
                        string.IsNullOrEmpty(compSep)  ? ", "  : compSep);
                }
            }
            return ("-", "", "SEC", ", ");
        }

        /// <summary>
        /// For each PRI value in <paramref name="priValues"/>, looks up the <paramref name="companionRole"/>
        /// column value in <paramref name="catalog"/> and joins the results with <paramref name="companionSep"/>.
        /// Items with an empty lookup result are skipped.
        /// </summary>
        public static string BuildMultiPickCompanionValue(
            IEnumerable<string> priValues, CatalogData catalog, string companionRole, string companionSep)
        {
            if (priValues == null || catalog == null) return "";
            var parts = new List<string>();
            foreach (var pri in priValues)
            {
                if (string.IsNullOrEmpty(pri)) continue;
                string val = LookupRoleValue(catalog, pri, companionRole);
                if (!string.IsNullOrEmpty(val)) parts.Add(val);
            }
            return string.Join(companionSep ?? "", parts);
        }

        // ── Pair Transform card ───────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled Pair Transform card.</summary>
        public static bool HasPairTransformCard(CardGroup group)
            => group?.Cards.Any(c => c.Enabled && c.Type == CardTypePairTransform) == true;

        /// <summary>
        /// Reads Pair Transform card parameters from the first enabled PairTransform card in
        /// <paramref name="group"/>. Returns defaults when no card is found.
        /// </summary>
        public static (string SourceSep, string LookupRole, string OutputRole, string OutputSep, string CompanionFieldKey)
            GetPairTransformConfig(CardGroup group)
        {
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled || card.Type != CardTypePairTransform) continue;
                    card.Params.TryGetValue(ParamSourceTokenSeparator, out string srcSep);
                    card.Params.TryGetValue(ParamLookupRole,           out string lookupRole);
                    card.Params.TryGetValue(ParamOutputRole,           out string outputRole);
                    card.Params.TryGetValue(ParamOutputTokenSeparator, out string outSep);
                    card.Params.TryGetValue(ParamCompanionFieldKey,    out string compField);
                    return (
                        string.IsNullOrEmpty(srcSep)     ? "-"   : srcSep,
                        string.IsNullOrEmpty(lookupRole) ? "PRI" : lookupRole,
                        string.IsNullOrEmpty(outputRole) ? "SEC" : outputRole,
                        string.IsNullOrEmpty(outSep)     ? ", "  : outSep,
                        compField ?? "");
                }
            }
            return ("-", "PRI", "SEC", ", ", "");
        }

        /// <summary>
        /// Splits <paramref name="sourceValue"/> by <paramref name="sourceSep"/>, looks up each token
        /// in <paramref name="catalog"/> where the <paramref name="lookupRole"/> column equals the token,
        /// collects the <paramref name="outputRole"/> column value for each match, and joins them with
        /// <paramref name="outputSep"/>. Tokens with no catalog match are omitted from the result.
        /// </summary>
        public static string BuildPairTransformValue(
            string sourceValue, CatalogData catalog,
            string sourceSep, string lookupRole, string outputRole, string outputSep,
            string activeGeneration = null)
        {
            if (catalog == null || string.IsNullOrEmpty(sourceValue)) return "";

            string[] tokens = sourceValue.Split(
                new[] { sourceSep ?? "-" }, StringSplitOptions.RemoveEmptyEntries);

            // Resolve column keys once
            ParseRoleBadge(lookupRole ?? "PRI", out ColumnRole lRole, out int lIdx);
            ParseRoleBadge(outputRole ?? "SEC", out ColumnRole oRole, out int oIdx);
            string lKey = lRole != ColumnRole.None ? GetColumnKey(catalog, lRole, lIdx) : null;
            string oKey = oRole != ColumnRole.None ? GetColumnKey(catalog, oRole, oIdx) : null;
            DiagLogger.Log("pairtransform", $"BuildPairTransform: lRole={lRole} lKey={lKey ?? "(null)"} oRole={oRole} oKey={oKey ?? "(null)"}");
            if (lKey == null || oKey == null) return "";

            var parts = new List<string>();
            foreach (string rawToken in tokens)
            {
                string token = rawToken.Trim();
                if (token.Length == 0) continue;
                foreach (var entry in catalog.Entries)
                {
                    if (!EntryInGenerationScope(catalog, entry, activeGeneration)) continue;
                    entry.Values.TryGetValue(lKey, out string lVal);
                    if (!string.Equals(lVal, token, StringComparison.OrdinalIgnoreCase)) continue;
                    entry.Values.TryGetValue(oKey, out string oVal);
                    if (!string.IsNullOrEmpty(oVal)) parts.Add(oVal);
                    break;
                }
            }
            return string.Join(outputSep ?? "", parts);
        }

        // ── T43 sorted assembly ───────────────────────────────────────────────────

        /// <summary>One ordered piece of an assembled companion value, tagged with its catalog sort keys.</summary>
        public struct OrderedSegment
        {
            public int Placing;    // primary sort key — placing_order (GroupSortKey role)
            public int Internal;   // secondary sort key — internal_order (SortKey role)
            public int Order;      // tertiary tiebreak — stable production order
            public string Text;    // the long-form text
            public OrderedSegment(int placing, int @internal, int order, string text)
            { Placing = placing; Internal = @internal; Order = order; Text = text; }
        }

        private static int ReadIntCell(CatalogEntry entry, string key)
            => key != null && entry.Values.TryGetValue(key, out string v) && int.TryParse((v ?? "").Trim(), out int n) ? n : 0;

        /// <summary>
        /// T43 — PairTransform as ORDERED SEGMENTS (for the sorted-assembly path). One segment per direct-match
        /// token, carrying its catalog placing_order (GroupSortKey) + internal_order (SortKey) for sorting, plus
        /// its input position as a stable tiebreak. Generation-scoped like <see cref="BuildPairTransformValue"/>.
        /// </summary>
        public static List<OrderedSegment> BuildPairTransformSegments(
            string sourceValue, CatalogData catalog, string sourceSep,
            string lookupRole, string outputRole, string activeGeneration = null)
        {
            var segs = new List<OrderedSegment>();
            if (catalog == null || string.IsNullOrEmpty(sourceValue)) return segs;
            string[] tokens = sourceValue.Split(new[] { sourceSep ?? "-" }, StringSplitOptions.RemoveEmptyEntries);
            ParseRoleBadge(lookupRole ?? "PRI", out ColumnRole lRole, out int lIdx);
            ParseRoleBadge(outputRole ?? "SEC", out ColumnRole oRole, out int oIdx);
            string lKey = lRole != ColumnRole.None ? GetColumnKey(catalog, lRole, lIdx) : null;
            string oKey = oRole != ColumnRole.None ? GetColumnKey(catalog, oRole, oIdx) : null;
            if (lKey == null || oKey == null) return segs;
            string pKey = GetColumnKey(catalog, ColumnRole.GroupSortKey, 1);   // placing_order
            string iKey = GetColumnKey(catalog, ColumnRole.SortKey, 1);        // internal_order
            int pos = 0;
            foreach (string rawToken in tokens)
            {
                string token = rawToken.Trim();
                if (token.Length != 0)
                    foreach (var entry in catalog.Entries)
                    {
                        if (!EntryInGenerationScope(catalog, entry, activeGeneration)) continue;
                        entry.Values.TryGetValue(lKey, out string lVal);
                        if (!string.Equals(lVal, token, StringComparison.OrdinalIgnoreCase)) continue;
                        entry.Values.TryGetValue(oKey, out string oVal);
                        if (!string.IsNullOrEmpty(oVal))
                            segs.Add(new OrderedSegment(ReadIntCell(entry, pKey), ReadIntCell(entry, iKey), pos, oVal));
                        break;
                    }
                pos++;
            }
            return segs;
        }

        /// <summary>
        /// T43 — sort a set of <see cref="OrderedSegment"/>s by (placing_order, internal_order, production order),
        /// drop empties, and join with <paramref name="separator"/>. The canonical-order assembly that replaces
        /// the append chain for the SPEZIFIK pipeline.
        /// </summary>
        public static string AssembleSorted(IEnumerable<OrderedSegment> segments, string separator)
        {
            if (segments == null) return "";
            var ordered = segments
                .Where(s => !string.IsNullOrEmpty(s.Text))
                .OrderBy(s => s.Placing).ThenBy(s => s.Internal).ThenBy(s => s.Order)
                .Select(s => s.Text);
            return string.Join(separator ?? ", ", ordered);
        }

        /// <summary>
        /// T43 — canonicalize the SHORT form: reorder <paramref name="sourceValue"/>'s tokens by the same placing
        /// the sorted assembly uses, so the short matches the long's order. A token's placing = its direct-match
        /// catalog GroupSortKey (generation-scoped); a packed token with no direct match takes the OutputPlacing of
        /// the first enabled Compose card that expands it. Unrecognized tokens keep their relative order, last.
        /// </summary>
        public static string BuildSortedShort(
            CardGroup group, CatalogData primaryCatalog, string sourceValue,
            string sourceSep, string lookupRole, string activeGeneration,
            Func<string, CatalogData> getCatalogById)
        {
            if (group == null || primaryCatalog == null || string.IsNullOrEmpty(sourceValue)) return sourceValue;
            string sep = string.IsNullOrEmpty(sourceSep) ? "-" : sourceSep;
            string[] tokens = sourceValue.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= 1) return sourceValue;
            string pKey = GetColumnKey(primaryCatalog, ColumnRole.GroupSortKey, 1);
            ParseRoleBadge(lookupRole ?? "PRI", out ColumnRole lRole, out int lIdx);
            string lKey = lRole != ColumnRole.None ? GetColumnKey(primaryCatalog, lRole, lIdx) : null;

            var ranked = new List<(int placing, int idx, string token)>();
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Length == 0) continue;
                int placing = int.MaxValue;
                bool direct = false;
                if (lKey != null)
                    foreach (var e in primaryCatalog.Entries)
                    {
                        if (!EntryInGenerationScope(primaryCatalog, e, activeGeneration)) continue;
                        if (e.Values.TryGetValue(lKey, out string lv) && string.Equals(lv, token, StringComparison.OrdinalIgnoreCase))
                        { placing = ReadIntCell(e, pKey); direct = true; break; }
                    }
                if (!direct)
                    foreach (var card in group.Cards)
                    {
                        if (!card.Enabled || card.Type != CardTypeCompose) continue;
                        var cfg = ReadComposeCard(card);
                        string r;
                        if (cfg.IsSplitMode)
                        {
                            CatalogData fb = (!string.IsNullOrEmpty(cfg.FallbackCatalogId) && getCatalogById != null)
                                ? getCatalogById(cfg.FallbackCatalogId) ?? primaryCatalog : primaryCatalog;
                            r = BuildComposeSplitValue(token, primaryCatalog, fb, cfg, activeGeneration);
                        }
                        else r = BuildComposeValue(token, primaryCatalog, cfg, activeGeneration);
                        if (!string.IsNullOrEmpty(r)) { placing = cfg.OutputPlacing; break; }
                    }
                ranked.Add((placing, i, token));
            }
            return string.Join(sep, ranked.OrderBy(x => x.placing).ThenBy(x => x.idx).Select(x => x.token));
        }

        // ── Compose card ─────────────────────────────────────────────────────────
        // The no-separator sibling of PairTransform: splits ONE packed field value into catalog
        // codes by LONGEST-MATCH (handles mixed-length codes that no literal-separator split can),
        // looks each up, and frames the result (per-item prefixes, collapse-when-equal, empty-drop).
        // All framing is parameterised, so the card itself stays domain-neutral.
        public const string CardTypeCompose              = "Compose";
        public const string ParamComposeMaxItems         = "MaxItems";            // 0/absent = unlimited
        public const string ParamComposeItemSeparator    = "ItemSeparator";       // joins differing items
        public const string ParamComposeItemPrefixes     = "ItemPrefixes";        // comma-separated, positional; spaces preserved
        public const string ParamComposeCollapseWhenEqual= "CollapseWhenEqual";   // "true" collapses identical codes into one
        public const string ParamComposeCollapsedPrefix  = "CollapsedPrefix";     // prefix used in the collapsed case
        public const string ParamComposeDropEmptyOutputs = "DropEmptyOutputs";    // "false" keeps empty-output codes
        public const string ParamComposeOnUnknownToken       = "OnUnknownToken";         // skip | keepRaw | passthrough
        // ── Split Mode params ─────────────────────────────────────────────────
        public const string ParamComposeSourceSeparator       = "SourceSeparator";        // enables split mode; splits source on this before processing
        public const string ParamComposeTokenOutputSeparator  = "TokenOutputSeparator";   // joins per-token outputs; default ", "
        public const string ParamComposeFallbackCatalogId     = "ComposeFallbackCatalogId"; // catalog for sub-tokenization (avoids Merkmal collisions)
        public const string ParamComposeOnDirectMatch         = "OnDirectMatch";           // include (default) | skip
        public const string ParamComposeOutputMode            = "OutputMode";              // replace (default) | append
        public const string ParamComposeAppendSeparator       = "AppendSeparator";         // separator when appending to non-empty companion; default ", "
        public const string ParamComposeFallbackCategoryColumn = "ComposeFallbackCategoryColumn"; // T43: column (role badge or key/label) holding the category
        public const string ParamComposeFallbackCategory       = "ComposeFallbackCategory";       // T43: restrict sub-tokenization to this category (replaces a separate sub-catalog)
        public const string ParamComposeOutputPlacing          = "OutputPlacing";                 // T43: this card's segment rank in the sorted assembly (placing_order)
        public const string ParamComposeOutputInternal         = "OutputInternal";                // T43: secondary rank within the same placing

        /// <summary>Resolved Compose-card config. Reuses ParamLookupRole / ParamOutputRole / ParamCompanionFieldKey.</summary>
        public sealed class ComposeConfig
        {
            public string LookupRole        { get; set; } = "PRI";
            public string OutputRole        { get; set; } = "SEC";
            public string CompanionFieldKey { get; set; } = "";
            public int    MaxItems          { get; set; } = 0;       // 0 = unlimited
            public string ItemSeparator     { get; set; } = " / ";
            public IReadOnlyList<string> ItemPrefixes { get; set; } = Array.Empty<string>();
            public bool   CollapseWhenEqual { get; set; } = false;
            public string CollapsedPrefix   { get; set; } = "";
            public bool   DropEmptyOutputs  { get; set; } = true;
            public string OnUnknownToken    { get; set; } = "skip";
            // Split Mode
            public string SourceSeparator       { get; set; } = "";
            public string TokenOutputSeparator  { get; set; } = ", ";
            public string FallbackCatalogId     { get; set; } = "";
            public string OnDirectMatch         { get; set; } = "include";  // "include" | "skip"
            public string OutputMode            { get; set; } = "replace";  // "replace" | "append"
            public string AppendSeparator       { get; set; } = ", ";
            public string FallbackCategoryColumn { get; set; } = "";   // T43: column holding the category (role badge or key/label)
            public string FallbackCategory       { get; set; } = "";   // T43: restrict sub-tokenization to this category value
            public int    OutputPlacing          { get; set; } = 0;    // T43: segment rank in the sorted assembly (placing_order)
            public int    OutputInternal         { get; set; } = 0;    // T43: secondary rank within the same placing
            public bool   IsSplitMode           => !string.IsNullOrEmpty(SourceSeparator);
        }

        /// <summary>Returns true when the group has at least one enabled Compose card.</summary>
        public static bool HasComposeCard(CardGroup group)
            => group?.Cards.Any(c => c.Enabled && c.Type == CardTypeCompose) == true;

        /// <summary>Reads the first enabled Compose card's config (for the editor). Defaults when none found.</summary>
        public static ComposeConfig GetComposeConfig(CardGroup group)
        {
            if (group != null)
                foreach (var card in group.Cards)
                    if (card.Enabled && card.Type == CardTypeCompose)
                        return ReadComposeCard(card);
            return new ComposeConfig();
        }

        private static ComposeConfig ReadComposeCard(CapabilityCard card)
        {
            var cfg = new ComposeConfig();
            if (card == null) return cfg;
            if (card.Params.TryGetValue(ParamLookupRole,             out string lr) && !string.IsNullOrEmpty(lr)) cfg.LookupRole = lr;
            if (card.Params.TryGetValue(ParamOutputRole,             out string or) && !string.IsNullOrEmpty(or)) cfg.OutputRole = or;
            if (card.Params.TryGetValue(ParamCompanionFieldKey,      out string comp)) cfg.CompanionFieldKey = comp ?? "";
            if (card.Params.TryGetValue(ParamComposeMaxItems,        out string mx) && int.TryParse(mx, out int m) && m >= 0) cfg.MaxItems = m;
            if (card.Params.TryGetValue(ParamComposeItemSeparator,   out string sep) && sep != null) cfg.ItemSeparator = sep;
            if (card.Params.TryGetValue(ParamComposeItemPrefixes,    out string pfx) && !string.IsNullOrEmpty(pfx)) cfg.ItemPrefixes = pfx.Split(',');
            if (card.Params.TryGetValue(ParamComposeCollapseWhenEqual, out string col)) cfg.CollapseWhenEqual = string.Equals(col, "true", StringComparison.OrdinalIgnoreCase);
            if (card.Params.TryGetValue(ParamComposeCollapsedPrefix, out string cp) && cp != null) cfg.CollapsedPrefix = cp;
            if (card.Params.TryGetValue(ParamComposeDropEmptyOutputs, out string de)) cfg.DropEmptyOutputs = !string.Equals(de, "false", StringComparison.OrdinalIgnoreCase);
            if (card.Params.TryGetValue(ParamComposeOnUnknownToken,       out string ou)  && !string.IsNullOrEmpty(ou))  cfg.OnUnknownToken       = ou;
            if (card.Params.TryGetValue(ParamComposeSourceSeparator,      out string ss)  && ss != null)                   cfg.SourceSeparator       = ss;
            if (card.Params.TryGetValue(ParamComposeTokenOutputSeparator, out string tos) && tos != null)                  cfg.TokenOutputSeparator  = tos;
            if (card.Params.TryGetValue(ParamComposeFallbackCatalogId,    out string fci) && !string.IsNullOrEmpty(fci))   cfg.FallbackCatalogId     = fci;
            if (card.Params.TryGetValue(ParamComposeOnDirectMatch,        out string odm) && !string.IsNullOrEmpty(odm))   cfg.OnDirectMatch         = odm;
            if (card.Params.TryGetValue(ParamComposeOutputMode,           out string om)  && !string.IsNullOrEmpty(om))    cfg.OutputMode            = om;
            if (card.Params.TryGetValue(ParamComposeAppendSeparator,      out string aps) && aps != null)                  cfg.AppendSeparator       = aps;
            if (card.Params.TryGetValue(ParamComposeFallbackCategoryColumn, out string fcc) && !string.IsNullOrEmpty(fcc)) cfg.FallbackCategoryColumn = fcc;
            if (card.Params.TryGetValue(ParamComposeFallbackCategory,       out string fcv) && !string.IsNullOrEmpty(fcv)) cfg.FallbackCategory       = fcv;
            if (card.Params.TryGetValue(ParamComposeOutputPlacing,          out string opl) && int.TryParse(opl, out int opli)) cfg.OutputPlacing  = opli;
            if (card.Params.TryGetValue(ParamComposeOutputInternal,         out string oin) && int.TryParse(oin, out int oini)) cfg.OutputInternal = oini;
            return cfg;
        }

        /// <summary>
        /// Splits <paramref name="sourceValue"/> into catalog codes by longest-match against the
        /// LookupRole column (case-insensitive), looks up each code's OutputRole value, and frames the
        /// result. Returns "" when nothing is produced; returns null ONLY when the value cannot be fully
        /// tokenised AND OnUnknownToken="passthrough" (signal to the caller: leave the companion untouched).
        /// </summary>
        public static string BuildComposeValue(string sourceValue, CatalogData catalog, ComposeConfig cfg, string activeGeneration = null)
        {
            if (cfg == null || catalog == null || string.IsNullOrEmpty(sourceValue)) return "";
            string lookupKey = GetRoleKey(catalog, string.IsNullOrEmpty(cfg.LookupRole) ? "PRI" : cfg.LookupRole);
            string outputKey = GetRoleKey(catalog, string.IsNullOrEmpty(cfg.OutputRole) ? "SEC" : cfg.OutputRole);
            if (lookupKey == null || outputKey == null) return "";

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in catalog.Entries)
                if (EntryInGenerationScope(catalog, e, activeGeneration) &&
                    EntryInCategory(catalog, e, cfg.FallbackCategoryColumn, cfg.FallbackCategory) &&
                    e.Values.TryGetValue(lookupKey, out string lv) && !string.IsNullOrEmpty(lv))
                    candidates.Add(lv);
            if (candidates.Count == 0) return "";
            var lengthsDesc = candidates.Select(s => s.Length).Distinct().OrderByDescending(x => x).ToList();

            var (codes, full) = LongestMatchTokenize(sourceValue, candidates, lengthsDesc, cfg.MaxItems);
            string mode = (cfg.OnUnknownToken ?? "skip").ToLowerInvariant();
            if (!full)
            {
                if (mode == "passthrough") return null;
                if (mode == "keepraw")     return sourceValue;
                // "skip": continue with whatever parsed
            }
            if (codes.Count == 0)
                return mode == "keepraw" ? sourceValue : (mode == "passthrough" ? null : "");

            var items = new List<(string code, string output)>(codes.Count);
            foreach (var code in codes)
            {
                string output = "";
                foreach (var e in catalog.Entries)
                    if (EntryInGenerationScope(catalog, e, activeGeneration) &&
                        EntryInCategory(catalog, e, cfg.FallbackCategoryColumn, cfg.FallbackCategory) &&
                        e.Values.TryGetValue(lookupKey, out string lv) &&
                        string.Equals(lv, code, StringComparison.OrdinalIgnoreCase))
                    { e.Values.TryGetValue(outputKey, out output); output ??= ""; break; }
                items.Add((code, output));
            }
            if (cfg.DropEmptyOutputs) items = items.Where(it => !string.IsNullOrEmpty(it.output)).ToList();
            if (items.Count == 0) return "";

            if (cfg.CollapseWhenEqual && items.Count >= 2 &&
                items.All(it => string.Equals(it.code, items[0].code, StringComparison.OrdinalIgnoreCase)))
                return (cfg.CollapsedPrefix ?? "") + items[0].output;

            var parts = new List<string>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                string prefix = (cfg.ItemPrefixes != null && i < cfg.ItemPrefixes.Count) ? cfg.ItemPrefixes[i] : "";
                parts.Add((prefix ?? "") + items[i].output);
            }
            return string.Join(cfg.ItemSeparator ?? "", parts);
        }

        /// <summary>
        /// Longest-match tokeniser: walks <paramref name="value"/> left→right, at each position taking the
        /// LONGEST candidate code that prefixes the remainder. Stops (full=false) on the first position with
        /// no match, or once <paramref name="maxItems"/> (when &gt;0) codes are taken with input still
        /// remaining. full=true only when the whole value is consumed.
        /// </summary>
        private static (List<string> codes, bool full) LongestMatchTokenize(
            string value, HashSet<string> candidates, List<int> lengthsDesc, int maxItems)
        {
            var codes = new List<string>();
            int pos = 0, n = value.Length;
            while (pos < n)
            {
                if (maxItems > 0 && codes.Count >= maxItems) return (codes, false);
                bool matched = false;
                foreach (int len in lengthsDesc)
                {
                    if (pos + len > n) continue;
                    string chunk = value.Substring(pos, len);
                    if (candidates.Contains(chunk)) { codes.Add(chunk); pos += len; matched = true; break; }
                }
                if (!matched) return (codes, false);
            }
            return (codes, true);
        }

        /// <summary>
        /// Returns all (fieldKey, value) writes from enabled Compose cards for <paramref name="sourceValue"/>.
        /// A card whose BuildComposeValue returns null (passthrough on un-tokenisable input) is skipped.
        /// </summary>
        public static IEnumerable<(string FieldKey, string Value)> GetComposeWrites(
            CardGroup group, CatalogData catalog, string sourceValue)
        {
            if (group == null || catalog == null) yield break;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeCompose) continue;
                var cfg = ReadComposeCard(card);
                if (string.IsNullOrEmpty(cfg.CompanionFieldKey)) continue;
                string result = BuildComposeValue(sourceValue, catalog, cfg);
                if (result == null) continue;   // passthrough → leave companion untouched
                yield return (cfg.CompanionFieldKey, result);
            }
        }

        /// <summary>
        /// Split-Mode-aware variant of <see cref="GetComposeWrites"/>.
        /// Returns one write instruction per enabled Compose card. The tuple includes
        /// <c>IsAppend</c> (true when OutputMode=append) and <c>AppendSeparator</c> so the
        /// caller can combine the new value with the companion field's existing content.
        /// <paramref name="getCatalogById"/> resolves <see cref="ComposeConfig.FallbackCatalogId"/>
        /// for split-mode cards; pass null to always use <paramref name="primaryCatalog"/>.
        /// </summary>
        public static IEnumerable<(string FieldKey, string Value, bool IsAppend, string AppendSeparator)>
            GetComposeWritesEx(
                CardGroup group, CatalogData primaryCatalog, string sourceValue,
                Func<string, CatalogData> getCatalogById = null, string activeGeneration = null)
        {
            if (group == null || primaryCatalog == null) yield break;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeCompose) continue;
                var cfg = ReadComposeCard(card);
                if (string.IsNullOrEmpty(cfg.CompanionFieldKey)) continue;

                string result;
                if (cfg.IsSplitMode)
                {
                    CatalogData fallback = (!string.IsNullOrEmpty(cfg.FallbackCatalogId) && getCatalogById != null)
                        ? getCatalogById(cfg.FallbackCatalogId) ?? primaryCatalog
                        : primaryCatalog;
                    result = BuildComposeSplitValue(sourceValue, primaryCatalog, fallback, cfg, activeGeneration);
                }
                else
                {
                    result = BuildComposeValue(sourceValue, primaryCatalog, cfg, activeGeneration);
                }

                if (result == null) continue;   // passthrough — leave companion untouched
                bool isAppend = string.Equals(cfg.OutputMode, "append", StringComparison.OrdinalIgnoreCase);
                if (isAppend && result == "") continue;  // nothing to append — skip to avoid spurious writes
                yield return (cfg.CompanionFieldKey, result, isAppend, cfg.AppendSeparator);
            }
        }

        /// <summary>
        /// T43 — Compose cards as ORDERED SEGMENTS for the sorted-assembly path. Each enabled Compose card
        /// produces one segment for its companion field, tagged with the card's <see cref="ComposeConfig.OutputPlacing"/>
        /// / <see cref="ComposeConfig.OutputInternal"/>; generation-scoped via <paramref name="activeGeneration"/>.
        /// The Order tiebreak (100000+) places compose segments after direct PairTransform tokens of equal placing.
        /// </summary>
        public static IEnumerable<(string FieldKey, OrderedSegment Segment)> GetComposeSegments(
            CardGroup group, CatalogData primaryCatalog, string sourceValue,
            Func<string, CatalogData> getCatalogById, string activeGeneration)
        {
            if (group == null || primaryCatalog == null) yield break;
            int order = 0;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeCompose) continue;
                var cfg = ReadComposeCard(card);
                if (string.IsNullOrEmpty(cfg.CompanionFieldKey)) continue;

                string result;
                if (cfg.IsSplitMode)
                {
                    CatalogData fallback = (!string.IsNullOrEmpty(cfg.FallbackCatalogId) && getCatalogById != null)
                        ? getCatalogById(cfg.FallbackCatalogId) ?? primaryCatalog
                        : primaryCatalog;
                    result = BuildComposeSplitValue(sourceValue, primaryCatalog, fallback, cfg, activeGeneration);
                }
                else
                {
                    result = BuildComposeValue(sourceValue, primaryCatalog, cfg, activeGeneration);
                }

                if (string.IsNullOrEmpty(result)) continue;   // passthrough / empty → no segment
                yield return (cfg.CompanionFieldKey,
                    new OrderedSegment(cfg.OutputPlacing, cfg.OutputInternal, 100000 + order, result));
                order++;
            }
        }

        /// <summary>
        /// Returns the companion field keys of all enabled Compose cards in <paramref name="group"/>
        /// that have a non-empty companion key. Used by the sorted-assembly path to detect companion
        /// fields that produced no segments and must be explicitly cleared.
        /// </summary>
        public static IEnumerable<string> GetComposeCompanionKeys(CardGroup group)
        {
            if (group == null) yield break;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeCompose) continue;
                var cfg = ReadComposeCard(card);
                if (!string.IsNullOrEmpty(cfg.CompanionFieldKey))
                    yield return cfg.CompanionFieldKey;
            }
        }

        /// <summary>
        /// Split-Mode expansion: splits <paramref name="sourceValue"/> on
        /// <see cref="ComposeConfig.SourceSeparator"/>, then for each outer token attempts a
        /// direct PRI→SEC lookup in <paramref name="primaryCatalog"/>; on miss, falls back to
        /// <see cref="BuildComposeValue"/> against <paramref name="fallbackCatalog"/>.
        /// <see cref="ComposeConfig.OnDirectMatch"/> controls whether direct-match tokens
        /// contribute to the output ("include") or are silently skipped ("skip").
        /// All per-token outputs are joined with <see cref="ComposeConfig.TokenOutputSeparator"/>.
        /// </summary>
        public static string BuildComposeSplitValue(
            string sourceValue, CatalogData primaryCatalog, CatalogData fallbackCatalog, ComposeConfig cfg,
            string activeGeneration = null)
        {
            if (cfg == null || primaryCatalog == null || string.IsNullOrEmpty(sourceValue)) return "";
            string[] outerTokens = sourceValue.Split(
                new[] { cfg.SourceSeparator }, StringSplitOptions.RemoveEmptyEntries);
            bool skipDirect = string.Equals(cfg.OnDirectMatch, "skip", StringComparison.OrdinalIgnoreCase);
            var parts = new List<string>();
            foreach (string rawToken in outerTokens)
            {
                string token = rawToken.Trim();
                if (token.Length == 0) continue;
                string directSec = FindDirectMatchSec(token, primaryCatalog, cfg.LookupRole, cfg.OutputRole, activeGeneration);
                if (directSec != null)
                {
                    if (!skipDirect && (!string.IsNullOrEmpty(directSec) || !cfg.DropEmptyOutputs))
                        parts.Add(directSec);
                    continue;
                }
                // No direct match → attempt Compose sub-tokenization in fallback catalog
                string subResult = BuildComposeValue(token, fallbackCatalog ?? primaryCatalog, cfg, activeGeneration);
                if (subResult == null) continue;                        // passthrough
                if (!string.IsNullOrEmpty(subResult)) parts.Add(subResult);
            }
            return string.Join(cfg.TokenOutputSeparator ?? ", ", parts);
        }

        /// <summary>
        /// Looks up <paramref name="token"/> by exact case-insensitive match in the
        /// <paramref name="lookupRole"/> column of <paramref name="catalog"/> and returns the
        /// corresponding <paramref name="outputRole"/> value. Returns null when no entry matches
        /// (no match), or the SEC string (possibly empty) when one does.
        /// </summary>
        private static string FindDirectMatchSec(
            string token, CatalogData catalog, string lookupRole, string outputRole,
            string activeGeneration = null)
        {
            if (catalog == null || string.IsNullOrEmpty(token)) return null;
            string lKey = GetRoleKey(catalog, string.IsNullOrEmpty(lookupRole) ? "PRI" : lookupRole);
            string oKey = GetRoleKey(catalog, string.IsNullOrEmpty(outputRole) ? "SEC" : outputRole);
            if (lKey == null || oKey == null) return null;
            foreach (var entry in catalog.Entries)
            {
                if (!EntryInGenerationScope(catalog, entry, activeGeneration)) continue;
                if (entry.Values.TryGetValue(lKey, out string lv) &&
                    string.Equals(lv, token, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Values.TryGetValue(oKey, out string ov);
                    return ov ?? "";    // matched (SEC may be empty for intentional empty-drop codes)
                }
            }
            return null;    // no match
        }

        // ── PrefixSuffix card ─────────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled PrefixSuffix card.</summary>
        public static bool HasPrefixSuffixCard(CardGroup group)
            => group?.Cards.Any(c => c.Enabled && c.Type == CardTypePrefixSuffix) == true;

        /// <summary>
        /// Reads PrefixSuffix card parameters from the first enabled PrefixSuffix card in
        /// <paramref name="group"/>. Returns empty strings and false when no card is found.
        /// </summary>
        public static (string Prefix, string Suffix, bool IsRemoveMode) GetPrefixSuffixConfig(CardGroup group)
        {
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled || card.Type != CardTypePrefixSuffix) continue;
                    card.Params.TryGetValue(ParamPrefix,       out string prefix);
                    card.Params.TryGetValue(ParamSuffix,       out string suffix);
                    card.Params.TryGetValue(ParamIsRemoveMode, out string isRemoveModeStr);
                    bool isRemoveMode = string.Equals(isRemoveModeStr, "true", StringComparison.OrdinalIgnoreCase);
                    return (prefix ?? "", suffix ?? "", isRemoveMode);
                }
            }
            return ("", "", false);
        }

        /// <summary>
        /// Applies the PrefixSuffix card to <paramref name="value"/>:
        /// in Add mode prepends <paramref name="prefix"/> and appends <paramref name="suffix"/>;
        /// in Remove mode strips them from start/end (case-sensitive, only if present).
        /// </summary>
        public static string ApplyPrefixSuffix(string value, string prefix, string suffix, bool isRemoveMode)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            if (isRemoveMode)
            {
                if (!string.IsNullOrEmpty(prefix) && value.StartsWith(prefix, StringComparison.Ordinal))
                    value = value.Substring(prefix.Length);
                if (!string.IsNullOrEmpty(suffix) && value.EndsWith(suffix, StringComparison.Ordinal))
                    value = value.Substring(0, value.Length - suffix.Length);
                return value;
            }
            return (prefix ?? "") + value + (suffix ?? "");
        }

        // ── Sort card ─────────────────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled Sort card.</summary>
        public static bool HasSortCard(CardGroup group)
            => group?.Cards.Any(c => c.Enabled && c.Type == CardTypeSort) == true;

        /// <summary>
        /// Reads Sort card parameters from the first enabled Sort card in <paramref name="group"/>.
        /// Returns defaults when no card is found.
        /// </summary>
        public static (string TokenSep, string LookupRole, bool IsInvert) GetSortConfig(CardGroup group)
        {
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled || card.Type != CardTypeSort) continue;
                    card.Params.TryGetValue(ParamSortTokenSeparator, out string sep);
                    card.Params.TryGetValue(ParamSortLookupRole,     out string lr);
                    card.Params.TryGetValue(ParamSortInvert,         out string invStr);
                    bool isInvert = string.Equals(invStr, "true", StringComparison.OrdinalIgnoreCase);
                    return (string.IsNullOrEmpty(sep) ? "-" : sep,
                            string.IsNullOrEmpty(lr)  ? "PRI" : lr,
                            isInvert);
                }
            }
            return ("-", "PRI", false);
        }

        /// <summary>
        /// Splits <paramref name="sourceValue"/> by <paramref name="tokenSep"/>, looks up each token
        /// in <paramref name="catalog"/> via the <paramref name="lookupRole"/> column, sorts by SRT
        /// column(s) in ascending order (or descending when <paramref name="isInvert"/> is true),
        /// and rejoins with the same separator. Unknown tokens are appended after known ones.
        /// Returns the original value unchanged when the catalog is null or has no SRT columns.
        /// </summary>
        public static string BuildSortedValue(
            string sourceValue, CatalogData catalog, string lookupRole, string tokenSep, bool isInvert)
        {
            if (catalog == null || string.IsNullOrEmpty(sourceValue)) return sourceValue ?? "";
            string sep = tokenSep ?? "-";

            // Resolve the lookup column
            string lKey = GetRoleKey(catalog, string.IsNullOrEmpty(lookupRole) ? "PRI" : lookupRole);
            if (lKey == null) return sourceValue;

            // Collect SRT column keys in RoleIndex order (multi-level sort)
            var srtKeys = catalog.Columns
                .Where(c => c.Role == ColumnRole.SortKey)
                .OrderBy(c => c.RoleIndex)
                .ThenBy(c => c.Key)
                .Select(c => c.Key)
                .ToList();
            if (srtKeys.Count == 0) return sourceValue;

            string[] tokens = sourceValue.Split(new[] { sep }, StringSplitOptions.None);
            var known   = new List<(List<string> Srt, int CatIdx, string Token)>();
            var unknown = new List<string>();

            for (int ti = 0; ti < tokens.Length; ti++)
            {
                string token = tokens[ti];
                bool found = false;
                for (int ei = 0; ei < catalog.Entries.Count; ei++)
                {
                    var entry = catalog.Entries[ei];
                    if (!entry.Values.TryGetValue(lKey, out string lv) ||
                        !string.Equals(lv, token, StringComparison.OrdinalIgnoreCase)) continue;
                    var srt = srtKeys
                        .Select(k => entry.Values.TryGetValue(k, out string sv) ? sv ?? "" : "")
                        .ToList();
                    known.Add((srt, ei, token));
                    found = true;
                    break;
                }
                if (!found) unknown.Add(token);
            }

            known.Sort((x, y) =>
            {
                for (int ci = 0; ci < Math.Max(x.Srt.Count, y.Srt.Count); ci++)
                {
                    string xv = ci < x.Srt.Count ? x.Srt[ci] : "";
                    string yv = ci < y.Srt.Count ? y.Srt[ci] : "";
                    int cmp = string.Compare(xv, yv, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return isInvert ? -cmp : cmp;
                }
                int idxCmp = x.CatIdx.CompareTo(y.CatIdx);
                return isInvert ? -idxCmp : idxCmp;
            });

            var parts = known.Select(k => k.Token).Concat(unknown);
            return string.Join(sep, parts);
        }

        // ── Basic Logic (BL) card ─────────────────────────────────────────────

        /// <summary>Returns true when the group has at least one enabled Basic Logic card.</summary>
        public static bool HasBasicLogicCard(CardGroup group)
            => group?.Cards.Any(c => c.Enabled && c.Type == CardTypeBasicLogic) == true;

        /// <summary>
        /// Evaluates every enabled BL card in <paramref name="group"/> against <paramref name="context"/>
        /// and yields (FieldKey, Value) writes. Each BL writes to its own ParamFormulaTargetFieldKey
        /// (or to <paramref name="group"/>.TargetFieldKey when blank).
        /// </summary>
        public static IEnumerable<(string FieldKey, string Value)> GetBasicLogicWrites(
            CardGroup group, FormulaContext context)
        {
            if (group == null || context == null) yield break;
            string ownLogicKey = group.Id != null ? "SPECIAL:LOGIC:" + group.Id : null;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeBasicLogic) continue;
                if (!card.Params.TryGetValue(ParamFormula, out string formula) || string.IsNullOrWhiteSpace(formula))
                    continue;

                card.Params.TryGetValue(ParamFormulaTargetFieldKey, out string targetKey);
                if (string.IsNullOrEmpty(targetKey)) targetKey = group.TargetFieldKey;
                if (string.IsNullOrEmpty(targetKey)) continue;

                // SAFEGUARD 1 — cycle detection: BL formula must not reference its own target field
                // OR the SPECIAL:LOGIC:{groupId} alias (which resolves to the same field). Either path
                // would create a read-X / write-X cycle that risks an Inventor event-loop crash.
                if (FormulaReferencesField(formula, targetKey))
                {
                    DiagLogger.Log("basiclogic", $"SKIP: formula references own target '{targetKey}' (direct cycle). formula='{formula}'");
                    continue;
                }
                if (ownLogicKey != null && FormulaReferencesField(formula, ownLogicKey))
                {
                    DiagLogger.Log("basiclogic", $"SKIP: formula references own SPECIAL:LOGIC alias '{ownLogicKey}' (indirect cycle). formula='{formula}'");
                    continue;
                }

                string result;
                try { result = FormulaEngine.Evaluate(formula, context); }
                catch (Exception ex)
                {
                    DiagLogger.Log("basiclogic", $"SKIP: evaluation error for target '{targetKey}': {DiagLogger.S(ex.Message)}");
                    continue; // SAFEGUARD 2 — never write error strings to FieldWriter (would crash Inventor)
                }
                if (result != null && result.StartsWith("#ERROR", StringComparison.Ordinal))
                {
                    DiagLogger.Log("basiclogic", $"SKIP: result starts with #ERROR for target '{targetKey}'. result='{DiagLogger.S(result)}'");
                    continue;
                }

                yield return (targetKey, result ?? "");
            }
        }

        /// <summary>True when <paramref name="formula"/> contains a <c>{fieldKey}</c> or <c>$[fieldKey]</c> token equal to the candidate (case-insensitive).</summary>
        private static bool FormulaReferencesField(string formula, string fieldKey)
        {
            if (string.IsNullOrEmpty(formula) || string.IsNullOrEmpty(fieldKey)) return false;
            // Check {FIELD_KEY} tokens
            int i = 0;
            while (i < formula.Length)
            {
                int open = formula.IndexOf('{', i);
                if (open < 0) break;
                int close = formula.IndexOf('}', open + 1);
                if (close < 0) break;
                string inside = formula.Substring(open + 1, close - open - 1).Trim();
                if (string.Equals(inside, fieldKey, StringComparison.OrdinalIgnoreCase))
                    return true;
                i = close + 1;
            }
            // Check $[FIELD_KEY] Expert Mode tokens
            foreach (string key in FormulaEngine.GetExpertRefs(formula))
                if (string.Equals(key, fieldKey, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// True when the group has at least one enabled BasicLogic card whose write target matches
        /// <paramref name="targetFieldKey"/>. Used to detect "BL is the authoritative writer" so the
        /// raw user input is not written first (which would corrupt parameters with non-numeric input).
        /// </summary>
        public static bool HasBasicLogicWritingTo(CardGroup group, string targetFieldKey)
        {
            if (group == null || string.IsNullOrEmpty(targetFieldKey)) return false;
            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeBasicLogic) continue;
                if (!card.Params.TryGetValue(ParamFormula, out string formula) || string.IsNullOrWhiteSpace(formula))
                    continue;
                card.Params.TryGetValue(ParamFormulaTargetFieldKey, out string targetKey);
                if (string.IsNullOrEmpty(targetKey)) targetKey = group.TargetFieldKey;
                if (string.Equals(targetKey, targetFieldKey, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ── Sync card ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all (fieldKey, value) pairs that enabled Sync cards want written when the
        /// primary field changes to <paramref name="priValue"/>.
        /// Each Sync card can specify a CompanionRole (default "SEC") to choose which catalog
        /// column value is written to the companion field.
        /// </summary>
        public static IEnumerable<(string FieldKey, string Value)> GetSyncWrites(
            CardGroup group, CatalogData catalog, string priValue)
        {
            if (group == null || catalog == null) yield break;

            // Cache role lookups across cards that share the same role.
            var roleValueCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var card in group.Cards)
            {
                if (!card.Enabled || card.Type != CardTypeSync) continue;
                if (!card.Params.TryGetValue(ParamCompanionFieldKey, out string compKey)) continue;
                if (string.IsNullOrEmpty(compKey)) continue;

                card.Params.TryGetValue(ParamCompanionRole, out string roleBadge);
                if (string.IsNullOrEmpty(roleBadge)) roleBadge = "SEC";

                if (!roleValueCache.TryGetValue(roleBadge, out string roleValue))
                {
                    roleValue = LookupRoleValue(catalog, priValue, roleBadge);
                    roleValueCache[roleBadge] = roleValue;
                }
                if (roleValue != null)
                    yield return (compKey, roleValue);
            }
        }

        // ── Role discovery (for card editor) ──────────────────────────────────

        /// <summary>
        /// Returns the distinct role badge strings for all roles in <paramref name="catalog"/>,
        /// with index suffixes when a role type appears more than once (e.g. "AUX", "SRT1", "SRT2").
        /// Single-occurrence roles omit the "1" suffix.
        /// </summary>
        public static IReadOnlyList<string> GetCatalogRoles(CatalogData catalog)
        {
            if (catalog == null) return Array.Empty<string>();

            // Count how many columns share each role type
            var counts = new Dictionary<ColumnRole, int>();
            foreach (var col in catalog.Columns)
                if (col.Role != ColumnRole.None)
                    counts[col.Role] = counts.TryGetValue(col.Role, out int c) ? c + 1 : 1;

            var seen  = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var col in catalog.Columns)
            {
                if (col.Role == ColumnRole.None) continue;
                string baseBadge = RoleBadge(col.Role);
                if (string.IsNullOrEmpty(baseBadge)) continue;
                // Include index suffix only when the role type has more than one column
                string badge = counts[col.Role] > 1
                    ? baseBadge + col.RoleIndex.ToString()
                    : baseBadge;
                if (seen.Add(badge))
                    order.Add(badge);
            }
            return order;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Reads consecutive Display_N_Role param values from a card until the first empty slot.
        /// Returns an empty list when no display columns are configured.
        /// </summary>
        private static IReadOnlyList<string> ReadDisplayRoles(CapabilityCard card)
        {
            var roles = new List<string>();
            for (int n = 0; n < MaxDisplayColumns; n++)
            {
                if (!card.Params.TryGetValue(DisplayRoleKey(n), out string role) || string.IsNullOrEmpty(role))
                    break;
                roles.Add(role);
            }
            return roles.Count > 0 ? roles : Array.Empty<string>();
        }

        /// <summary>
        /// Splits a badge string (e.g. "AUX2", "SRT1", "SEC") into its base role and numeric index.
        /// When no trailing digit is present, index defaults to 1.
        /// </summary>
        private static void ParseRoleBadge(string badge, out ColumnRole role, out int index)
        {
            index = 1;
            if (string.IsNullOrEmpty(badge)) { role = ColumnRole.None; return; }
            int i = badge.Length;
            while (i > 0 && char.IsDigit(badge[i - 1])) i--;
            string basePart = badge.Substring(0, i);
            if (i < badge.Length && int.TryParse(badge.Substring(i), out int parsed) && parsed >= 1)
                index = parsed;
            role = RoleFromBadge(basePart);
        }

        private static string GetRoleKey(CatalogData catalog, string roleBadge)
        {
            if (string.IsNullOrEmpty(roleBadge)) return null;
            ParseRoleBadge(roleBadge, out ColumnRole role, out int index);
            return role != ColumnRole.None ? GetColumnKey(catalog, role, index) : null;
        }

        private static string GetColumnKey(CatalogData catalog, ColumnRole role, int roleIndex)
            => catalog.Columns.FirstOrDefault(c => c.Role == role && c.RoleIndex == roleIndex)?.Key;

        private static string GetColumnLabel(CatalogData catalog, ColumnRole role, int roleIndex)
        {
            var col = catalog.Columns.FirstOrDefault(c => c.Role == role && c.RoleIndex == roleIndex);
            if (col == null) return null;
            return !string.IsNullOrEmpty(col.Label) ? col.Label : col.Key;
        }

        private static string GetRoleLabel(CatalogData catalog, string roleBadge)
        {
            if (string.IsNullOrEmpty(roleBadge)) return null;
            ParseRoleBadge(roleBadge, out ColumnRole role, out int index);
            return role != ColumnRole.None ? GetColumnLabel(catalog, role, index) : null;
        }

        /// <summary>
        /// Returns the column SPECS (label + index into <see cref="CatalogDropdownItem.AllDisplayValues"/>)
        /// for the multi-column Logic Dropdown popup of <paramref name="group"/>.
        /// Order: PRI (always present) → SEC → AUX → Display_N roles (each only if its role resolves to a real catalog column).
        /// </summary>
        public static IReadOnlyList<(string Label, int SourceIndex)> GetLogicDropdownColumnSpecs(
            CardGroup group, CatalogData catalog)
        {
            if (catalog == null) return Array.Empty<(string, int)>();

            string secRoleBadge     = "SEC";
            string tooltipRoleBadge = "AUX";
            IReadOnlyList<string> extraRoles = null;
            if (group != null)
            {
                foreach (var card in group.Cards)
                {
                    if (!card.Enabled) continue;
                    if (card.Type != CardTypeDropdown && card.Type != CardTypeSearch &&
                        card.Type != CardTypeButton   && card.Type != CardTypeMultiPick) continue;
                    if (card.Params.TryGetValue(ParamSecRole, out string s) && !string.IsNullOrEmpty(s))
                        secRoleBadge = s;
                    if (card.Params.TryGetValue(ParamTooltipRole, out string t) && !string.IsNullOrEmpty(t))
                        tooltipRoleBadge = t;
                    extraRoles = ReadDisplayRoles(card);
                    break;
                }
            }

            var specs = new List<(string Label, int SourceIndex)>();
            string priLabel = GetColumnLabel(catalog, ColumnRole.PrimaryDisplay, 1);
            if (priLabel == null) return Array.Empty<(string, int)>();
            specs.Add((priLabel, 0));

            string secLabel = GetRoleLabel(catalog, secRoleBadge);
            if (!string.IsNullOrEmpty(secLabel)) specs.Add((secLabel, 1));

            string auxLabel = GetRoleLabel(catalog, tooltipRoleBadge);
            if (!string.IsNullOrEmpty(auxLabel)) specs.Add((auxLabel, 2));

            if (extraRoles != null)
            {
                int extraIdx = 3;
                foreach (var r in extraRoles)
                {
                    string l = GetRoleLabel(catalog, r);
                    if (!string.IsNullOrEmpty(l)) specs.Add((l, extraIdx));
                    extraIdx++;
                }
            }

            return specs;
        }

        // ── Multi-token autocomplete ──────────────────────────────────────────

        /// <summary>
        /// Returns the separator and catalog lookup-column key for per-token autocomplete.
        /// For MultiPick groups the lookup column is PRI; for PairTransform groups it is the LookupRole column.
        /// Returns (null, null) when no relevant card is found.
        /// </summary>
        public static (string Separator, string LookupColKey, string SecColKey)
            GetMultiTokenAutoCompleteConfig(CardGroup group, CatalogData catalog)
        {
            if (group == null || catalog == null) return (null, null, null);
            foreach (var card in group.Cards)
            {
                if (!card.Enabled) continue;
                if (card.Type == CardTypeMultiPick)
                {
                    card.Params.TryGetValue(ParamPrimaryTokenSeparator, out string sep);
                    string priKey = GetColumnKey(catalog, ColumnRole.PrimaryDisplay,   1);
                    string secKey = GetColumnKey(catalog, ColumnRole.SecondaryDisplay, 1);
                    return (string.IsNullOrEmpty(sep) ? "-" : sep, priKey, secKey);
                }
                if (card.Type == CardTypePairTransform)
                {
                    card.Params.TryGetValue(ParamSourceTokenSeparator, out string sep);
                    card.Params.TryGetValue(ParamLookupRole,           out string lrBadge);
                    card.Params.TryGetValue(ParamOutputRole,           out string orBadge);
                    string lKey = GetRoleKey(catalog, string.IsNullOrEmpty(lrBadge) ? "PRI" : lrBadge);
                    string sKey = GetRoleKey(catalog, string.IsNullOrEmpty(orBadge) ? "SEC" : orBadge);
                    return (string.IsNullOrEmpty(sep) ? "-" : sep, lKey, sKey);
                }
                if (card.Type == CardTypeSort)
                {
                    card.Params.TryGetValue(ParamSortTokenSeparator, out string sep);
                    card.Params.TryGetValue(ParamSortLookupRole,     out string lrBadge);
                    string lKey = GetRoleKey(catalog, string.IsNullOrEmpty(lrBadge) ? "PRI" : lrBadge);
                    return (string.IsNullOrEmpty(sep) ? "-" : sep, lKey, null);
                }
            }
            return (null, null, null);
        }
    }
}
