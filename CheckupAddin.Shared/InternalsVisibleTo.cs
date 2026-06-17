using System.Runtime.CompilerServices;

// Grants the public test suite (Tests/CheckupAddIn.Tests) access to internal members
// (e.g. CheckupViewModel.EnforceButtonRules) so row-management invariants can be unit-tested.
// No public API is added; add-in users see nothing change.
[assembly: InternalsVisibleTo("CheckupAddIn.Tests")]
// Grants the build-time Design Harness (Task #36) access to internal helpers
// (e.g. UiStateStore.DesignMode) so previews use factory sizes, not persisted ones.
[assembly: InternalsVisibleTo("CheckupAddIn.DesignHarness")]
