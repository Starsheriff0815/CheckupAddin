# Checkup Test Suite

This folder holds the **automated test suite** for Checkup — a small program whose only job
is to re-check Checkup's internal rules automatically, so an accidental change gets caught
before it ships.

Think of it as the automatic version of a manual test checklist: each rule is written once,
and the computer re-runs all of them in a couple of seconds on every build, pointing
straight at anything that broke.

## Why it's in the (public) repo

- **Anyone who clones the repo gets it as a tool** — run it to confirm your build is healthy,
  or that a change you made didn't break anything.
- **No Autodesk Inventor required.** The Inventor interop library is included under `lib/`, so
  the projects — and these tests — build and run without Inventor installed.
- **It can run in CI** (GitHub Actions) to check every push / pull request automatically.

## How to run it

From the repository root:

```
dotnet test Tests/CheckupAddIn.Tests/CheckupAddIn.Tests.csproj
```

Or open the solution in Visual Studio 2022 and use **Test Explorer**.

## One suite, both Inventor versions

Checkup ships as two identical-twin projects on different .NET versions:

| Inventor | Project | .NET |
|---|---|---|
| 2026 | `CheckupAddin2026` | `net8.0-windows` |
| 2024 | `CheckupAddin2024` | `.NET Framework 4.8` |

This test project is **multi-targeted**: the *same test code* compiles and runs against
**both** twins. If the two projects ever drift apart, a test that fails on only one
framework points it out immediately.

> The `.NET 4.8` half needs the **.NET Framework 4.8 Developer Pack** installed. If it's
> missing, that target is skipped automatically and the `.NET 8` (Inventor 2026) tests
> still run.

## What it does — and does not — cover

**Covered here (pure decision-logic, no Inventor needed):**
- Row rules and state — when a row can be removed, edit vs. display mode, value validation.
- Value / number / text formatting and the formula functions.
- Saving and loading presets.

**Not covered here (needs a live Inventor session) — see the manual test checklist:**
- Reading from / writing to a real part or assembly.
- How a missing or legacy field is shown in the live window.
- Style purge and the ribbon integration.

There is no Inventor running inside the automated tests, so those live behaviours are
verified by hand using the manual checklist document.

## A note on "internal" rules

A few of the rules being checked live in helper routines that are normally private to the
add-in. The test project is granted access to those so it can check them directly. This adds
nothing that add-in users can see or use — it only lets the tests look in.
