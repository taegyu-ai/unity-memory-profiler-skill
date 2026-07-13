# Native Subsystems Catalog (Hybrid) — Baseline Layer

> **Layer**: This document is the baseline (mechanical/structural classification). Per-area reduction **recommendations**
> are separated into the customization overlay, in `playbook.md` §3 (project-specific overrides in `project-customization.md` §B). This document keeps only classification and identification, not recommendations.

The All Of Memory tab's "Unity Subsystems" row is grouped by `NativeRootReferences.AreaName` (a dynamic string
emitted by the C++ engine at capture time). Since there is no hardcoded list, the skill uses a **Hybrid** strategy:

- **Tool**: dynamically aggregates and returns all AreaNames (preserving coverage).
- **Skill (this catalog)**: classifies known AreaNames by **handling approach** for annotation/recommendation. Areas that are large but not in the catalog are
  handled as `unknown-but-large → "investigate further"`.

> **Status**: the classification structure below is finalized. The recommendation details for A (Actionable) are refined via additional Player captures + domain expert review (ongoing).

> **Source data**: derived from AreaName dumps of real Player/Editor captures across multiple platforms (iOS, macOS). Since this is based on a small number of titles per platform, the Actionable list needs broader validation as more captures are analyzed.

---

## Handling Classification (Finalized)

| Classification | Meaning | Skill Behavior |
|---|---|---|
| **A. Actionable** | Native allocation areas the user can reduce | Included in resident% ranking, with area-specific recommendations |
| **B. Redirect** | Areas better broken down by another view | Redirect to that view (no reduction recommendation is made) |
| **C. Artifact** | The profiler's own measurement overhead | **Excluded** from candidates, flagged only |
| **D. Inform-only** | Fixed/indirect engine areas | Displayed as information, no direct reduction recommendation |
| **E. Editor-only** | Appears only in Editor captures | Editor-capture rejection signal |
| unknown-but-large | Outside the catalog + large resident | "investigate further" (Hybrid dynamic) |

---

## A. Actionable (Identification only — recommendations are in `playbook.md` §3)

| AreaName | Identity (sample object) |
|---|---|
| **SerializedFile** | Loaded serialized files/scenes (globalgamemanagers, *.assets, CAB-*) |
| **AssetBundle** | Bundle loading cache (LoadingCache) |
| **Rendering** | ShaderLab/Mesh·SpriteID generator, GraphicsCaps |
| **UnsafeUtility** | Native Malloc pools (Persistent/Temp/AudioKernel) |
| **MemoryPools** | Renderer batch/intermediate pools |
| **UnityWebRequest** | In-progress/completed but unreleased requests and UploadHandler (463 refs) |
| **Font Engine / TextRendering** | FreeType, GPOS/GSUB, font atlas |
| **Physics / Physics2D** | PhysX/Box2D structures |
| **Navigation** | NavMesh build/debug |
| **Input** | Input System state |
| **UIElements** | UI Toolkit layout/mesh (runtime UI) |
| **ParticleSystem / Animation Module** | Particle/animation managers and bindings |

> Per-area Recommend(a-d)/Verify entries are filled in by the customization overlay (`playbook.md` §3). The Actionable **membership determination** belongs to the baseline (structural residual classification).

## B. Redirect

| AreaName | Redirect target |
|---|---|
| **Objects** | Total native UnityEngine.Object (349MB/169051 refs) = broken down by type in the Unity Objects view. → Redirect to Phase A's Top Unity Objects / Unity Objects tab |

## C. Artifact (excluded from candidates)

`Profiling`, `MemoryProfiling`, `ProfilerUnsafeUtility`, `SymbolCache` — the profiler's own overhead.
In Editor captures, `Profiling` can balloon up to 902MB. Excluded from resident ranking and labeled only as a "measurement artifact".

## D. Inform-only

| AreaName | Notes |
|---|---|
| **System** (ExecutableAndDlls) | Executable file size. Already reported separately under the ExecutablesAndMapped bucket (source special-case). |
| **Managers** | Fixed engine manager overhead |
| **PersistentManager.Remapper** | Serialization remap table — proportional to the number of loaded objects (indirect) |
| **Job System** | BackgroundJobQueue |
| **File System / FileSystem** | VFS |

## E. Editor-only (Item 6 rejection signal)

**True editor-only** (absent in Player): `Editor`, `EditorUtilities`, `Asset Database`, `PackageManager`, `Menus`,
`Undo`, `Services` (ProcessService), and `*Editor Module` (TextRenderingEditor/PhysicsEditor/ClothEditor/VREditor Module).

> ⚠️ **Pitfall (verified 2026-06-14)**: `Insights Module` and `UnityConsent` **also exist in standalone Player** → not editor-only (classified as InformOnly). Including these in the editor signature would misclassify OSXPlayer captures as Editor.

**The authoritative basis for Editor/Player determination is `MetaData.TargetInfo.RuntimePlatform`** (clearly distinguishes OSXEditor vs OSXPlayer). The AreaName signatures above are **only a fallback** for older snapshots that lack platform information, and do not override the Player determination.
