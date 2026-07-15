# Changelog

All notable changes to this package are documented in this file.

## [0.1.4] - 2026-07-15

- The analysis now states which snapshot it analyzed. `Initialize` returns the capture's `snapshotName`
  (file name) and `productName` (the captured project's name), and `InitializeComparison` returns both
  for the A/B pair — the skill prints `Analyzed snapshot: …` (single) or `Compared: A=… vs B=…`
  (comparison) at the top of the result, so output is unambiguous when several captures are in play.
- Project-scoped customization entries now match on the real project. Because `Initialize` exposes the
  captured project's `productName`, a learned entry with a concrete `project=<name>` scope applies only
  to that project's captures; `project=*` (still the default) continues to match any project, so existing
  entries are unaffected.

## [0.1.3] - 2026-07-15

- When no snapshot is loaded and no file path is given, the analysis skill no longer dead-ends. The new
  read-only `ListAvailableSnapshots` tool enumerates the `.snap` captures on disk (sorted so captures
  matching the current project come first, then most recent) so it can offer you one to analyze; picking
  it loads via `Initialize`.
- Documented a benign console-noise case: loading some captures (notably iOS IL2CPP) prints a burst of
  Memory Profiler package asserts from its "Shortest Path to Root" processing. These are non-fatal and do
  not affect the analysis; the skill now recognizes them instead of treating them as an error.

## [0.1.2] - 2026-07-14

- Unrooted allocation analysis now understands IL2CPP (iOS) callstacks, not just Mono JIT. Sites
  reachable from your scripts are classified `user-code`/app-actionable instead of all collapsing to
  `native`, and comparison-mode Unrooted diffs join across captures. Validated on an iOS capture.
- The analysis skill now detects whether the Memory Profiler window is in single or comparison
  (Compare) mode and, when the request doesn't specify, analyzes in that mode — via the new read-only
  `GetLoadedSnapshotState` tool. Explicit file paths or "compare/diff" intent still take precedence.

## [0.1.1] - 2026-07-13

- Added a Korean README (`Documentation~/README.ko.md`), linked from the top of the English README.
  Lives in a `~`-suffixed folder so it's excluded from Unity's asset import (no `.meta` needed).

## [0.1.0] - 2026-07-13

Initial public preview release, distributed as a UPM package (git URL install).

- Analysis skill (`unity-memory-profiling-skill`): single-snapshot Wide Survey, 2-snapshot comparison
  (diff) mode, Unrooted allocation-callstack breakdown.
- Customization skill (`unity-memory-customization-skill`, experimental): records project-accepted
  costs and general playbook know-how via `RecordCustomization`.
- Overlay data (`playbook.md` / `project-customization.md`) now lives in the consuming project's
  `Assets/`, not inside this package, so it survives package updates/removal. A starter template is
  available via the package's "Default Overlays" sample.
