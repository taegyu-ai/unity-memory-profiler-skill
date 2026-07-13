# Memory Profiler Skill — Playbook Overlay (general, project-independent)

The **general layer** of the analysis skill's optional overlay (split into a general "playbook" layer and a project-dependent "project-customization" layer, so general know-how can be shared/distributed independently of project-specific data). It applies **additively** to the baseline analysis (mechanical, deterministic) — it never changes mechanical numbers and only supplies (a) difficulty/priority annotations, (b) Phase B Recommend slots, and (c) default thresholds. If this file is absent, the skill emits baseline output only.

> **This file = general, project-independent memory-analysis know-how, perspectives, and methods.** It applies to any project and is a **shareable, distributable expertise asset** for an analyst/team.
> **Project-dependent information (intentional accepted costs, etc.) does not belong here** → put it in `project-customization.md` (the dependent layer). Boundary rule: **if the insight generalizes beyond one project, it goes to playbook; if it is valid only for a specific project (accepted costs, project-tuned overrides), it goes to project-customization.**

> **2-overlay load order**: the analysis skill applies `baseline → playbook (general) → project-customization (project-dependent)`. Later overrides earlier (all additive). Both apply scope-matched Learned Entries only.

> **Personalization**: hand-edit the seed sections (§1–Cross-cutting), or accumulate general entries via the learning skill `unity-memory-customization-skill` (Curator) with `layer=playbook`. Delete it and only the baseline remains, with no general know-how.

---

## Learned Entries format spec (shared contract: store tool ↔ Curator skill ↔ analysis skill)

Only the `<!-- entries:start -->` ~ `<!-- entries:end -->` region at the bottom is managed by the store tool(`RecordCustomization`, `layer=playbook`). **The seed sections (§1–Cross-cutting) are never touched by the tool — zero contamination of hand-authored content.** Each entry is a canonical block (format shared by both overlays):

```
<!-- entry:start id=<slug> -->
### [<classification>] <subject>
- **scope**: platform=<iOS|Android|Editor|*>; project=*; type=<typeName|areaName|*>; captureOrigin=<Player|Editor|*>
- **confidence**: <low|medium|high>
- **observations**: <count> (<context1>; <context2>; ...)
- **updated**: <YYYY-MM-DD>

<body — markdown advice. For §1, a threshold override + rationale. Mechanical numbers are never changed (additive).>
<!-- entry:end -->
```

- `classification` (playbook): `§1` threshold / `§2` difficulty / `§3` subsystem / `§4` type group / `cross-cutting`. (**`accepted` does not belong in the playbook** — it is project-dependent, so it is project-customization only.)
- `scope`: all optional. **Set `project` to `*`** — the analysis skill cannot read a project name from a snapshot, so a concrete value never scope-matches; record the originating project in `observations`/`sourceContext` instead. Platform scoping is allowed (e.g. `platform=iOS`).
- `id` (dedup): a slug of `classification + scope + subject`. Same id → merge. `confidence`: the Curator raises it as the insight recurs across snapshots.

---

## 1. Tunable Thresholds — defaults
Per-project overrides go in `project-customization.md`.

### 1.1 Phase A recommendation-priority mapping
The `⭐ top / secondary / defer` decision rule (default):

| Condition | Priority |
|---|---|
| high resident + low difficulty | ⭐ top |
| high resident + mid/high difficulty + analyzable/partially | secondary |
| high resident + opaque (not analyzable) | defer |
| low resident | defer (revisit at stop time) |

- **`high resident` threshold (default)**: **≥ 10%** of total resident. (`low` = below that.)
- Can be overridden per group (e.g. Managed Empty Heap Space is a fragmentation signal, so it has its own rule).

### 1.2 Stop-condition thresholds
The hybrid trigger `(cumulative coverage ≥ X%) OR (next candidate resident% < Y%)`:

- **X (cumulative coverage, default)** = **75%**
- **Y (per-candidate resident%, default)** = **3%**
- `coverage` definition (default): the ratio of **the resident sum of the groups already analyzed** to total resident.
- ⚠️ These defaults are tuning targets to refine against real sample snapshots (empirical). Project-specific values override in `project-customization.md`.

---

## 2. Difficulty / Analyzability ratings (per group)
`analyzability` is derived in the baseline from data availability — here we **only override** it.
`difficulty` is empirical:

| Group / AreaName | Difficulty | Note |
|---|---|---|
| *(not yet populated — add rows here by hand, or let the customization skill accumulate them as you analyze more projects/snapshots)* | | |

---

## 3. Native Subsystem recommendations (AreaName key)
The recommendation bodies for the skill's `native-subsystems-catalog.md` reference, §A (Actionable). The baseline provides only classification/identity; the Recommend (a–d)/Verify below are filled in as an overlay. **Draft — to be reinforced with more Player captures + domain-expert review.**

