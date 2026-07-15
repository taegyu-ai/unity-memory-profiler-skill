# Memory Analysis — Golden Path (2-Phase Wide Survey)

Different from the CPU profiler's linear hot-path drill. For memory, **Phase A generates a ranked candidate-group list once → Phase B repeatedly drills into groups**.

## Mode detection (before Phase A / Comparison)
Decide single vs comparison before anything else. If the request names `.snap` path(s) or an explicit intent, follow that (one path or "analyze this" → single; two paths or "what grew / diff / before-vs-after" → comparison). If the request is **ambiguous** (e.g. "analyze memory usage"), call `Unity.MemoryProfiler.GetLoadedSnapshotState` (read-only, loads nothing) and route by `loadedMode`: `"single"` → start at **A0** below; `"comparison"` → go to the **Comparison Phase** (SKILL.md "Comparison Mode"); `"none"` → go to the **No snapshot loaded** branch below (offer captures via `ListAvailableSnapshots`) rather than dead-ending. An explicit request/path always overrides the detected mode.

## Phase A — Snapshot Overview (once)

### A0. Load customization overlays
Call `GetCustomization(layer="playbook")` and `GetCustomization(layer="project")` once per conversation
(order-independent relative to A1). Each call either returns overlay `content` (treat its `§`-numbered
sections as `playbook.md §N` / `project-customization.md §N` in the rules below) or an `error` — an error
just means that layer isn't available yet (fresh project, or the "Default Overlays" package sample hasn't
been imported into `Assets/`); proceed with baseline-only rules for that layer, silently, not as a problem
to surface.

**Scope-matching a learned entry**: apply an entry only if its `scope` matches this snapshot — match `platform`/`type`/`captureOrigin` as usual, and match `project` against Initialize's `productName`. `project=*` (the default) matches any project; a concrete `project` value applies only when it equals `productName`; if `productName` is null (legacy capture) drop the `project` dimension. This runs after A1, so if you called A0 first just defer the project-match until you have `productName`.

