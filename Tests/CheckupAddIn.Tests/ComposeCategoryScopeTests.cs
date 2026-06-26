using System.Collections.Generic;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Task #43 — category scope on the Compose fallback. Instead of a dedicated material sub-catalog, the
    /// Compose card sub-tokenizes against the shared catalog restricted to a named category. This reproduces
    /// the sub-catalog's collision avoidance (a longer Merkmal code that shares a prefix is excluded) and lets
    /// us delete the separate file (two-file reduction). Pure logic — no Inventor/COM.
    /// </summary>
    public class ComposeCategoryScopeTests
    {
        // `l5a` is a longer Merkmal code that would hijack longest-match of `l5a1` if not category-scoped.
        private static CatalogData Cat()
        {
            var c = new CatalogData { Id = "t43cat", Name = "T43CAT" };
            c.Columns.Add(new CatalogColumn { Key = "pri", Label = "pri", Role = ColumnRole.PrimaryDisplay,   RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "sec", Label = "sec", Role = ColumnRole.SecondaryDisplay, RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "cat", Label = "cat", Role = ColumnRole.None,             RoleIndex = 1 });
            void Add(string p, string s, string cat) => c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = p, ["sec"] = s, ["cat"] = cat } });
            // Category cells use the real "CategoryNNN" convention → exercises starts-with matching.
            Add("l5",  "Edelstahl", "Material051");
            Add("a1",  "PVC-w",     "Material001");
            Add("l5a", "C-Profil",  "ILKAsys_Blech_Merkmale005");   // longer, different category — the collision
            return c;
        }

        private static CardEngine.ComposeConfig Cfg(string category) => new CardEngine.ComposeConfig
        {
            LookupRole = "PRI", OutputRole = "SEC", ItemSeparator = ", ", DropEmptyOutputs = true,
            FallbackCategoryColumn = "cat", FallbackCategory = category,
        };

        [Fact]
        public void CategoryScope_RestrictsSubTokenization_ToMaterial()
            => Assert.Equal("Edelstahl, PVC-w", CardEngine.BuildComposeValue("l5a1", Cat(), Cfg("Material")));

        [Fact]
        public void WithoutCategory_LongerMerkmalCodeHijacksLongestMatch()
        {
            // No category filter → `l5a` (3-char Merkmal) wins longest-match over `l5`+`a1`. This is exactly
            // the collision the category scope (or the old material sub-catalog) prevents.
            var cfg = new CardEngine.ComposeConfig { LookupRole = "PRI", OutputRole = "SEC", ItemSeparator = ", ", DropEmptyOutputs = true };
            Assert.Equal("C-Profil", CardEngine.BuildComposeValue("l5a1", Cat(), cfg));
        }
    }
}
