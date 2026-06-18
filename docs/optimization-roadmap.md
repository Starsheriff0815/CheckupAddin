# CheckupAddin — Optimization Roadmap (experiment branch)

> **Branch:** `experiment/optimize` — **never pushed**. Public repo + `main` untouched.
> **Rollback (100%):** `git checkout main && git branch -D experiment/optimize`.
> **This doc is internal to the branch and is not part of any release.**

## Goal (from the user, 2026-06-18)
A generally **smoother live panel**. The window stays open as a live property inspector; users switch selection/documents and it auto-updates. Pain points: **flicker on auto-update** (90% case: 1–few parts) and **lag/stutter on large assemblies and on value edits** (the occasional 10%). Keep the auto-update behavior; make it cheaper. Reads + writes occasionally.

## Guardrails
- **Behavior-preserving by default.** `[pure]` items = no observable change. `[needs-OK]` items = behavior-adjacent → user sign-off + PerfLogger evidence + user's Inventor test first; TDD updated before code if it touches the spec.
- The event/sticky/deselect logic has many hard-won Inventor-quirk fixes — optimize *around* it, don't rewrite its semantics.

## Logistics (locked 2026-06-18)
- **Test target:** Inventor **2026** variant (net8).
- **Transfer:** manual zip both ways. Test machine is **clean / fresh each run** (no production Checkup).
- **Validation:** tests (18/18 multi-target, run by me) **+ PerfLogger** numbers captured by the user in Inventor.

## Hand-off workflow (because testing is on a separate work machine — slow loop, pause/resume)
Each phase ships a **test kit** (self-contained zip): the 2026 build (+ the user's 2026 interop for internal test), seeds, a ready `.addin`, `MEASURE.md` checklist, and a README (deploy + send-back).
- **Runtime toggle:** optimizations are gated by a flag file next to the DLL, so the user captures **before (off) and after (on) in ONE Inventor session on the SAME assembly** — apples-to-apples (important: fresh machine may use a different assembly each round) and it halves the test rounds.
- **PerfLogger** is enabled in the kit build only (never on `main`); each log line records the toggle mode.
- **Pause/Resume:** status is mirrored in synced memory `project_experiment_optimization.md`; updated at every hand-off so any later session (Desktop/Laptop) resumes cleanly.

## Phases
**P0 — Setup & baseline** *(no behavior change)*: branch, this doc, confirm suite green, PerfLogger instrumentation (open latency + redraw-count metric).
**P1 — Live refresh cycle** *(headline)*:
- 1a `[pure]` Guard value/visual setters (`DisplayValue`, `ValueForeground`, `MatchedPart`/`UnmatchedPart`, …) to no-op on unchanged → kills auto-update flicker; edits redraw only changed rows. **Kit #1.**
- 1b `[pure]` Skip redundant work only where provably unchanged (poller already count-guarded; coalesce duplicate triggers).
- 1c `[pure]` Cheaper `DoRefreshCore` for large selections — fewer COM round-trips, cache scalars, materialize-once, fewer per-row allocations.
- 1d `[needs-OK]` Coalescing gate for near-simultaneous triggers.
**P2 — Cold open** `[needs-OK]`: show-then-fill so the once-per-session open is perceived-instant.
**P3 — Startup & memory** *(mostly `[pure]`)*: lean `Activate()`; teardown/leak audit.
**P4 — Stability hardening** `[pure]`: catch-block audit, COM liveness, DataGrid crash-pattern coverage, `Deactivate()` idempotency.
**P5 — Maintainability refactors** `[pure]`, last: split the 3000/2693-line view-models; extract `DoRefreshCore`.

## Per-phase validation
Suite stays 18/18 (I run it) · PerfLogger before/after captured by user · one commit per phase on the branch.

## Status log
- 2026-06-18 — P0 done: branch created; roadmap committed; suite baseline 18/18 (net8+net48).
- 2026-06-18 — Kit #1 READY (P1/1a): flicker fix behind the `perf_opt.on` toggle + PerfLogger open-latency & redraw-count; 2026 Release built clean; suite still 18/18. Zip: `dist\CheckupExperiment2026_kit1.zip`. **Awaiting user Inventor measurement (PAUSED).**
