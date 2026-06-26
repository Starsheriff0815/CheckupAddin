using System.Collections.Generic;
using System.Linq;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Task #41 — Compose card. Splits ONE packed value (no separator) into catalog codes by
    /// LONGEST-MATCH, looks each up, and frames the result (per-item prefixes, collapse-when-equal,
    /// empty-drop, unknown-token policy). Neutral colour-pair fixture incl. a 3-char code (`wht`) to
    /// prove mixed-length longest-match. Pure logic — no Inventor/COM.
    /// </summary>
    public class ComposeCardTests
    {
        private static CatalogData Cat(params (string pri, string sec)[] rows)
        {
            var c = new CatalogData { Id = "t41", Name = "T41" };
            c.Columns.Add(new CatalogColumn { Key = "pri", Role = ColumnRole.PrimaryDisplay,   RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "sec", Role = ColumnRole.SecondaryDisplay, RoleIndex = 1 });
            foreach (var (pri, sec) in rows)
                c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = pri, ["sec"] = sec } });
            return c;
        }

        // 2-char codes + one 3-char code (`wht`) + one empty-output code (`xx`)
        private static CatalogData Demo() => Cat(
            ("rd", "Red"), ("bl", "Blue"), ("gn", "Green"), ("wht", "White"), ("xx", ""));

        private static CardEngine.ComposeConfig Cfg(bool collapse = false) => new CardEngine.ComposeConfig
        {
            LookupRole        = "PRI",
            OutputRole        = "SEC",
            CompanionFieldKey = "comp",
            ItemPrefixes      = new[] { "Front ", "Back " },
            ItemSeparator     = " / ",
            CollapseWhenEqual = collapse,
            CollapsedPrefix   = "Both sides ",
            DropEmptyOutputs  = true,
            OnUnknownToken    = "skip",
        };

        // ── Core framing ──────────────────────────────────────────────────────────

        [Fact]
        public void Differing_PerItemPrefixes_AndSeparator()
            => Assert.Equal("Front Red / Back Blue", CardEngine.BuildComposeValue("rdbl", Demo(), Cfg()));

        [Fact]
        public void Equal_Collapses_WithCollapsedPrefix()
            => Assert.Equal("Both sides Red", CardEngine.BuildComposeValue("rdrd", Demo(), Cfg(collapse: true)));

        [Fact]
        public void Equal_NoCollapse_StillFramesPerItem()
            => Assert.Equal("Front Red / Back Red", CardEngine.BuildComposeValue("rdrd", Demo(), Cfg(collapse: false)));

        // ── The headline requirement: mixed-length longest-match ──────────────────

        [Fact]
        public void LongestMatch_ThreeCharCode_BeatsTwoChar()
            => Assert.Equal("Front White / Back Red", CardEngine.BuildComposeValue("whtrd", Demo(), Cfg()));

        [Fact]
        public void LongestMatch_ThreeCharOnBothSides()
            => Assert.Equal("Front White / Back White", CardEngine.BuildComposeValue("whtwht", Demo(), Cfg()));

        // ── Empty-output drop (the "neutral axis" case) ───────────────────────────

        [Fact]
        public void EmptyOutputCode_IsDropped_ByDefault()
            => Assert.Equal("Front Red", CardEngine.BuildComposeValue("rdxx", Demo(), Cfg()));

        [Fact]
        public void EmptyOutputCode_Kept_WhenDropDisabled()
        {
            var cfg = Cfg(); cfg.DropEmptyOutputs = false;
            Assert.Equal("Front Red / Back ", CardEngine.BuildComposeValue("rdxx", Demo(), cfg));
        }

        // ── Feature-style: no prefixes, comma separator, empty drops ──────────────

        [Fact]
        public void FeatureStyle_NoPrefixes_CommaJoin_EmptyDrops()
        {
            var cfg = new CardEngine.ComposeConfig
            { LookupRole = "PRI", OutputRole = "SEC", CompanionFieldKey = "c", ItemSeparator = ", ", DropEmptyOutputs = true };
            Assert.Equal("Red, Blue", CardEngine.BuildComposeValue("rdbl", Demo(), cfg));
            Assert.Equal("Red",       CardEngine.BuildComposeValue("rdxx", Demo(), cfg));   // empty axis drops
        }

        // ── Case-insensitive matching ─────────────────────────────────────────────

        [Fact]
        public void Matching_IsCaseInsensitive()
            => Assert.Equal("Front Red / Back Blue", CardEngine.BuildComposeValue("RDBL", Demo(), Cfg()));

        // ── MaxItems cap ──────────────────────────────────────────────────────────

        [Fact]
        public void MaxItems_CapsCodes_ThenSkipUsesParsed()
        {
            var cfg = Cfg(); cfg.MaxItems = 2;
            Assert.Equal("Front Red / Back Blue", CardEngine.BuildComposeValue("rdblgn", Demo(), cfg));
        }

        // ── Unknown / un-tokenisable input ────────────────────────────────────────

        [Fact]
        public void Unknown_Skip_FromParsedPrefix()
        {
            var cfg = Cfg();                       // "rdz": rd matches, trailing z does not
            Assert.Equal("Front Red", CardEngine.BuildComposeValue("rdz", Demo(), cfg));
        }

        [Fact]
        public void Unknown_NothingParsed_Skip_ReturnsEmpty()
            => Assert.Equal("", CardEngine.BuildComposeValue("zz", Demo(), Cfg()));

        [Fact]
        public void Unknown_KeepRaw_ReturnsSource()
        {
            var cfg = Cfg(); cfg.OnUnknownToken = "keepRaw";
            Assert.Equal("zz", CardEngine.BuildComposeValue("zz", Demo(), cfg));
        }

        [Fact]
        public void Unknown_Passthrough_ReturnsNull()
        {
            var cfg = Cfg(); cfg.OnUnknownToken = "passthrough";
            Assert.Null(CardEngine.BuildComposeValue("zz", Demo(), cfg));
        }

        // ── Empty / null source ───────────────────────────────────────────────────

        [Fact]
        public void EmptySource_ReturnsEmpty()
        {
            Assert.Equal("", CardEngine.BuildComposeValue("",   Demo(), Cfg()));
            Assert.Equal("", CardEngine.BuildComposeValue(null, Demo(), Cfg()));
        }

        // ── Bounded to the configured catalog/column (no cross-column match) ──────

        [Fact]
        public void OnlyLookupRoleColumn_IsMatched()
        {
            // "Red" lives in SEC, not PRI → it must NOT tokenise as a code.
            Assert.Equal("", CardEngine.BuildComposeValue("Red", Demo(), Cfg()));
        }

        // ── GetComposeWrites integration (param-driven, card order) ───────────────

        [Fact]
        public void GetComposeWrites_WritesFramedCompanion()
        {
            var cat = Demo();
            var group = new CardGroup { Id = "g", TargetFieldKey = "short" };
            var card = new CapabilityCard { Type = CardEngine.CardTypeCompose, Enabled = true, CatalogId = cat.Id };
            card.Params[CardEngine.ParamLookupRole]               = "PRI";
            card.Params[CardEngine.ParamOutputRole]               = "SEC";
            card.Params[CardEngine.ParamCompanionFieldKey]        = "long";
            card.Params[CardEngine.ParamComposeItemPrefixes]      = "Front ,Back ";  // spaces preserved across the comma split
            card.Params[CardEngine.ParamComposeItemSeparator]     = " / ";
            card.Params[CardEngine.ParamComposeCollapseWhenEqual] = "true";
            card.Params[CardEngine.ParamComposeCollapsedPrefix]   = "Both sides ";
            group.Cards.Add(card);

            var writes = CardEngine.GetComposeWrites(group, cat, "whtrd").ToList();
            Assert.Single(writes);
            Assert.Equal("long", writes[0].FieldKey);
            Assert.Equal("Front White / Back Red", writes[0].Value);

            // collapse path via the same configured card
            Assert.Equal("Both sides Red", CardEngine.GetComposeWrites(group, cat, "rdrd").Single().Value);
        }

        [Fact]
        public void GetComposeWrites_Passthrough_SkipsWrite()
        {
            var cat = Demo();
            var group = new CardGroup { Id = "g", TargetFieldKey = "short" };
            var card = new CapabilityCard { Type = CardEngine.CardTypeCompose, Enabled = true, CatalogId = cat.Id };
            card.Params[CardEngine.ParamCompanionFieldKey]      = "long";
            card.Params[CardEngine.ParamComposeOnUnknownToken]  = "passthrough";
            group.Cards.Add(card);

            Assert.Empty(CardEngine.GetComposeWrites(group, cat, "zz"));   // un-tokenisable → companion left untouched
        }

        [Fact]
        public void HasComposeCard_DetectsEnabledOnly()
        {
            var g = new CardGroup();
            g.Cards.Add(new CapabilityCard { Type = CardEngine.CardTypeCompose, Enabled = false });
            Assert.False(CardEngine.HasComposeCard(g));
            g.Cards.Add(new CapabilityCard { Type = CardEngine.CardTypeCompose, Enabled = true });
            Assert.True(CardEngine.HasComposeCard(g));
        }
    }

    /// <summary>
    /// Task #42 — Compose Split Mode. Extends the Compose card to handle separator-delimited source
    /// fields (e.g. SPEZIFIK1 "60-pur-g2g2-fngg") where some tokens are direct catalog matches and
    /// others are packed sub-codes requiring sub-tokenization. Tests cover BuildComposeSplitValue and
    /// GetComposeWritesEx. Pure logic — no Inventor/COM.
    /// </summary>
    public class ComposeSplitModeTests
    {
        // ── Catalog helpers ───────────────────────────────────────────────────────

        private static CatalogData MakeCat(string id, params (string pri, string sec)[] rows)
        {
            var c = new CatalogData { Id = id, Name = id };
            c.Columns.Add(new CatalogColumn { Key = "pri", Role = ColumnRole.PrimaryDisplay,   RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "sec", Role = ColumnRole.SecondaryDisplay, RoleIndex = 1 });
            foreach (var (pri, sec) in rows)
                c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = pri, ["sec"] = sec } });
            return c;
        }

        // Primary catalog: direct-match tokens (thickness, füllung, suffix)
        private static CatalogData Primary() => MakeCat("primary",
            ("60",  "ISO 60"),
            ("100", "ISO 100"),
            ("pur", "PUR-Schaum"),
            ("ge",  "gelocht"));

        // Material-only sub-catalog: 2-char material codes for sub-tokenization
        private static CatalogData MatCat() => MakeCat("mat",
            ("g2",  "Edelstahl 0,8 Korn 320"),
            ("a1",  "PVC-w 0,6 RAL9010"),
            ("l5",  "Edelstahl 2,0 rutschh. R11"),
            ("hlz", "Druckverteilerplatte"));      // 3-char

        // Feature-combinations catalog: pre-computed 4-char stacks as direct entries
        private static CatalogData FeatCat() => MakeCat("feat",
            ("fnnn", "Feder links"),
            ("fngg", "Feder links, stirnseitig glatt"),
            ("ggnn", "beidseitig glatt"),
            ("nnnn", ""));                          // both axes empty → SEC is ""

        // D1/D2 config reused for material expand tests
        private static CardEngine.ComposeConfig D1D2Cfg(string srcSep = "-", string onDirect = "skip") =>
            new CardEngine.ComposeConfig
            {
                LookupRole         = "PRI",
                OutputRole         = "SEC",
                CompanionFieldKey  = "long",
                SourceSeparator    = srcSep,
                TokenOutputSeparator = ", ",
                OnDirectMatch      = onDirect,
                OutputMode         = "replace",
                AppendSeparator    = ", ",
                ItemPrefixes       = new[] { "D1 ", "D2 " },
                ItemSeparator      = " / ",
                CollapseWhenEqual  = true,
                CollapsedPrefix    = "D1/D2 ",
                DropEmptyOutputs   = true,
                OnUnknownToken     = "skip",
            };

        // Simple comma-join config (for feature expand / include-direct tests)
        private static CardEngine.ComposeConfig CommaCfg(string srcSep = "-", string onDirect = "include") =>
            new CardEngine.ComposeConfig
            {
                LookupRole         = "PRI",
                OutputRole         = "SEC",
                CompanionFieldKey  = "long",
                SourceSeparator    = srcSep,
                TokenOutputSeparator = ", ",
                OnDirectMatch      = onDirect,
                OutputMode         = "replace",
                AppendSeparator    = ", ",
                ItemSeparator      = ", ",
                DropEmptyOutputs   = true,
                OnUnknownToken     = "skip",
            };

        // ── OnDirectMatch = include/skip ──────────────────────────────────────────

        [Fact]
        public void DirectMatch_Include_AddsSecToOutput()
        {
            // "60-pur" — both are direct matches in primary; include mode → both appear
            string r = CardEngine.BuildComposeSplitValue("60-pur", Primary(), Primary(), CommaCfg(onDirect: "include"));
            Assert.Equal("ISO 60, PUR-Schaum", r);
        }

        [Fact]
        public void DirectMatch_Skip_OmitsDirectTokens()
        {
            // "60-pur" — both are direct matches; skip mode → nothing in output
            string r = CardEngine.BuildComposeSplitValue("60-pur", Primary(), Primary(), CommaCfg(onDirect: "skip"));
            Assert.Equal("", r);
        }

        [Fact]
        public void DirectMatch_Skip_IPT_AllDirect_EmptyOutput()
        {
            // IPT: "100-pur-ge" — all direct matches; OnDirectMatch=skip → empty (Compose cards produce nothing; PairTransform already wrote them)
            string r = CardEngine.BuildComposeSplitValue("100-pur-ge", Primary(), Primary(), CommaCfg(onDirect: "skip"));
            Assert.Equal("", r);
        }

        // ── Sub-tokenization on miss ──────────────────────────────────────────────

        [Fact]
        public void NoDirectMatch_SubTokenizes_SameMaterialDouble()
        {
            // "g2g2" not in primary; sub-tokenizes in MatCat → g2+g2 → collapse → "D1/D2 Edelstahl..."
            string r = CardEngine.BuildComposeSplitValue("g2g2", Primary(), MatCat(), D1D2Cfg(srcSep: "-"));
            Assert.Equal("D1/D2 Edelstahl 0,8 Korn 320", r);
        }

        [Fact]
        public void NoDirectMatch_SubTokenizes_MixedMaterialDouble()
        {
            // "l5a1" not in primary; sub-tokenizes → l5+a1 → different → D1/D2 framing
            string r = CardEngine.BuildComposeSplitValue("l5a1", Primary(), MatCat(), D1D2Cfg(srcSep: "-"));
            Assert.Equal("D1 Edelstahl 2,0 rutschh. R11 / D2 PVC-w 0,6 RAL9010", r);
        }

        [Fact]
        public void NoDirectMatch_SubTokenizes_ThreeCharPlusTwoChar()
        {
            // "hlzg2" not in primary; longest-match in MatCat → hlz(3-char)+g2(2-char)
            string r = CardEngine.BuildComposeSplitValue("hlzg2", Primary(), MatCat(), D1D2Cfg(srcSep: "-"));
            Assert.Equal("D1 Druckverteilerplatte / D2 Edelstahl 0,8 Korn 320", r);
        }

        [Fact]
        public void NoDirectMatch_SubTokenizes_FeatureCombo_DirectHitInFallback()
        {
            // "fngg" not in primary; in FeatCat it IS a 4-char direct PRI entry → correct O-U semantics
            string r = CardEngine.BuildComposeSplitValue("fngg", Primary(), FeatCat(), CommaCfg(onDirect: "skip"));
            Assert.Equal("Feder links, stirnseitig glatt", r);
        }

        [Fact]
        public void NoDirectMatch_FeatureCombo_BothAxesEmpty_ProducesNothing()
        {
            // "nnnn" → SEC="" in FeatCat; DropEmptyOutputs=true → dropped → empty
            string r = CardEngine.BuildComposeSplitValue("nnnn", Primary(), FeatCat(), CommaCfg(onDirect: "skip"));
            Assert.Equal("", r);
        }

        [Fact]
        public void NoDirectMatch_FeatureCombo_SingleAxis_SecondEmpty()
        {
            // "fnnn" → SEC="Feder links" in FeatCat (nn axis drops)
            string r = CardEngine.BuildComposeSplitValue("fnnn", Primary(), FeatCat(), CommaCfg(onDirect: "skip"));
            Assert.Equal("Feder links", r);
        }

        // ── Combined (direct matches + sub-expanded tokens in one pass) ───────────

        [Fact]
        public void Combined_DirectAndSubExpanded_Skip()
        {
            // "60-pur-g2g2" — 60+pur are direct (skipped); g2g2 sub-expands
            var cfg = D1D2Cfg(onDirect: "skip");
            string r = CardEngine.BuildComposeSplitValue("60-pur-g2g2", Primary(), MatCat(), cfg);
            Assert.Equal("D1/D2 Edelstahl 0,8 Korn 320", r);
        }

        [Fact]
        public void Combined_DirectAndSubExpanded_Include()
        {
            // "60-pur-g2g2" — 60+pur are direct (included); g2g2 sub-expands (D1/D2 collapse)
            var cfg = new CardEngine.ComposeConfig
            {
                LookupRole = "PRI", OutputRole = "SEC", CompanionFieldKey = "long",
                SourceSeparator = "-", TokenOutputSeparator = ", ", OnDirectMatch = "include",
                ItemPrefixes = new[] { "D1 ", "D2 " }, ItemSeparator = " / ",
                CollapseWhenEqual = true, CollapsedPrefix = "D1/D2 ",
                DropEmptyOutputs = true, OnUnknownToken = "skip",
            };
            string r = CardEngine.BuildComposeSplitValue("60-pur-g2g2", Primary(), MatCat(), cfg);
            Assert.Equal("ISO 60, PUR-Schaum, D1/D2 Edelstahl 0,8 Korn 320", r);
        }

        // ── Unknown outer token (no direct match AND sub-tokenization fails) ──────

        [Fact]
        public void UnknownOuterToken_Skip_IsOmitted()
        {
            // "xyz" not in primary, not sub-tokenisable in MatCat → skip
            string r = CardEngine.BuildComposeSplitValue("xyz", Primary(), MatCat(), D1D2Cfg(srcSep: "-"));
            Assert.Equal("", r);
        }

        [Fact]
        public void UnknownOuterToken_DoesNotPolluteMixedResult()
        {
            // "g2g2-xyz" → g2g2 expands, xyz is dropped
            string r = CardEngine.BuildComposeSplitValue("g2g2-xyz", Primary(), MatCat(), D1D2Cfg(onDirect: "skip"));
            Assert.Equal("D1/D2 Edelstahl 0,8 Korn 320", r);
        }

        // ── TokenOutputSeparator ──────────────────────────────────────────────────

        [Fact]
        public void TokenOutputSeparator_JoinsMultipleExpandedTokens()
        {
            var cfg = CommaCfg(onDirect: "include"); cfg.TokenOutputSeparator = " | ";
            string r = CardEngine.BuildComposeSplitValue("60-pur", Primary(), Primary(), cfg);
            Assert.Equal("ISO 60 | PUR-Schaum", r);
        }

        // ── GetComposeWritesEx ────────────────────────────────────────────────────

        private static CardGroup MakeGroup(CardEngine.ComposeConfig cfg)
        {
            var g = new CardGroup { Id = "g", TargetFieldKey = "short" };
            var card = new CapabilityCard { Type = CardEngine.CardTypeCompose, Enabled = true, CatalogId = "primary" };
            card.Params[CardEngine.ParamLookupRole]                  = cfg.LookupRole;
            card.Params[CardEngine.ParamOutputRole]                  = cfg.OutputRole;
            card.Params[CardEngine.ParamCompanionFieldKey]           = cfg.CompanionFieldKey;
            card.Params[CardEngine.ParamComposeItemSeparator]        = cfg.ItemSeparator;
            card.Params[CardEngine.ParamComposeDropEmptyOutputs]     = cfg.DropEmptyOutputs ? "true" : "false";
            card.Params[CardEngine.ParamComposeOnUnknownToken]       = cfg.OnUnknownToken;
            if (!string.IsNullOrEmpty(cfg.SourceSeparator))
                card.Params[CardEngine.ParamComposeSourceSeparator]  = cfg.SourceSeparator;
            if (!string.IsNullOrEmpty(cfg.FallbackCatalogId))
                card.Params[CardEngine.ParamComposeFallbackCatalogId]= cfg.FallbackCatalogId;
            card.Params[CardEngine.ParamComposeOnDirectMatch]        = cfg.OnDirectMatch;
            card.Params[CardEngine.ParamComposeOutputMode]           = cfg.OutputMode;
            card.Params[CardEngine.ParamComposeAppendSeparator]      = cfg.AppendSeparator;
            if (cfg.CollapseWhenEqual)
            {
                card.Params[CardEngine.ParamComposeCollapseWhenEqual]= "true";
                card.Params[CardEngine.ParamComposeCollapsedPrefix]  = cfg.CollapsedPrefix;
            }
            if (cfg.ItemPrefixes != null && cfg.ItemPrefixes.Count > 0)
                card.Params[CardEngine.ParamComposeItemPrefixes]     = string.Join(",", cfg.ItemPrefixes);
            g.Cards.Add(card);
            return g;
        }

        [Fact]
        public void WriteEx_BaseMode_Replace_IsAppendFalse()
        {
            var cfg = CommaCfg(srcSep: "");     // no SourceSeparator → base mode
            cfg.CompanionFieldKey = "long";
            // For base mode GetComposeWritesEx falls back to BuildComposeValue
            // "rdbl" against primary has no PRI "rd"/"bl", so result is empty; use Demo instead via primary override
            var demoCat = MakeCat("primary", ("rd", "Red"), ("bl", "Blue"));
            var writes = CardEngine.GetComposeWritesEx(MakeGroup(cfg), demoCat, "rdbl").ToList();
            Assert.Single(writes);
            Assert.Equal("Red, Blue", writes[0].Value);
            Assert.False(writes[0].IsAppend);
        }

        [Fact]
        public void WriteEx_SplitMode_Replace_IsAppendFalse()
        {
            var cfg = D1D2Cfg(onDirect: "skip"); cfg.OutputMode = "replace";
            string result = CardEngine.BuildComposeSplitValue("g2g2", Primary(), MatCat(), cfg);
            Assert.Equal("D1/D2 Edelstahl 0,8 Korn 320", result);

            var group = MakeGroup(cfg);
            var writes = CardEngine.GetComposeWritesEx(group, Primary(), "g2g2",
                id => id == "mat" ? MatCat() : null).ToList();
            // cfg.FallbackCatalogId is "" here → falls back to primary, which has no g2 → sub-tokenize in primary → fails
            // Use explicit FallbackCatalogId to hit the correct catalog:
            var cfgWithFb = D1D2Cfg(onDirect: "skip");
            cfgWithFb.FallbackCatalogId = "mat";
            cfgWithFb.OutputMode = "replace";
            var g2 = MakeGroup(cfgWithFb);
            var w2 = CardEngine.GetComposeWritesEx(g2, Primary(), "g2g2",
                id => id == "mat" ? MatCat() : null).ToList();
            Assert.Single(w2);
            Assert.Equal("D1/D2 Edelstahl 0,8 Korn 320", w2[0].Value);
            Assert.False(w2[0].IsAppend);
        }

        [Fact]
        public void WriteEx_SplitMode_Append_IsAppendTrue_CorrectSeparator()
        {
            var cfg = D1D2Cfg(onDirect: "skip");
            cfg.FallbackCatalogId = "mat";
            cfg.OutputMode    = "append";
            cfg.AppendSeparator = ", ";
            var g = MakeGroup(cfg);
            var writes = CardEngine.GetComposeWritesEx(g, Primary(), "g2g2",
                id => id == "mat" ? MatCat() : null).ToList();
            Assert.Single(writes);
            Assert.True(writes[0].IsAppend);
            Assert.Equal(", ", writes[0].AppendSeparator);
            Assert.Equal("D1/D2 Edelstahl 0,8 Korn 320", writes[0].Value);
        }

        [Fact]
        public void WriteEx_AppendEmptyResult_IsSkipped()
        {
            // "nnnn" → FeatCat → SEC="" → DropEmptyOutputs=true → result="" → no write
            var cfg = CommaCfg(onDirect: "skip");
            cfg.FallbackCatalogId = "feat";
            cfg.OutputMode = "append";
            var g = MakeGroup(cfg);
            var writes = CardEngine.GetComposeWritesEx(g, Primary(), "nnnn",
                id => id == "feat" ? FeatCat() : null).ToList();
            Assert.Empty(writes);
        }

        [Fact]
        public void WriteEx_FallbackResolver_UsedForSubTokenization()
        {
            // Feature combo "fngg" is NOT in primary catalog but IS a direct entry in FeatCat
            var cfg = CommaCfg(onDirect: "skip");
            cfg.FallbackCatalogId = "feat";
            cfg.OutputMode = "replace";
            var g = MakeGroup(cfg);
            var writes = CardEngine.GetComposeWritesEx(g, Primary(), "fngg",
                id => id == "feat" ? FeatCat() : null).ToList();
            Assert.Single(writes);
            Assert.Equal("Feder links, stirnseitig glatt", writes[0].Value);
        }
    }
}
