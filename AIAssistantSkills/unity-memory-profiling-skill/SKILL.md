---
name: unity-memory-profiling-skill
description: Analyzes Unity Memory Profiler snapshots (.snap) to survey memory footprint (resident/committed bytes, objects, textures, native subsystems) and guide reductions. Use for memory usage / footprint / out-of-memory / memory snapshot investigation queries. NOT for CPU/frame-time or GC-allocation performance profiling — that is the unity-profiling-skill. For Player captures.
required_editor_version: ">=6000.3.13"
required_packages:
  com.unity.memoryprofiler: ">=1.1.0"
tools:
  - Unity.MemoryProfiler.GetLoadedSnapshotState
  - Unity.MemoryProfiler.ListAvailableSnapshots
  - Unity.MemoryProfiler.Initialize
  - Unity.MemoryProfiler.GetMemoryOverview
  - Unity.MemoryProfiler.GetTopUnityObjectCategories
  - Unity.MemoryProfiler.GetNativeSubsystems
  - Unity.MemoryProfiler.GetUnityObjectTypeDetail
  - Unity.MemoryProfiler.GetNativeSubsystemDetail
  - Unity.MemoryProfiler.GetPotentialDuplicates
  - Unity.MemoryProfiler.GetObjectRetentionPath
  - Unity.MemoryProfiler.GetUnrootedAllocationBreakdown
  - Unity.MemoryProfiler.InitializeComparison
  - Unity.MemoryProfiler.GetComparisonOverview
  - Unity.MemoryProfiler.SetComparisonDrillTarget
  - Unity.MemoryProfiler.GetComparisonUnrootedBreakdown
  - Unity.MemoryProfiler.GetCustomization
---
## Role
You analyze Unity Memory Profiler snapshots to find where memory goes and how to bring it down. Treat this as a **Wide Survey**, not a single-bottleneck hunt: gather improvement candidates across many groups and rank them by **resident** memory share. Keep findings specific and actionable. When a request is ambiguous, make a reasonable assumption, state it, and investigate — don't stall to interrogate the user.

## Scope
- **Player captures only.** If `Initialize` reports `captureOrigin: Editor`, tell the user this is out of v1 scope (Editor memory distorts Native/Subsystem analysis) and stop.
- **Metric**: prefer **resident** memory. If `residentAvailable` is false (older Memory Profiler/Editor captures, some Android) → analyze by **allocation size (committed)** and **state this caveat up front** in the result. Never read `resident: null` as 0.
- **GPU ≠ resident**: `gpuCommitted` is not counted in resident. On unified-memory platforms (most mobile, Apple Silicon) GPU is physical RAM, so include `gpuCommitted` as physical footprint when ranking even under the resident metric — don't bury GPU-heavy textures just because their resident is small.
- **Untracked / Graphics(Estimated)**: not actionable groups. `Untracked` is everything the profiler doesn't track; without resident it reflects **virtual** memory (≈ total virtual − Unity-tracked), wildly larger than physical → caption it as "virtual, not a basis for judgment" and never rank it as a top candidate.
- **Unrooted** (formerly `Unknown`) is **different from Untracked**: it's native allocations with no root reference, and it **is** drillable — when a large `Unrooted`/`Unknown` subsystem shows up, use `GetUnrootedAllocationBreakdown` to attribute it to allocation sites + callstacks (workflow B4). Managed `GC.Alloc` spikes are out of scope → refer to the CPU Profiler.
- **Number formatting (strict)**: tools return exact byte integers. When showing human-readable sizes, use the **same binary units as the Memory Profiler window** (1 KB = 1024 B, 1 MB = 1024², Unity `EditorUtility.FormatBytes` convention — **never** decimal ÷10⁶) and **always print the exact bytes** alongside, e.g. `10.8 MB (11,280,499 B)`. Compute derived values (averages, percentages) from the exact integers, not from rounded MB. This keeps your numbers consistent with what the user sees in the Memory Profiler UI.

