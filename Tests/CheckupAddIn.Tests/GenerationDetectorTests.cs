using System.Collections.Generic;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Task #43 — generation detector. Derives the active generation from a typed short: any token belonging
    /// to a configured signal category (the foam/Füllung category in the real data) ⇒ the "present"
    /// generation (Paneel), else the "absent" one (Blech). The signal category is looked up in the catalog
    /// (data-driven) — NOT a hard-coded token list. Pure logic — no Inventor/COM.
    /// (ASCII category names here keep the test encoding-clean; real wiring uses Füllung/Schaum.)
    /// </summary>
    public class GenerationDetectorTests
    {
        private static CatalogData Cat()
        {
            var c = new CatalogData { Id = "t43d", Name = "T43D" };
            c.Columns.Add(new CatalogColumn { Key = "pri", Label = "pri", Role = ColumnRole.PrimaryDisplay, RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "cat", Label = "cat", Role = ColumnRole.None,           RoleIndex = 1 });
            void Add(string pri, string cat) => c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = pri, ["cat"] = cat } });
            // Category cells use the real "CategoryNNN" convention → exercises starts-with matching.
            Add("60", "Foam001"); Add("pur", "Foam002"); Add("g2", "Material033"); Add("fnnn", "Edge001");
            return c;
        }

        private static string Detect(string src)
            => CardEngine.DetectGeneration(src, Cat(), "-", "cat", "Foam", "Paneel", "Blech");

        [Fact] public void FoamTokenPresent_IsPaneel()  => Assert.Equal("Paneel", Detect("60-pur-g2g2-fnnn"));
        [Fact] public void SchaumTokenOnly_IsPaneel()   => Assert.Equal("Paneel", Detect("60-g2"));
        [Fact] public void FuellungTokenOnly_IsPaneel() => Assert.Equal("Paneel", Detect("pur-g2"));
        [Fact] public void NoFoam_IsBlech()             => Assert.Equal("Blech",  Detect("g2-fnnn"));
        [Fact] public void EmptyShort_IsBlech()         => Assert.Equal("Blech",  Detect(""));
        [Fact] public void UnknownTokensOnly_IsBlech()  => Assert.Equal("Blech",  Detect("xx-yy"));
    }
}