| AreaName | (a) How to apply | (b) Expected effect | (c) Difficulty | (d) Side effects / risk | Verify |
|---|---|---|---|---|---|
| SerializedFile | Unload unused scenes/assets, split by Addressables unit, `Resources.UnloadUnusedAssets` | loaded-asset residency ↓ | mid | reload cost / hitches | area sum ↓ on recapture |
| AssetBundle | `Unload(true)` unused bundles, release cache right after loading | bundle cache ↓ | low | reload if re-referenced | area sum ↓ |
| Rendering | shader variant stripping, sprite atlas consolidation | ShaderLab share ↓ | mid | build-pipeline change | ShaderLab share ↓ |
| UnsafeUtility | review native plugin/Job temp-allocation patterns | Temp/Persistent pools ↓ | high | native-code change risk | pool size ↓ |
| MemoryPools | reduce renderer/detail instance counts in the scene | batch pools ↓ | mid | rendering-quality trade-off | pool size ↓ |
| UnityWebRequest | `Dispose()` after completion, cap concurrent requests | unreleased handles ↓ | low | — | RootRefCount ↓ |
| Font Engine / TextRendering | dynamic font atlas size/resolution, clean up font fallbacks | font atlas ↓ | mid | glyph quality / regeneration hitches | area sum ↓ |
| Physics / Physics2D | collider/contact counts, broadphase settings | PhysX/Box2D structures ↓ | mid | physics-accuracy trade-off | area sum ↓ |
| Navigation | navmesh resolution/tiles | navmesh ↓ | mid | pathfinding quality | area sum ↓ |
| Input | remove unused devices / action maps | — | low | — | — |
| UIElements | UI hierarchy complexity / rebuild frequency | layout/mesh ↓ | mid | UI structure change | Layout/Renderer share ↓ |
| ParticleSystem / Animation Module | active system count, binding cache | — | mid | — | — |

---

## 4. Improvement Group recommendations (Type group)
Baseline taxonomy and derivation signals are in the skill's `improvement-groups.md` reference. Here we provide the Recommend (a–d)/Verify bodies. **Draft — to be reinforced.**
🟢 = recommendation verifiable from the snapshot, 🔵 = cross-tool referral (the snapshot has no format-level info).

| Type group | (a) How to apply | (b) Effect | (c) Difficulty | (d) Side effects | Verify |
|---|---|---|---|---|---|
| **Texture2D/Sprite** | 🟢 consolidate duplicate instances, unload unused / 🔵 adjust compression format (ASTC etc.), mipmaps, max size in import | large (often the biggest group) | 🟢 low / 🔵 mid | 🔵 quality trade-off | type-group GPU·size ↓ on recapture |
| **RenderTexture** | 🟢 `Release()`/pool temporary RTs, remove unneeded RTs, reduce resolution·count | mid~large | mid | render-pipeline impact | RT count·GPU ↓ |
| **Mesh** | 🟢 consolidate duplicate meshes / 🔵 disable Read/Write, mesh compression, remove unused channels in import | mid | 🟢 low / 🔵 low | 🔵 no runtime edits (R/W off) | type-group size ↓ |
| **Shader/Material** | 🟢 consolidate identical materials / 🔵 shader variant stripping + keyword cleanup in build | mid | 🔵 mid | 🔵 pink if a variant is missing | shader object count·size ↓ |
| **AudioClip** | 🟢 consolidate duplicate clips / 🔵 Compressed In Memory·Vorbis, streaming in import | mid | 🟢 low / 🔵 low | 🔵 decode CPU·latency | type-group size ↓ |
| **AnimationClip** | 🔵 import compression (Optimal/Keyframe Reduction) | small~mid | low | 🔵 motion quality | size ↓ |
| **Font** | 🔵 dynamic font atlas size·glyph limit, static atlas | mid | mid | glyph-regeneration hitches | size ↓ (tied to the Font Engine subsystem) |

### Cross-cutting signal recommendations
- **Duplicate suspects (duplicate detection)** → 🟢 the same asset loaded/duplicated multiple times. Consolidate references, unify via Addressables. Large effect, low difficulty.
- **Persistence (DDOL/Persistent)** → 🟢 objects that survive scene transitions. Check whether they truly need to be permanent; unload explicitly.
- **Abnormal instance count** → 🟢 sign of missing unloads / leaks. Check the create/release balance (tie in the CPU profiler if needed).

---

## Learned Entries (general)
The region where the learning skill accumulates **generalized** entries with `layer=playbook`. **Only the `RecordCustomization` tool manages this region.** See the format spec above.

<!-- entries:start -->
<!-- entries:end -->
