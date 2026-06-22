using CheckupAddIn.Models;
using CheckupAddIn.ViewModels;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Characterization tests for the optimization experiment (branch: experiment/optimize).
    ///   Kit #1 — GuardUnchangedSetters: WPF notification guard (flicker fix).
    ///   Kit #2 — BuildDocSignature: doc-set signature used by the refresh value cache.
    /// Pure logic only — no Inventor/COM.
    /// </summary>
    public class RefreshOptimizationTests
    {
        // ── Kit #1: GuardUnchangedSetters ─────────────────────────────────────────

        [Fact]
        public void Kit1_GuardedSetter_SuppressesNotification_WhenValueUnchanged()
        {
            RowModel.GuardUnchangedSetters = true;
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
            RowModel.GuardUnchangedSetters = true;
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
        public void Kit1_Baseline_Unguarded_AlwaysRaisesNotification()
        {
            RowModel.GuardUnchangedSetters = false;
            var row = new RowModel { DisplayValue = "hello" };
            int notified = 0;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RowModel.DisplayValue)) notified++; };

            row.DisplayValue = "hello"; // no guard → notification fires even for same value

            Assert.Equal(1, notified);
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
    }
}