### A1. Initialize
`Unity.MemoryProfiler.Initialize(filePath)` →
- `captureOrigin == Editor` → **abort**, notify that this is out of v1 scope (Editor memory distorts Native/Subsystem analysis).
- **Snapshot identity**: the result includes `snapshotName` (the capture file name) and `productName` (the captured project's name, or null for legacy captures). Keep both — `snapshotName` is stated at the top of the analysis (A3), and `productName` drives project scope-matching for the project overlay (A0).
- **Metric selection (must be stated explicitly)**: check `residentAvailable`(=`hasSystemMemoryRegionsInfo`).
  - `true` → analyze based on **resident** (preferred metric).
  - `false` → no resident data (older Memory Profiler/Editor captures, some Android captures, etc.). **Analyze based on allocation size (committed)**, and **you must state at the top of the result: "This capture has no resident (physical footprint) information, so the analysis is based on allocation (committed)"**. Do not interpret a resident=null value as 0.
- **Known benign console noise (not our bug, not a failure)**: loading a snapshot may print a burst of package asserts/errors like `Native allocation ... should be ...` / `Native Allocation failed validation: (... AreaName: Managers, ObjectName: IL2CPP VM ...)`. These come from the **Memory Profiler package's** "Shortest Path to Root" processing (`RootAndImpactInfo.Validate`), which runs on load whenever the *Preferences ▸ Analysis ▸ Memory Profiler ▸ "Enable Shortest Path to Root"* setting is on (default on in recent builds) and the capture has scene roots — the same processing runs when you open the capture in the Memory Profiler window. They are `Debug.Assert`/`LogError` (non-fatal): the `Initialize` result is still valid (`error:null`) and the survey is unaffected. Do **not** treat them as a tool error or retry. If the user asks about the noise or wants a clean console, explain it is package-side and they can turn that preference **off** — at the cost of `GetObjectRetentionPath` (workflow B1) degrading to `pathDataAvailable:false` (retention-path drill unavailable). Most likely a package limitation on IL2CPP captures; worth an upstream report but out of this skill's control.

### A1b. No snapshot loaded (branch)
Reached when `GetLoadedSnapshotState` returns `loadedMode:"none"`, or `Initialize` (with no `filePath`) returns an `error` because nothing is open in the Memory Profiler window. Do **not** dead-end — help the user pick a capture:
1. Call `ListAvailableSnapshots` (read-only; enumerates `.snap` files in the project's capture folder, lists nothing loaded).
2. If it returns candidates (already sorted project-match-first, then most-recent), present the top few — filename, last-modified, size, and which match the current project (`matchesCurrentProject`). Ask which to analyze, or offer the most relevant as a default.
3. When the user picks one, call `Initialize(filePath=<that candidate's filePath>)` and continue at A2.
4. If `directoryExists:false` or the list is empty, relay the tool's `note` (how to take a capture) and stop — there is nothing to analyze yet.

### A2. Collect Overview (batch)
Call all at once:
- `GetMemoryOverview` — Allocated 5-bucket + Managed breakdown + Resident total + fragmentation
- `GetTopUnityObjectCategories` — type groups
- `GetNativeSubsystems` — AreaName groups + classification

### A3. Build Ranked Candidate List (baseline)
Merge the above results into a single ranked list, **sorted by resident % descending**. Each row:
- **Resident %** (primary sort) · size (committed/resident) · group identity · analyzability
- Filter by `GetNativeSubsystems`'s `classification`:
  - `Artifact`(profiler overhead)·`EditorOnly` → **exclude from the ranked list** (mark only as a measurement artifact)
  - `Redirect`(Objects) → keep the row, but branch to Unity Objects in Phase B
- **GPU memory ≠ resident**: `gpuCommitted` is not included in resident. Ranking by resident alone pushes GPU-heavy groups (Texture2D/RenderTexture) to the bottom — a real example: Texture2D committed 243MB (GPU 237MB) but resident only 5.9MB. **On unified-memory platforms (most mobile, Apple Silicon) the GPU is physical RAM**, so even when using resident as the basis, **factor `gpuCommitted` into the ranking as physical footprint too** (effective ≈ resident + gpuCommitted), and note the GPU share separately. Do not deprioritize a large GPU group just because its resident value is small.
- **Special handling for Untracked / Graphics (Estimated)**: the `Untracked*`·`Graphics (Estimated)` rows in the Allocated distribution have `residentUnavailable` and are **not an actionable improvement group.**
  - **Untracked** = every allocation the profiler does not track. **Without resident data, Untracked = (total virtual memory − Unity-tracked portion)**, so it appears as a very large **virtual memory** size that diverges significantly from actual physical memory and cannot be judged from this value alone. Even if it tops the ranked list, **mark it defer/opaque and caption it "virtual-memory-based, not a basis for judging physical footprint"**. Never promote it to a top-priority improvement candidate.
  - **Graphics** is an estimate, so do not treat it as a precise figure.
- **If a customization overlay is present**: add difficulty and recommended-priority (⭐/secondary/defer) columns (`playbook.md` §1·§2 defaults; project-specific overrides in `project-customization.md` §B). If not, sort only by size (committed if resident is unavailable).

Present the ranked list to the user and let them choose which group to drill into. **State the analyzed snapshot at the top of the result**: `Analyzed snapshot: <snapshotName>` (from Initialize). **If resident is unavailable for the capture, also state at the top of the list that the ranking is committed-based.**

## Phase B — Group Drill & Recommend (repeat)

For each group the user picks, drill with a detail tool, then output the **5-slot** format.

### B1. Drill tools by group type
- Unity Object type group → `GetUnityObjectTypeDetail(typeName)` (+ `GetPotentialDuplicates(typeName)`, and `GetObjectRetentionPath(nativeObjectIndex)` if needed — pass `objects[].nativeObjectIndex` from `GetUnityObjectTypeDetail`, **not** `instanceId`)
- Native subsystem group → `GetNativeSubsystemDetail(areaName)`
- **`Unrooted`/`Unknown` subsystem (native allocations with no root)** → `GetUnrootedAllocationBreakdown(topN)` (see B4 below)
- Managed/fragmentation → `GetMemoryOverview`'s managed/fragmentation + retention (if needed)

### B2. 5-slot output
1. **Identify** — group identity·category (baseline)
2. **Quantify** — resident size·% of total·member count (baseline; use the Native/Managed/GPU split). ⚠️ **GPU is not measured as resident** — GPU-heavy types (Texture2D/RenderTexture) show totalResident < totalCommitted, so report the GPU share as committed and caption it "GPU is outside physical-residency measurement".
3. **Hypothesize** — hypothesize the reason it's large. If it can't be confirmed, state it explicitly: "Hypothesis: ... (cannot be confirmed from a single snapshot — use [tool] to verify)". Reinforce with a retention path.
4. **Recommend** — choose one of **3 modes**:
   - 🟢 `reduce` — a reduction derivable from the snapshot (dedup·unload·RT pooling·review persistence). (a) method (b) effect (c) difficulty (d) risk.
   - 🔵 `refer` — branch to a cross-tool (format/compression/mipmap/read-write/load type → import settings·build report; fragmentation → CPU GC analysis). (a-d) are based on the branched-to action.
   - ⛔ `none` — no action needed (fixed engine cost)·hard to reduce directly. Reason + optional indirect note.
   - The recommendation body comes from the customization overlay (`playbook.md` §3·§4, plus any project-specific overrides in `project-customization.md` §B). Without an overlay, present only the classification/redirect, and note that concrete recommendations become available once the overlay is active.
5. **Verify** — where to look after a recapture (that group's resident/area sum/count).

### B3. Stop-condition (after each group)
If either `(cumulative coverage ≥ X%) OR (next candidate resident% < Y%)` is met → ask the user whether to continue.
- Default X=75%, Y=3%, coverage = ratio of summed resident of analyzed groups (defaults in `playbook.md` §1.2, overridable per project in `project-customization.md` §B).

### B4. Unrooted subsystem drill (`GetUnrootedAllocationBreakdown`, step 9)
**When**: when the `Unrooted` (formerly `Unknown`) group shows up as a large candidate in the Native Subsystems survey. Unrooted = native allocations with no root reference, so they are not attributed to any Unity object/root → a blind spot that no other drill tool can open. (Do not confuse with `Untracked`: Untracked is profiler-untracked virtual memory, so it's opaque·defer, whereas **Unrooted can be opened with this tool.**)

Interpreting the `GetUnrootedAllocationBreakdown(topN)` return value:
- `totalUnrootedCommitted` / `totalUnrootedResident` (when resident is available) / `unrootedAllocationCount` — **always** computed. committed·resident are based on the unified memory map (EntriesMemoryMap), so they **match the `Native > Unrooted` node's committed/resident in the Memory Profiler window** (not a sum of raw allocations). Use them as-is for Quantify, preferring resident.
- `available:false` — this capture has no allocation callstacks (cannot attribute). **Instead of a dead-end conclusion**, pass along the `note` as-is to guide a recapture: *"Recapturing with `-enable-memoryprofiler-callstacks` (Unity 6000.3+) will let you break down Unrooted memory by callstack."* Report only the total and stop.
- `available:true` — `topSites[]` provides, per site, `committedBytes`·`residentBytes`·`allocationCount`·`memoryLabel`·`callstack` (human-readable callstack)·`frameOrigin`·`appActionable`/`actionabilityHint`. **Apply Recommend using the 3 `frameOrigin` categories**:
  - `frameOrigin:"user-code"` (the callstack has a managed frame outside the engine/system = my `Assembly-CSharp`/plugin, `appActionable:true`) → 🟢 `reduce`/investigate — recommend reviewing the code directly, citing the relevant script line from the callstack.
  - `frameOrigin:"engine-managed"` (all managed `.cs` frames are `UnityEngine.*`/`Unity.*`/`System.*`, e.g. the URP render loop) → 🔵 `refer` (indirect) — not my code, but **can be adjusted indirectly via project/quality/component settings** (e.g. camera stack·URP asset·draw calls). Not a Unity bug.
  - `frameOrigin:"native"` (no managed frame) → 🔵 `refer` — internal engine native code, hard to modify directly; file a Unity bug report if needed.
  - ⚠️ This is a **heuristic** (based on the declaring-type namespace in the callstack). Present the raw callstack alongside it and do not state it as certain. Keep in mind this was originally a debugging tool for engine developers, so the range of action available to game/app developers is narrow (usually `engine-managed`/`native`). **On IL2CPP builds (mobile/iOS) the symbol carries no namespace**, so the engine↔user split falls back to a namespace-stripped type-token heuristic (coarser than the Mono namespace check) — lean harder on the raw callstack there, and read the `_m<hash>`-suffixed symbols in it as the C# method names (e.g. `EquipmentManager_OnEquipmentWeaponChanged`).

## Comparison Phase — 2-Snapshot Diff (comparison mode)

A **separate mode** from a single survey. Triggers: "compare two snapshots / the increase / what grew / leak·regression / before-after / diff A vs B". A=baseline, B=candidate/later, delta is **B − A** (positive = increase). Inherits the Phase A/B rules above as-is (resident-first·GPU·Untracked·classification exclusion·5-slot·stop).

### C1. InitializeComparison
`Unity.MemoryProfiler.InitializeComparison(filePathA, filePathB)` →
- Both omitted → use the Memory Profiler window's **Compare pair** (Base+Compared). Both specified → load the files. Only one specified → error.
- `captureOriginA` or `captureOriginB == Editor` → **abort** (out of scope).
- `residentAvailableBoth == false` → state at the top of the result that the **comparison is committed-based**. Do not read a null resident field as 0.
- **Snapshot identity**: the result includes `snapshotNameA`/`snapshotNameB` (the two capture file names) and `productNameA`/`productNameB`. Keep them — state which pair you compared at the top of the diff (C3), and use `productNameB` (the candidate's project) for project scope-matching.

### C2. GetComparisonOverview
`Unity.MemoryProfiler.GetComparisonOverview(topN=50)` →
- `typeGroupDeltas` — **changed types only** (unchanged excluded), sorted by `|committedDelta|` descending, truncated at topN. Fields: committed/resident **absolute A·B values** + delta, count A·B·delta, `changeKind` (new/grew/shrank/freed). If `changedTypeGroupCount` > the returned length, it was truncated (raise topN).
- `subsystemDeltas` — changed subsystems only, per-AreaName committed/resident A·B·delta + `classification` + `changeKind` (no cutoff).
- Overall totals: `totalCommittedDelta`, `totalResidentDelta` (when available).
- ⚠️ **`countB`/`committedB` for the `MonoBehaviour`·`ScriptableObject` types can be undercounted** (a bug in the comparison-builder package): instances of **C# script subclasses newly introduced only in B (candidate)** are missing from the comparison tree (A·baseline values and subsystem·totals are unaffected). So if these two base types show a large `grew`/`new`, **the actual increase may be larger than reported** — if you need the exact absolute B value, drill with `SetComparisonDrillTarget("B")` + `GetUnityObjectTypeDetail` to confirm, and describe the increase as a "minimum" rather than asserting an exact figure. (A known limitation confirmed via oracle comparison.)

### C3. Ranked Comparison List
- **State the compared pair at the top of the result**: `Compared: A=<snapshotNameA> vs B=<snapshotNameB>` (from InitializeComparison).
- Sort by `|committedDelta|` (together with resident delta when resident is available). **Prioritize highlighting `new`·`grew`** (from an increase/leak/regression perspective).
- GPU: same as single mode — comparison ItemData has no GPU split (committed/resident based). The unified-memory-platform caveat still applies.
- `Untracked`/`Graphics(Estimated)`: even with a large committed delta, it's **virtual-based** → do not rank as a top candidate, caption it. subsystem `classification=Artifact/EditorOnly` → exclude from ranking.

### C4. Drill (reuse existing Phase B)
For a significant change type → single-snapshot Phase B tools (`GetUnityObjectTypeDetail`/`GetPotentialDuplicates`/`GetNativeSubsystemDetail`/`GetObjectRetentionPath`) → 5-slot output. Add an "A→B change cause" angle to Hypothesize (new objects introduced/missed unload/duplicate increase, etc.).
- **Drill target**: right after `InitializeComparison`, these tools **automatically target B (candidate)** (confirmed by `drillTarget:"B"` in the `InitializeComparison` result). **To look at baseline A, first call `SetComparisonDrillTarget("A")`** (call it with `"B"` to switch back). Every drill result echoes `comparisonDrillTarget` ("A"/"B") so you can tell which snapshot you looked at from the result alone.
- ⚠️ In comparison mode, **do not call `Initialize` or search the filesystem for `.snap` file paths** — the pair is already loaded, and the drill target is switched only via `SetComparisonDrillTarget`.
- **Count cross-check (optional)**: check the B drill result against `typeGroupDeltas[].countB` (absolute value) — don't confuse it with `countDelta` (=B−A). `typeGroupDeltas` holds **changed types only** (`changeKind != unchanged`); if the drilled type isn't in the list (if `changedTypeGroupCount` > the returned length, it was truncated at topN), **don't go hunting for it** — just report the drill's absolute B value as-is, or raise topN. The cross-check is only a supplementary confirmation, not a reason to block progress.

### C5. Unrooted diff (`GetComparisonUnrootedBreakdown`) — tracking native growth/leaks
Use when checking, in comparison mode, whether **Unrooted has grown** (a native leak signal). `GetComparisonUnrootedBreakdown(topN)` returns:
- `totalUnrootedCommitted/ResidentDelta` (B−A) — **always**. An increase is a first-line signal of native growth.
- `available:false` (one or both sides lack callstacks) → report only the total delta + guide, per the `note`, to **recapture both sides with `-enable-memoryprofiler-callstacks`**.
- `available:true`:
  - `labelDeltas[]` — committed/resident/count delta + `changeKind` (new/grew/shrank/freed) **per memory label**. Answers "which subsystem's Unrooted grew" (e.g. VertexData +5MB). Reliable, since the label is a stable key.
  - `siteDeltas[]` — delta + `changeKind` + `frameOrigin` (same user-code/engine-managed/native as B4) + a representative `callstack`, **per callstack signature** (joined across captures/builds by normalizing away line numbers). Changed-only, sorted by `|committedDelta|`, topN. **Prioritize highlighting `new`·`grew`** — "which code path leaked". Branch the action direction (user-code 🟢 / engine-managed·native 🔵) via `frameOrigin`, same as B4.
- ⚠️ The join is based on the **symbolicated callstack, not the address** (addresses change between captures). It's exact for two points in the same build; across different builds, line-number differences are absorbed by normalization, but a site whose code changed may show up as new/freed (expected).

### Stop-condition (comparison mode)
`(cumulative |committedDelta| coverage ≥ 75%) OR (next candidate |committedDelta|% < 3%)` → confirm whether to continue.

## Core Principles
- **Resident first.** If only committed is available, state that fact.
- **Group-level share > individual object size.**
- **Format-related information is not in the snapshot** → always 🔵 refer (`improvement-groups.md`).
- **Be strict with numeric formatting**: tools return exact byte integers. Compute human-readable sizes using the **same binary conversion as the Memory Profiler window** (1 KB=1024 B, 1 MB=1024², the Unity `EditorUtility.FormatBytes` convention — decimal ÷10⁶ is **forbidden**), and **always include the raw byte count alongside it** (e.g. `10.8 MB (11,280,499 B)`). Derived values like averages/ratios must be computed **from the raw integer**, not from a rounded MB value. (E.g. `11,280,499 B` is **`10.8 MB`**, not `11.28 MB`.)
- **managed `GC.Alloc` (per-frame temporary allocations·GC pressure) is outside the scope of this snapshot tool** → 🔵 refer: do live analysis with the CPU/Timeline Profiler's **"Call Stacks"** button (or `Profiler.enableAllocationCallstacks`). The Unrooted callstacks in the memory snapshot (B4) attribute **native** allocations, which is separate from managed GC.Alloc.
- **Preserve Wide Survey coverage**: don't omit low-resident groups either — mark them as lower priority instead, and revisit at the stop point.