## Mode detection (do this first)
There are two analysis modes: **single-snapshot** (Two-Phase survey below) and **comparison** (2-snapshot diff). Pick the mode in this order:
1. **Explicit `.snap` path(s)** in the request → load them directly (one path → single `Initialize`; two paths → `InitializeComparison`). This overrides whatever the window has open.
2. **Explicit mode intent** in the request → follow it: compare / diff / "what grew" / "before vs after" / regression-between-builds → **comparison**; "analyze this snapshot" / "survey memory" / OOM of one capture → **single**.
3. **Neither given (ambiguous, e.g. "analyze memory usage")** → call `GetLoadedSnapshotState` and match the window: `loadedMode:"comparison"` → **Comparison Mode**; `loadedMode:"single"` → **Two-Phase Workflow**; `loadedMode:"none"` → nothing is loaded, so call `ListAvailableSnapshots` and offer the captures it returns (see the "No snapshot loaded" branch in the workflow reference) instead of dead-ending.

**One-line rule: an explicit request or file path overrides the detected loaded mode; otherwise analyze in whatever mode the window is already in.** `GetLoadedSnapshotState` is read-only and loads nothing — it only tells you how to route.

## Two-Phase Workflow
Read `references/memory-analysis-workflow.md` for the Golden Path, ranked-list construction, stop-condition, and per-group 5-slot output rules.

1. **Phase A — Overview (once)**: `Initialize` → `GetMemoryOverview` + `GetTopUnityObjectCategories` + `GetNativeSubsystems` (batch). Build one **ranked candidate list** sorted by resident %.
2. **Phase B — Group Drill (repeat)**: user picks one or more groups → for each, drill with the detail tools and emit the **5-slot output** (Identify / Quantify / Hypothesize / Recommend / Verify). After each group, check the **stop-condition**.

## Comparison Mode (2-snapshot diff)
Use this mode — **not** the single-snapshot survey — when the user wants to **compare two snapshots**: "what grew / changed between", "memory leak / regression between builds", "before vs after", "diff snapshot A and B"; **or** when the request is ambiguous and `GetLoadedSnapshotState` reports `loadedMode:"comparison"` (see "Mode detection" above). B is the candidate/later capture, A the baseline; deltas are **B − A** (positive = grew). See `references/memory-analysis-workflow.md` "Comparison Phase".

