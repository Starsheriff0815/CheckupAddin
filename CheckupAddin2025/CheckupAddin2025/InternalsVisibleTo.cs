using System.Runtime.CompilerServices;

// Grants the public test suite (Tests/CheckupAddIn.Tests) access to internal members
// (e.g. CheckupViewModel.EnforceButtonRules) so row-management invariants can be unit-tested.
// No public API is added; add-in users see nothing change.
[assembly: InternalsVisibleTo("CheckupAddIn.Tests")]
