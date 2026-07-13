# v1 Improvement Groups Taxonomy — Baseline Layer

Concrete definition of the v1 improvement-group taxonomy. This is the **baseline** (mechanical grouping + derivable signals);
the **recommendation body** per area is separated into the customization overlay (`playbook.md` §3/§4; project-specific overrides in `project-customization.md` §B).

> **Key boundary**: the snapshot only exposes, per object, **size (Native/Managed/GPU), type, name, persistence flags, refcount, and duplicates**.
> **Format/compression/mipmap/read-write/load type are not available** → those recommendations are a cross-tool referral.
> In other words, v1 group analysis covers "how big / how many / duplicated / unreleasable (GPU·persistent)", while "which format to reduce it with" is deferred elsewhere.

## Grouping Dimensions (3)

### D1. Unity Object Type Groups (primary)
Based on `NativeObjects.NativeTypeArrayIndex`. Phase A Top Unity Objects uses this dimension.
Signals derived per type: total size, **Native/Managed/GPU breakdown**, instance count, suspected duplicates, persistence ratio.

### D2. Native Subsystem Groups
Based on `NativeRootReferences.AreaName` → `native-subsystems-catalog.md` (defined separately).

### D3. Managed Structure Groups
Based on `ManagedHeapSection`/`ManagedObject`. Empty Heap Space (fragmentation), VM/static fields, objects per managed type.

## Cross-cutting Signals (common to D1 type groups)
| Signal | Derivation | Nature |
|---|---|---|
| **Suspected duplicates** | Built-in duplicate detection (identical type+name+size) | 🟢 snapshot-derivable, high value |
| **Persistence** | `ObjectFlags.IsDontDestroyOnLoad/IsPersistent` | 🟢 identifies objects that won't be released |
| **GPU share** | `GpuSize` breakdown | 🟢 graphics memory pressure |
| **Abnormal instance count** | Object count in group | 🟢 sign of a leak/missing unload |
| **Large single instance** | Largest object in the group | 🟢 |

## v1 Key Type Groups + Recommendation Source Classification
Recommendation content lives in the playbook overlay §4. Here we only cover **what is derived and where it's referred to** (baseline).

| Type Group | baseline derived signals | 🟢 snapshot recommendation | 🔵 cross-tool referral |
|---|---|---|---|
| **Texture2D / Sprite** | size, GPU, count, duplicates | remove duplicates, unload unused | import: format/compression/mipmap/resolution |
| **RenderTexture** | GPU, count | release/pool temporary RTs, resolution/count | — (mostly created at runtime) |
| **Mesh** | size, GPU, count, duplicates | remove duplicates | import: read/write, compression, vertex format |
| **Shader / Material** | count, duplicates (≈ variant count) | remove duplicate materials | build: shader variant stripping |
| **AudioClip** | size, count, duplicates | remove duplicates | import: load type/compression |
| **AnimationClip** | size, count | — | import: compression |
| **Font** | size | (linked to Font Engine subsystem) | dynamic atlas settings |
| **Other types** | size, count, duplicates | duplicates/unload | branch by type |

## Managed Structure Groups (D3) — mostly cross-tool
| Group | Handling |
|---|---|
| **Empty Heap Space (fragmentation)** | Cannot analyze dynamic allocation from a single snapshot → refer to CPU Profiler GC Allocation analysis |
| **VM / static fields** | Display info (TypeDescriptions.StaticFieldBytes) |
| **Managed Objects by type** | Size per type (managed crawl) |

> ⚠️ Recommendation content/difficulty/effect are supplemented in the customization overlay (additional Player captures + domain expert). The baseline structure is finalized.
