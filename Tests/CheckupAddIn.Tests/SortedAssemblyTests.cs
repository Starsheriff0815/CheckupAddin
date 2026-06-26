using System.Collections.Generic;
using System.Linq;
using CheckupAddIn.Models;
using CheckupAddIn.Services;
using Xunit;
using OS = CheckupAddIn.Services.CardEngine.OrderedSegment;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Task #43 — sorted assembly. Every contributor emits OrderedSegments tagged with placing_order
    /// (GroupSortKey) + internal_order (SortKey); AssembleSorted orders by (placing, internal, production
    /// order), drops empties, and joins once. Replaces the append chain so a mis-typed token order still
    /// yields canonical output. Pure logic — no Inventor/COM.
    /// </summary>
    public class SortedAssemblyTests
    {
        // catalog with placing (GST) + internal (SRT) columns
        private static CatalogData Cat()
        {
            var c = new CatalogData { Id = "t43s", Name = "T43S" };
            c.Columns.Add(new CatalogColumn { Key = "pri", Role = ColumnRole.PrimaryDisplay,   RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "sec", Role = ColumnRole.SecondaryDisplay, RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "gst", Role = ColumnRole.GroupSortKey,      RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "srt", Role = ColumnRole.SortKey,           RoleIndex = 1 });
            void Add(string p, string s, string gst, string srt) => c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = p, ["sec"] = s, ["gst"] = gst, ["srt"] = srt } });
            Add("60",  "ISO 60",     "1", "0");
            Add("pur", "PUR-Schaum", "2", "0");
            Add("x",   "XX",         "5", "0");
            return c;
        }

        [Fact]
        public void AssembleSorted_OrdersByPlacing()
        {
            var segs = new List<OS> { new OS(5, 0, 0, "E"), new OS(1, 0, 1, "A"), new OS(3, 0, 2, "C") };
            Assert.Equal("A, C, E", CardEngine.AssembleSorted(segs, ", "));
        }

        [Fact]
        public void AssembleSorted_TieBrokenByInternalThenOrder()
        {
            var segs = new List<OS> { new OS(1, 2, 0, "second"), new OS(1, 1, 9, "first"), new OS(1, 2, 1, "third") };
            Assert.Equal("first, second, third", CardEngine.AssembleSorted(segs, ", "));
        }

        [Fact]
        public void AssembleSorted_DropsEmptyText()
        {
            var segs = new List<OS> { new OS(1, 0, 0, "A"), new OS(2, 0, 1, ""), new OS(3, 0, 2, "C") };
            Assert.Equal("A, C", CardEngine.AssembleSorted(segs, ", "));
        }

        [Fact]
        public void PairTransformSegments_SortMisorderedInput()
        {
            // typed out of order (x first) → canonical placing order out
            var segs = CardEngine.BuildPairTransformSegments("x-60-pur", Cat(), "-", "PRI", "SEC");
            Assert.Equal("ISO 60, PUR-Schaum, XX", CardEngine.AssembleSorted(segs, ", "));
        }

        [Fact]
        public void MixedSources_ComposeSegmentInterleavesByPlacing()
        {
            var segs = new List<OS>(CardEngine.BuildPairTransformSegments("x-60-pur", Cat(), "-", "PRI", "SEC"));
            segs.Add(new OS(3, 0, 100, "MAT"));   // a Compose card's output at placing 3
            Assert.Equal("ISO 60, PUR-Schaum, MAT, XX", CardEngine.AssembleSorted(segs, ", "));
        }

        [Fact]
        public void GetComposeSegments_YieldsSegmentAtCardOutputPlacing()
        {
            var cat = new CatalogData { Id = "g", Name = "G" };
            cat.Columns.Add(new CatalogColumn { Key = "pri", Role = ColumnRole.PrimaryDisplay,   RoleIndex = 1 });
            cat.Columns.Add(new CatalogColumn { Key = "sec", Role = ColumnRole.SecondaryDisplay, RoleIndex = 1 });
            cat.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = "rd", ["sec"] = "Red" } });
            cat.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = "bl", ["sec"] = "Blue" } });

            var group = new CardGroup();
            group.Cards.Add(new CapabilityCard
            {
                Type = CardEngine.CardTypeCompose, Enabled = true,
                Params = new Dictionary<string, string>
                {
                    ["CompanionFieldKey"] = "comp",
                    ["ItemSeparator"]     = ", ",
                    ["OutputPlacing"]     = "3",
                }
            });

            var segs = CardEngine.GetComposeSegments(group, cat, "rdbl", null, null).ToList();
            Assert.Single(segs);
            Assert.Equal("comp", segs[0].FieldKey);
            Assert.Equal(3, segs[0].Segment.Placing);
            Assert.Equal("Red, Blue", segs[0].Segment.Text);
        }

        [Fact]
        public void BuildSortedShort_DirectTokens_SortByCatalogPlacing()
        {
            // typed "x-60-pur" → canonical "60-pur-x" (by GroupSortKey 1,2,5)
            Assert.Equal("60-pur-x", CardEngine.BuildSortedShort(new CardGroup(), Cat(), "x-60-pur", "-", "PRI", null, id => null));
        }

        [Fact]
        public void BuildSortedShort_PackedToken_UsesComposeOutputPlacing()
        {
            var c = new CatalogData { Id = "ss", Name = "SS" };
            c.Columns.Add(new CatalogColumn { Key = "pri", Role = ColumnRole.PrimaryDisplay,   RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "sec", Role = ColumnRole.SecondaryDisplay, RoleIndex = 1 });
            c.Columns.Add(new CatalogColumn { Key = "gst", Role = ColumnRole.GroupSortKey,      RoleIndex = 1 });
            void Add(string p, string s, string g) => c.Entries.Add(new CatalogEntry { Values = new Dictionary<string, string> { ["pri"] = p, ["sec"] = s, ["gst"] = g } });
            Add("60", "ISO 60", "1"); Add("pur", "PUR", "2"); Add("rd", "Red", "9");
            var group = new CardGroup();
            group.Cards.Add(new CapabilityCard { Type = CardEngine.CardTypeCompose, Enabled = true,
                Params = new Dictionary<string, string> { ["CompanionFieldKey"] = "x", ["ItemSeparator"] = ", ", ["OutputPlacing"] = "3" } });
            // "rdrd" is not a direct PRI → expands via the Compose card (OutputPlacing 3), sorting after pur(2).
            Assert.Equal("60-pur-rdrd", CardEngine.BuildSortedShort(group, c, "rdrd-60-pur", "-", "PRI", null, id => null));
        }
    }
}
