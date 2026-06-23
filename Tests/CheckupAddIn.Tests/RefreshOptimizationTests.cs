using CheckupAddIn.Models;
using CheckupAddIn.Services;
using CheckupAddIn.ViewModels;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Characterization tests for the optimization experiment (branch: experiment/optimize).
    ///   Kit #1 — GuardUnchangedSetters: WPF notification guard (flicker fix).
    ///   Kit #2 — BuildDocSignature: doc-set signature used by the refresh value cache.
    ///   Kit #3 — MergeAssetNames: doc-local + global-library asset-name merge (2a catalog cache).
    /// Pure logic only — no Inventor/COM.
    /// </summary>
    public class RefreshOptimizationTests
    {
        // ── Kit #1: GuardUnchangedSetters ─────────────────────────────────────────

        [Fact]
        public void Kit1_GuardedSetter_SuppressesNotification_WhenValueUnchanged()
        {
            // Guard is unconditional since T39 un-gating (the GuardUnchangedSetters toggle was removed).
            var row = new RowModel { DisplayValue = "hello" };
            RowModel.DisplayValueSetTotal   = 0; // reset after setup so only the test call is counted
            RowModel.DisplayValueSetChanged = 0;
            int notified = 0;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RowModel.DisplayValue)) notified++; };

            row.DisplayValue = "hello"; // same value → guard fires, no notification

            Assert.Equal(0, notified);
            Assert.Equal(1, RowModel.DisplayValueSetTotal);
            Assert.Equal(0, RowModel.DisplayValueSetChanged);
        }

        [Fact]
        public void Kit1_GuardedSetter_AllowsNotification_WhenValueChanges()
        {
            var row = new RowModel { DisplayValue = "hello" };
            RowModel.DisplayValueSetTotal   = 0;
            RowModel.DisplayValueSetChanged = 0;
            int notified = 0;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RowModel.DisplayValue)) notified++; };

            row.DisplayValue = "world"; // different value → notification fires

            Assert.Equal(1, notified);
            Assert.Equal(1, RowModel.DisplayValueSetTotal);
            Assert.Equal(1, RowModel.DisplayValueSetChanged);
        }

        [Fact]
        public void Kit1_UnconditionalGuard_CountsAttemptsButSuppressesUnchangedRepaints()
        {
            // T39 shipped the guard unconditionally (no toggle): repeated same-value sets are counted
            // in Total but never repaint and never raise PropertyChanged.
            var row = new RowModel { DisplayValue = "hello" };
            RowModel.DisplayValueSetTotal   = 0;
            RowModel.DisplayValueSetChanged = 0;
            int notified = 0;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RowModel.DisplayValue)) notified++; };

            row.DisplayValue = "hello"; // same
            row.DisplayValue = "hello"; // same again

            Assert.Equal(0, notified);
            Assert.Equal(2, RowModel.DisplayValueSetTotal);
            Assert.Equal(0, RowModel.DisplayValueSetChanged);
        }

        // ── Kit #2: BuildDocSignature ─────────────────────────────────────────────

        [Fact]
        public void Kit2_BuildDocSignature_IsOrderIndependent()
        {
            string sig1 = CheckupViewModel.BuildDocSignature(new[] { @"C:\a.ipt", @"C:\b.ipt" });
            string sig2 = CheckupViewModel.BuildDocSignature(new[] { @"C:\b.ipt", @"C:\a.ipt" });

            Assert.Equal(sig1, sig2);
        }

        [Fact]
        public void Kit2_BuildDocSignature_DifferentiatesDocSets()
        {
            string sig1 = CheckupViewModel.BuildDocSignature(new[] { @"C:\a.ipt" });
            string sig2 = CheckupViewModel.BuildDocSignature(new[] { @"C:\b.ipt" });

            Assert.NotEqual(sig1, sig2);
        }

        [Fact]
        public void Kit2_BuildDocSignature_EmptyInputReturnsEmpty()
        {
            Assert.Equal("", CheckupViewModel.BuildDocSignature(System.Array.Empty<string>()));
        }

        [Fact]
        public void Kit2_BuildDocSignature_NullInputReturnsEmpty()
        {
            Assert.Equal("", CheckupViewModel.BuildDocSignature(null));
        }

        [Fact]
        public void Kit2_BuildDocSignature_SingleDocRoundTrips()
        {
            string path = @"C:\parts\e-db2-37573_NLi6.ipt";
            string sig = CheckupViewModel.BuildDocSignature(new[] { path });

            Assert.Equal(path, sig);
        }

        [Fact]
        public void Kit2_BuildDocSignature_AddingDocChangesSignature()
        {
            string sig1 = CheckupViewModel.BuildDocSignature(new[] { @"C:\a.ipt" });
            string sig2 = CheckupViewModel.BuildDocSignature(new[] { @"C:\a.ipt", @"C:\b.ipt" });

            Assert.NotEqual(sig1, sig2);
        }

        // ── Kit #3: MergeAssetNames ───────────────────────────────────────────────
        // Splitting the asset walk into a cached global half + a cheap doc-local half must
        // reproduce the old single-pass result: union, OrdinalIgnoreCase dedup (doc wins), sorted.

        [Fact]
        public void Kit3_MergeAssetNames_UnionsAndSorts()
        {
            var merged = FieldCatalogBuilder.MergeAssetNames(
                new[] { "Steel", "Aluminum" }, new[] { "Brass", "Titanium" });

            Assert.Equal(new[] { "Aluminum", "Brass", "Steel", "Titanium" }, merged);
        }

        [Fact]
        public void Kit3_MergeAssetNames_DedupsCaseInsensitively_DocLocalWins()
        {
            // "STEEL" from the library collides with doc-local "Steel" → dropped, doc casing kept.
            var merged = FieldCatalogBuilder.MergeAssetNames(
                new[] { "Steel" }, new[] { "STEEL", "Brass" });

            Assert.Equal(new[] { "Brass", "Steel" }, merged);
        }

        [Fact]
        public void Kit3_MergeAssetNames_NullGlobal_ReturnsDocLocalOnly()
        {
            var merged = FieldCatalogBuilder.MergeAssetNames(new[] { "Brass", "Aluminum" }, null);

            Assert.Equal(new[] { "Aluminum", "Brass" }, merged);
        }

        [Fact]
        public void Kit3_MergeAssetNames_NullDocLocal_ReturnsGlobalOnly()
        {
            var merged = FieldCatalogBuilder.MergeAssetNames(null, new[] { "Brass", "Aluminum" });

            Assert.Equal(new[] { "Aluminum", "Brass" }, merged);
        }

        [Fact]
        public void Kit3_MergeAssetNames_BothNull_ReturnsEmpty()
        {
            var merged = FieldCatalogBuilder.MergeAssetNames(null, null);

            Assert.Empty(merged);
        }

        [Fact]
        public void Kit3_MergeAssetNames_DedupsWithinSameSource()
        {
            // Defensive: a source containing its own dup collapses to one entry.
            var merged = FieldCatalogBuilder.MergeAssetNames(new[] { "Brass", "brass" }, null);

            Assert.Equal(new[] { "Brass" }, merged);
        }

        // ── Kit #4: IsUserDefinedSet + NormalizeSetName (structural-cache helpers) ──────────
        // BuildStructureItems hoists these per-PropertySet calls (not per-Property). Verify the
        // pure logic so a regression in normalization stays caught without needing COM.

        [Fact]
        public void Kit4_IsUserDefinedSet_RecognizesGermanAndEnglish()
        {
            Assert.True(FieldCatalogBuilder.IsUserDefinedSet("Benutzerdefinierte Eigenschaften"));
            Assert.True(FieldCatalogBuilder.IsUserDefinedSet("User Defined Properties"));
            Assert.True(FieldCatalogBuilder.IsUserDefinedSet("Custom Properties"));
            Assert.False(FieldCatalogBuilder.IsUserDefinedSet("Design Tracking Properties"));
            Assert.False(FieldCatalogBuilder.IsUserDefinedSet("Inventor Summary Information"));
        }

        [Fact]
        public void Kit4_GetSetNameCandidates_ReturnsAliasForKnownGermanName()
        {
            string[] candidates = FieldCatalogBuilder.GetSetNameCandidates("Design Tracking - Eigenschaften");

            Assert.Contains("Design Tracking Properties", candidates);
        }

        [Fact]
        public void Kit4_GetSetNameCandidates_PassesThroughUnknownName()
        {
            string[] candidates = FieldCatalogBuilder.GetSetNameCandidates("My Custom Set");

            Assert.Single(candidates);
            Assert.Equal("My Custom Set", candidates[0]);
        }
    }
}