1. **`InitializeComparison`** — load the pair (omit both paths to use the Memory Profiler window's Compare pair; or pass both `.snap` paths). If `captureOriginA` or `captureOriginB` is `Editor`, stop (out of scope). If `residentAvailableBoth` is false, state up front that the diff is **committed-only**.
2. **`GetComparisonOverview`** — ranked `typeGroupDeltas` (by `|committedDelta|`) + `subsystemDeltas` + whole-snapshot totals, each tagged `changeKind` (new/grew/shrank/freed/unchanged). Each type/subsystem row already carries **both** `committedA`/`committedB` (and resident, count A/B) — the absolute sizes, not just the delta.
3. Present the **ranked diff**: lead with `new` and `grew`; the same **resident-first / GPU / Untracked** rules and Artifact/EditorOnly exclusions as Phase A carry over.
4. **Drill** the significant changed types with the **single-snapshot Phase B tools** (`GetUnityObjectTypeDetail`, `GetPotentialDuplicates`, `GetNativeSubsystemDetail`, `GetObjectRetentionPath`). After `InitializeComparison` these tools **already target snapshot B (candidate)** — the result's `drillTarget` confirms this. To inspect the **baseline A**, call `SetComparisonDrillTarget("A")` first (and `"B"` to switch back). Every drill result echoes `comparisonDrillTarget` ("A"/"B") so you can confirm which snapshot it ran on. Emit the 5-slot output with a "what changed since A" angle in Hypothesize.
   - **Do NOT** call `Initialize` or search the filesystem for `.snap` paths in comparison mode — the pair is already loaded; switch drill target with `SetComparisonDrillTarget`.
   - **Reconcile counts** against the right absolute field: a drill on B matches `typeGroupDeltas[].countB` (not `countDelta`, which is B−A). `typeGroupDeltas` lists **changed types only**; if the drilled type isn't there (its change ranked below `topN` — compare `changedTypeGroupCount` to the list length), **don't hunt for it** — just report the drill's absolute B numbers, or raise `topN` if you need the A/B split. Reconciliation is optional polish, never a blocker.
5. **Unrooted diff** (native growth/leak): if the Unrooted bucket grew (or the user suspects a native leak), call `GetComparisonUnrootedBreakdown` — diffs Unrooted by memory label and by callstack signature (B − A), classified new/grew/shrank/freed. See `references/memory-analysis-workflow.md` "C5".

## Baseline + two overlays
Load order: **baseline → playbook (general) → project-customization (this project)**. Each overlay is **additive** — it never changes the mechanical numbers; a later (more specific) layer overrides an earlier one.
- **Baseline** (always): ranked list, Identify/Quantify/Verify, structural classification, cross-tool redirects, coverage/stop mechanics — produced from tool data alone.
- **Loading the overlays — call the tool, don't assume a bundled file**: at the start of Phase A (or the Comparison equivalent), call `GetCustomization(layer="playbook")` and `GetCustomization(layer="project")`. These overlays live in the **consuming project's `Assets/`** (not in this skill's own package bundle) because they are project-owned data that must survive this package being updated or removed. If a call returns an `error` (no overlay recorded/imported yet for that layer), proceed **without** that layer — this is a normal, expected state (e.g. a fresh project, or one that hasn't imported the "Default Overlays" package sample yet), not a failure to report to the user.
- **Playbook overlay** (`layer="playbook"`, if returned without error): **project-independent** know-how — difficulty/priority ratings, Recommend content, default thresholds, analysis perspectives. Applies to any project.
- **Project Customization overlay** (`layer="project"`, if returned without error): **this project's** accepted costs, intentional characteristics, and project-specific threshold overrides. Overrides the playbook for this project.
- **Scope-matching**: apply only Learned Entries whose `scope` matches the current snapshot's platform/type/captureOrigin. The `project` field is informational only (`*` by convention — a snapshot does not expose a project name): **never skip an entry because of its `project` value**; the project overlay's file is already project-scoped. Mismatched entries are skipped or marked low-confidence to prevent overfitting.
- **Accepted costs** (`accepted` entries, project overlay): when a candidate (type/subsystem/group) matches an `accepted` entry's scope, mark it `✅ accepted (project)`, give the one-line reason, and **exclude it from the improvement-candidate ranking** (or move it to a separate "accepted/expected" section). Still **show its measured numbers** — only the judgment/recommendation is suppressed (additive). This keeps periodic health checks focused on new deviations rather than re-litigating intentional costs.
- If neither overlay is present, emit baseline and note that recording customizations (or importing the package's "Default Overlays" sample into `Assets/`) yields tuned, project-aware recommendations.

## References
- `references/memory-analysis-workflow.md` — 2-phase Golden Path, ranked list, stop-condition, 5-slot rules, Recommend 3-mode
- `references/improvement-groups.md` — group taxonomy, per-type derivable signals, snapshot-derivable vs cross-tool-referral boundary
- `references/native-subsystems-catalog.md` — AreaName classification (Actionable/Redirect/Artifact/Inform-only/Editor-only)
- Playbook / Project Customization overlays — **not bundled here**; loaded at runtime via `GetCustomization(layer="playbook"|"project")` from the consuming project's `Assets/`. A starter template for both is available as this package's "Default Overlays" sample (Package Manager ▸ this package ▸ Samples ▸ Import).
