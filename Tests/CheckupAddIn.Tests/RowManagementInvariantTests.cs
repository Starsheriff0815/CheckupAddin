using CheckupAddIn.Models;
using CheckupAddIn.ViewModels;
using Xunit;

namespace CheckupAddIn.Tests
{
    /// <summary>
    /// Characterization tests for CheckupViewModel.EnforceButtonRules — the row-removal
    /// invariant: a row can never be removed if it would leave fewer than one row.
    /// This invariant must survive the legacy-field removal (Task #29).
    ///
    /// Uses the parameterless (VS Designer) constructor, which is COM-free; EnforceButtonRules
    /// is reached via [InternalsVisibleTo] (no public API added to the add-in).
    /// </summary>
    public class RowManagementInvariantTests
    {
        private static CheckupViewModel NewEmptyVm()
        {
            var vm = new CheckupViewModel();
            vm.Rows.Clear();
            return vm;
        }

        [Fact]
        public void SingleRow_CannotBeRemoved()
        {
            var vm = NewEmptyVm();
            vm.Rows.Add(new RowModel { FieldKey = "UDEF:ISO" });

            vm.EnforceButtonRules();

            Assert.False(vm.Rows[0].CanRemove);
        }

        [Fact]
        public void MultipleRows_AreAllRemovable()
        {
            var vm = NewEmptyVm();
            vm.Rows.Add(new RowModel { FieldKey = "UDEF:ISO" });
            vm.Rows.Add(new RowModel { FieldKey = "DOC:Material" });
            vm.Rows.Add(new RowModel { FieldKey = "PARAM:User:Thickness" });

            vm.EnforceButtonRules();

            Assert.All(vm.Rows, r => Assert.True(r.CanRemove));
        }
    }
}
