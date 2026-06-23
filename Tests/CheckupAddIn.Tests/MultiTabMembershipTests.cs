using System.Collections.Generic;
using System.Linq;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Task #40 — multi-tab catalog membership. A value row's TAB-role cell may list several tabs
    /// (comma-separated); the picker shows the row under each. A no-comma cell is unchanged (opt-in,
    /// backward-compatible). Pure logic only — no Inventor/COM.
    /// </summary>
    public class MultiTabMembershipTests
    {
        private static CatalogData Cat(params (string pri, string tab, string tst)[] rows)
        {
            var c = new CatalogData { Id = "t40", Name = "T40" };
            c.Columns.Add(new CatalogColumn { Key = "pri", Role = ColumnRole.PrimaryDisplay, RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "tab", Role = ColumnRole.TabId,          RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "tst", Role = ColumnRole.TabSortKey,     RoleIndex = 1 });
            foreach (var (pri, tab, tst) in rows)
                c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = pri, ["tab"] = tab, ["tst"] = tst } });
            return c;
        }

        // ── SplitTabIds (the parsing primitive) ───────────────────────────────────

        [Fact]
        public void SplitTabIds_SingleValue_NoComma_YieldsOne()
        {
            var s = CatalogDropdownItem.SplitTabIds("Alpha");
            Assert.Single(s);
            Assert.Contains("Alpha", s);
        }

        [Fact]
        public void SplitTabIds_CommaList_TrimsAndDedupesCaseInsensitive()
        {
            var s = CatalogDropdownItem.SplitTabIds(" Alpha , Beta,alpha ,  ");
            Assert.Equal(2, s.Count);            // "alpha" collapses into "Alpha"; blank token dropped
            Assert.Contains("Alpha", s);
            Assert.Contains("Beta", s);
        }

        [Fact]
        public void SplitTabIds_EmptyOrNull_YieldsEmpty()
        {
            Assert.Empty(CatalogDropdownItem.SplitTabIds(""));
            Assert.Empty(CatalogDropdownItem.SplitTabIds(null));
        }

        // ── Item membership ───────────────────────────────────────────────────────

        [Fact]
        public void Item_IsInTab_MultiMembership_CaseInsensitive()
        {
            var item = new CatalogDropdownItem("v1", "", "", "", "Alpha, Beta");
            Assert.True(item.IsInTab("Alpha"));
            Assert.True(item.IsInTab("beta"));   // case-insensitive
            Assert.False(item.IsInTab("Gamma"));
            Assert.False(item.IsInTab(""));      // "All" is handled by the caller, never a member
            Assert.Equal(2, item.TabIds.Count);
        }

        [Fact]
        public void Item_SingleTab_BackwardCompatible()
        {
            var item = new CatalogDropdownItem("v", "", "", "", "X");
            Assert.True(item.IsInTab("X"));
            Assert.False(item.IsInTab("Y"));
            Assert.Single(item.TabIds);
        }

        // ── Engine: tab strip ─────────────────────────────────────────────────────

        [Fact]
        public void GetPickerTabs_MultiTabCells_ExpandToDistinctTabs_OrderedByTst()
        {
            var cat = Cat(
                ("",   "Alpha",       "1"),   // definition rows declare order
                ("",   "Beta",        "2"),
                ("",   "Gamma",       "3"),
                ("v1", "Alpha, Beta", ""),
                ("v2", "Gamma",       ""),
                ("v3", "Alpha",       ""));

            var tabs = CardEngine.GetPickerTabs(cat).Select(t => t.TabId).ToList();

            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, tabs);
        }

        // ── Engine: item membership across the set ────────────────────────────────

        [Fact]
        public void GetDropdownItems_MultiTabRow_BelongsToEachTab_NotOthers_AndAppearsOnce()
        {
            var cat = Cat(
                ("",   "Alpha",       "1"),
                ("",   "Beta",        "2"),
                ("",   "Gamma",       "3"),
                ("v1", "Alpha, Beta", ""),
                ("v2", "Gamma",       ""),
                ("v3", "Alpha",       ""));

            var items = CardEngine.GetDropdownItems(cat);

            Assert.Equal(3, items.Count);                       // definition rows (empty PRI) skipped
            Assert.Single(items, i => i.PriValue == "v1");      // no row-duplication

            var v1 = items.First(i => i.PriValue == "v1");
            Assert.True(v1.IsInTab("Alpha"));
            Assert.True(v1.IsInTab("Beta"));
            Assert.False(v1.IsInTab("Gamma"));

            Assert.Equal(new[] { "v1", "v3" }, items.Where(i => i.IsInTab("Alpha")).Select(i => i.PriValue).OrderBy(x => x));
            Assert.Equal(new[] { "v1" },       items.Where(i => i.IsInTab("Beta")).Select(i => i.PriValue));
            Assert.Equal(new[] { "v2" },       items.Where(i => i.IsInTab("Gamma")).Select(i => i.PriValue));
        }

        // ── Backward compatibility: single-tab catalog behaves exactly as before ──

        [Fact]
        public void SingleTabCatalog_Unchanged_ExactMatchSemantics()
        {
            var cat = Cat(
                ("a", "X", ""),
                ("b", "Y", ""),
                ("c", "X", ""));

            var tabs = CardEngine.GetPickerTabs(cat).Select(t => t.TabId).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "X", "Y" }, tabs);

            var items = CardEngine.GetDropdownItems(cat);
            Assert.Equal(new[] { "a", "c" }, items.Where(i => i.IsInTab("X")).Select(i => i.PriValue).OrderBy(x => x));
            Assert.Single(items, i => i.IsInTab("Y"));
        }
    }
}
