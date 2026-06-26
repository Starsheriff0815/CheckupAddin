using System.Collections.Generic;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Task #43 — Generation scope filter. A Generation-roled (GEN) cell tags a catalog row to one
    /// generation; a BLANK cell is universal. Lookups (PairTransform / Compose) skip rows whose generation
    /// differs from the active scope. With no active generation — or no GEN column — the filter is inert,
    /// so the existing suite is unaffected. Pure logic — no Inventor/COM.
    ///
    /// Fixture models the real collision: `fnnn` = "umlaufend C-Kante" on a Blech part, "Feder links" on a
    /// foam Paneel; `60` is universal (blank generation).
    /// </summary>
    public class GenerationScopeTests
    {
        private static CatalogData Cat(params (string pri, string sec, string gen)[] rows)
        {
            var c = new CatalogData { Id = "t43", Name = "T43" };
            c.Columns.Add(new CatalogColumn { Key = "pri", Role = ColumnRole.PrimaryDisplay,   RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "sec", Role = ColumnRole.SecondaryDisplay, RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "gen", Role = ColumnRole.Generation,       RoleIndex = 1 });
            foreach (var (pri, sec, gen) in rows)
                c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = pri, ["sec"] = sec, ["gen"] = gen } });
            return c;
        }

        private static CatalogData Collision() => Cat(
            ("60",   "ISO 60",                  ""),        // universal (blank)
            ("fnnn", "umlaufend C-Kante 17/10", "Blech"),   // Blech-only reading
            ("fnnn", "Feder links",             "Paneel")); // Paneel-only reading

        private static string PT(CatalogData c, string src, string gen)
            => CardEngine.BuildPairTransformValue(src, c, "-", "PRI", "SEC", ", ", gen);

        private static CardEngine.ComposeConfig ComposeCfg() => new CardEngine.ComposeConfig
        { LookupRole = "PRI", OutputRole = "SEC", ItemSeparator = ", ", DropEmptyOutputs = true };

        // ── PairTransform direct lookup ──────────────────────────────────────────

        [Fact]
        public void BlankGeneration_IsUniversal()
        {
            Assert.Equal("ISO 60", PT(Collision(), "60", "Paneel"));
            Assert.Equal("ISO 60", PT(Collision(), "60", "Blech"));
        }

        [Fact]
        public void TaggedRow_MatchedUnderItsOwnGeneration()
            => Assert.Equal("umlaufend C-Kante 17/10", PT(Collision(), "fnnn", "Blech"));

        [Fact]
        public void TaggedRow_Skipped_OtherGenerationRowWins()
            => Assert.Equal("Feder links", PT(Collision(), "fnnn", "Paneel"));

        [Fact]
        public void NoActiveGeneration_FilterInert_FirstMatchWins()
            => Assert.Equal("umlaufend C-Kante 17/10", PT(Collision(), "fnnn", null));

        [Fact]
        public void NoGenerationColumn_FilterInert()
        {
            var c = new CatalogData { Id = "p", Name = "P" };
            c.Columns.Add(new CatalogColumn { Key = "pri", Role = ColumnRole.PrimaryDisplay,   RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "sec", Role = ColumnRole.SecondaryDisplay, RoleIndex = 1 });
            c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = "60", ["sec"] = "ISO 60" } });
            Assert.Equal("ISO 60", CardEngine.BuildPairTransformValue("60", c, "-", "PRI", "SEC", ", ", "Paneel"));
        }

        // ── Compose longest-match lookup ─────────────────────────────────────────

        [Fact]
        public void Compose_RespectsGenerationScope()
        {
            Assert.Equal("Feder links",             CardEngine.BuildComposeValue("fnnn", Collision(), ComposeCfg(), "Paneel"));
            Assert.Equal("umlaufend C-Kante 17/10", CardEngine.BuildComposeValue("fnnn", Collision(), ComposeCfg(), "Blech"));
        }
    }
}
