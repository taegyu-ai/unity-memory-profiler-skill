// Production Unity AI Assistant tools for the Memory Profiler skill (ADR 0005).
//
// This is the bridge migration of the dev PoC
// (com.unity.memoryprofiler/.../Editor/_MemoryProfilerSkillTools/MemoryProfilerSkillTools.cs).
// The 8 tools' computation is unchanged from the verified PoC; only four things differ:
//   ⓐ location   — this lives in a bridge asmdef named `Unity.MemoryProfiler.Editor.Tests`,
//                  which inherits the package's existing IVT (MemoryProfilerWindow.cs:7) and so
//                  reaches CachedSnapshot / *ModelBuilder / SnapshotDataService internals directly.
//   ⓑ return     — Phase A tools return typed POCOs (the Assistant serializes them) instead of
//                  hand-built JSON strings. Phase B already returned POCOs.
//   ⓒ entry      — analysis targets the snapshot ALREADY loaded in the Memory Profiler window
//                  (MemoryProfilerWindow.SnapshotDataService.Base) by default; a file path is a fallback.
//   ⓓ decoration — each tool is a [AgentTool] public static method; each parameter is [ToolParameter].
//
// POCOs use [Serializable] public fields (Unity JsonUtility convention). If the Assistant's serializer
// requires public *properties* instead, convert them to auto-properties — the bodies (object
// initializers, `++`) are unchanged by that conversion. (Verified 2026-06-15: field
// serialization works as-is — no conversion needed.)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.AI.Assistant.FunctionCalling;
using Unity.MemoryProfiler.Editor.Format;
using Unity.MemoryProfiler.Editor.Format.QueriedSnapshot;
using Unity.MemoryProfiler.Editor.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.MemoryProfiler.Editor.AgentTools
{
    static class MemoryProfilerAgentTools
    {
        // The snapshot under analysis. Set by Initialize; shared by all other tools.
        static CachedSnapshot s_Snapshot;
        // True only when WE built s_Snapshot (file fallback) and must Dispose it. A snapshot reused
        // from the Memory Profiler window is owned by the window — never Dispose it.
        static bool s_OwnsSnapshot;

        // Resident memory is only meaningful when the capture has system memory regions info.
        // Many Android/console captures lack it → report resident as null rather than misleading 0.
        static bool ResidentAvailable => s_Snapshot != null && s_Snapshot.HasSystemMemoryRegionsInfo;

        // Comparison pair (step 8). A = baseline, B = candidate/later. Independent of s_Snapshot:
        // Initialize and InitializeComparison never clear each other's state. Same ownership rule as
        // s_OwnsSnapshot — only dispose snapshots WE built from a file; never a window-owned one.
        static CachedSnapshot s_SnapshotA;
        static CachedSnapshot s_SnapshotB;
        static bool s_OwnsA;
        static bool s_OwnsB;
        static bool ResidentAvailableA => s_SnapshotA != null && s_SnapshotA.HasSystemMemoryRegionsInfo;
        static bool ResidentAvailableB => s_SnapshotB != null && s_SnapshotB.HasSystemMemoryRegionsInfo;
        static bool ResidentAvailableBoth => ResidentAvailableA && ResidentAvailableB;

        // Which comparison snapshot the single-snapshot drill tools currently point at: "A" | "B" | null.
        // null = not in comparison mode (s_Snapshot is a normal single snapshot). Echoed in every drill result
        // so the caller can tell which snapshot a drill targeted without inferring it from object counts.
        static string s_ComparisonDrillTarget;

        // ---------------------------------------------------------------- Initialize

        [AgentTool(
            "Loads the Unity memory snapshot to analyze and reports its metadata, capture origin " +
            "(Player vs Editor) and data availability gates. Call this first. With no filePath it reuses " +
            "the snapshot already open in the Memory Profiler window; pass a .snap path to load a file instead.",
            "Unity.MemoryProfiler.Initialize")]
        public static InitializeResult Initialize(
            [ToolParameter("Optional absolute path to a .snap file. Omit to analyze the snapshot already loaded in the Memory Profiler window.")]
            string filePath = null)
        {
            UnloadInternal();

            var cs = ResolveSnapshot(filePath, out var owns, out var source, out var resolveError);
            if (cs == null)
                return new InitializeResult { error = resolveError, source = source };

            s_Snapshot = cs;
            s_OwnsSnapshot = owns;

            ResolveCaptureOrigin(cs, out var platform, out var captureOrigin, out var captureSignals);

            return new InitializeResult
            {
                source = source,
                filePath = filePath,
                snapshotName = SnapshotNameOf(cs),
                productName = ProductNameOf(cs),
                runtimePlatform = platform,
                captureOrigin = captureOrigin,
                captureOriginSignals = captureSignals,
                availability = new SnapshotAvailability
                {
                    isSupportedFormat = cs.HasTargetAndMemoryInfo && cs.HasMemoryLabelSizesAndGCHeapTypes,
                    hasSystemMemoryRegionsInfo = cs.HasSystemMemoryRegionsInfo,
                    hasSystemMemoryResidentPages = cs.HasSystemMemoryResidentPages,
                    hasManagedHeap = cs.ManagedHeapSections.Count > 0,
                    hasNativeObjects = cs.NativeObjects != null && cs.NativeObjects.Count > 0,
                    hasAllocationCallstacks = cs.NativeAllocationSites != null && cs.NativeAllocationSites.Count > 0,
                },
            };
        }

        // ---------------------------------------------------------------- Loaded-mode detection

        [AgentTool(
            "Reports what the Memory Profiler window currently has loaded WITHOUT loading or changing anything: " +
            "a single snapshot, a comparison pair (Compare mode), or nothing. Call this FIRST when the user did " +
            "NOT specify single vs comparison and did NOT give .snap file paths — then route to Initialize " +
            "(loadedMode 'single') or InitializeComparison (loadedMode 'comparison') to match what is already open.",
            "Unity.MemoryProfiler.GetLoadedSnapshotState")]
        public static LoadedSnapshotState GetLoadedSnapshotState()
        {
            var res = new LoadedSnapshotState { loadedMode = "none" };

            var windows = Resources.FindObjectsOfTypeAll<MemoryProfilerWindow>();
            var service = (windows != null && windows.Length > 0) ? windows[0].SnapshotDataService : null;
            if (service == null)
            {
                res.note = "The Memory Profiler window is not open. Open it and load a capture, or call Initialize with a filePath.";
                return res;
            }
            res.windowOpen = true;
            res.compareMode = service.CompareMode;

            bool baseValid = service.Base != null && service.Base.Valid;
            bool comparedValid = service.Compared != null && service.Compared.Valid;

            if (service.CompareMode && baseValid && comparedValid)
            {
                res.loadedMode = "comparison";
                ResolveCaptureOrigin(service.Base, out var pa, out _, out _);
                ResolveCaptureOrigin(service.Compared, out var pb, out _, out _);
                res.platformA = pa; res.platformB = pb;
            }
            else if (baseValid)
            {
                res.loadedMode = "single";
                ResolveCaptureOrigin(service.Base, out var p, out _, out _);
                res.platform = p;
                if (service.CompareMode)
                    res.note = "Compare mode is on but only one snapshot is loaded — treated as single.";
            }
            else
            {
                res.note = "No valid snapshot is loaded. Open a capture in the Memory Profiler window, or call Initialize with a filePath.";
            }
            return res;
        }

        // ---------------------------------------------------------------- Available-snapshot listing

        [AgentTool(
            "Lists the .snap capture files on disk WITHOUT loading any of them, so you can offer the user a " +
            "capture to analyze when nothing is loaded in the Memory Profiler window. Call this when Initialize " +
            "reports no snapshot and the user gave no filePath. Candidates are sorted most-useful-first (captures " +
            "whose filename matches the current project, then most-recently-modified). Present them and, once the " +
            "user picks one, call Initialize with that candidate's filePath.",
            "Unity.MemoryProfiler.ListAvailableSnapshots")]
        public static AvailableSnapshots ListAvailableSnapshots()
        {
            const int k_MaxCandidates = 20;
            var productName = Application.productName;
            var dir = MemoryProfilerSettings.AbsoluteMemorySnapshotStoragePath;

            var res = new AvailableSnapshots
            {
                captureDirectory = dir,
                productName = productName,
                snapshots = new List<SnapshotCandidate>(),
            };

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                res.directoryExists = false;
                res.note = $"No capture directory found at '{dir}'. Take a snapshot from the Memory Profiler " +
                           "window (Capture button), or call Initialize with an absolute .snap path.";
                return res;
            }
            res.directoryExists = true;

            var candidates = Directory.GetFiles(dir, "*.snap", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    var name = info.Name;
                    return new SnapshotCandidate
                    {
                        fileName = name,
                        filePath = info.FullName,
                        sizeBytes = info.Length,
                        lastModifiedUtc = info.LastWriteTimeUtc.ToString("o"),
                        matchesCurrentProject = !string.IsNullOrEmpty(productName)
                            && name.IndexOf(productName, StringComparison.OrdinalIgnoreCase) >= 0,
                    };
                })
                // Project-matching captures first, then most-recently-modified.
                .OrderByDescending(c => c.matchesCurrentProject)
                .ThenByDescending(c => c.lastModifiedUtc, StringComparer.Ordinal)
                .ToList();

            res.totalCount = candidates.Count;
            res.snapshots = candidates.Take(k_MaxCandidates).ToList();

            if (candidates.Count == 0)
                res.note = $"The capture directory '{dir}' contains no .snap files. Take a snapshot from the " +
                           "Memory Profiler window (Capture button), or call Initialize with an absolute .snap path.";
            else if (candidates.Count > k_MaxCandidates)
                res.note = $"Showing the {k_MaxCandidates} most relevant of {candidates.Count} captures.";

            return res;
        }

        // Loaded-snapshot priority, file fallback (ADR 0005).
        //  - filePath empty → reuse MemoryProfilerWindow.SnapshotDataService.Base (already crawled; not owned).
        //  - filePath given → load it ourselves via FileReader + PostProcess (owned; verified PoC path).
        static CachedSnapshot ResolveSnapshot(string filePath, out bool owns, out string source, out string error)
        {
            owns = false; source = null; error = null;

            if (string.IsNullOrEmpty(filePath))
            {
                var windows = Resources.FindObjectsOfTypeAll<MemoryProfilerWindow>();
                var service = (windows != null && windows.Length > 0) ? windows[0].SnapshotDataService : null;
                var loaded = service?.Base;
                if (loaded != null && loaded.Valid)
                {
                    source = "loaded";
                    return loaded; // owned by the window — do NOT dispose.
                }
                error = "No snapshot is loaded in the Memory Profiler window. Call ListAvailableSnapshots to see captures on disk, open a capture in the window, or call Initialize with a filePath.";
                return null;
            }

            var reader = new FileReader();
            var err = reader.Open(filePath);
            if (err != ReadError.Success)
            {
                reader.Dispose();
                error = $"open failed: {err}";
                source = "file";
                return null;
            }

            // Mirror SnapshotDataService.LoadSnapshot: build + crawl (managed crawl always).
            var cs = new CachedSnapshot(reader);
            var processing = cs.PostProcess(true);
            processing.MoveNext();
            while (processing.MoveNext()) { }

            owns = true;
            source = "file";
            return cs;
        }

        static bool HasEditorAreaNameSignature(CachedSnapshot cs)
        {
            var roots = cs.NativeRootReferences;
            for (long i = 0; i < roots.Count; i++)
            {
                if (ClassifyArea(roots.AreaName[i]) == "EditorOnly")
                    return true;
            }
            return false;
        }

        // Capture file name without extension (the name the user sees in the Memory Profiler window's
        // snapshot list). FullPath is populated for both window-loaded and file-loaded snapshots.
        static string SnapshotNameOf(CachedSnapshot cs)
            => string.IsNullOrEmpty(cs?.FullPath) ? null : Path.GetFileNameWithoutExtension(cs.FullPath);

        // The captured project's product name (MetaData.ProductName = TargetInfo.ProductName), used for
        // project scope-matching. This is the SNAPSHOT's project — not the Editor's Application.productName —
        // so it stays correct when analyzing a capture taken from a different project. Legacy captures with no
        // ProfileTargetInfo get "Unknown Project" (mirrors MetaData.k_UnknownProductName); normalize that to
        // null so the analysis skill drops the project scope dimension instead of matching a bogus name.
        static string ProductNameOf(CachedSnapshot cs)
        {
            var pn = cs?.MetaData.ProductName;
            return string.IsNullOrWhiteSpace(pn) || pn == "Unknown Project" ? null : pn;
        }

        // Capture origin (Player vs Editor) + platform string, in priority order. Extracted from
        // Initialize so InitializeComparison can resolve both snapshots' origins identically.
        //   1. TargetInfo.RuntimePlatform — authoritative for Editor vs Player (modern captures).
        //   2. Legacy MetaData.Platform/IsEditorCapture — for old captures without ProfileTargetInfo
        //      that still recorded a platform string. DeserializeLegacyMetadata sets
        //      IsEditorCapture = Platform.Contains("Editor") only when a platform string was present;
        //      otherwise Platform stays "Unknown Platform" and IsEditorCapture defaults to false
        //      (meaningless) — so it is only trusted when the platform string was actually recorded.
        //   3. AreaName signature — last-resort heuristic when no platform metadata exists at all.
        //      It must NOT override a known platform (some non-editor areas like Insights/Consent
        //      would otherwise cause false "Editor" positives on standalone Player captures).
        static void ResolveCaptureOrigin(CachedSnapshot cs, out string platform, out string captureOrigin,
            out CaptureOriginSignals signals)
        {
            var meta = cs.MetaData;
            platform = "Unknown";
            bool platformIsEditor = false;
            if (meta.TargetInfo.HasValue)
            {
                var rp = meta.TargetInfo.Value.RuntimePlatform;
                platform = rp.ToString();
                platformIsEditor = rp == RuntimePlatform.WindowsEditor
                    || rp == RuntimePlatform.OSXEditor
                    || rp == RuntimePlatform.LinuxEditor;
            }
            // "Unknown Platform" is MetaData.k_UnknownPlatform (the legacy no-metadata default).
            bool legacyPlatformKnown = !meta.TargetInfo.HasValue
                && !string.IsNullOrEmpty(meta.Platform)
                && meta.Platform != "Unknown Platform";
            bool isEditorCaptureFlag = meta.IsEditorCapture;
            bool areaNameEditorSignature = HasEditorAreaNameSignature(cs);

            if (meta.TargetInfo.HasValue)
                captureOrigin = platformIsEditor ? "Editor" : "Player";       // RuntimePlatform authoritative
            else if (legacyPlatformKnown)
            {
                platform = meta.Platform;
                captureOrigin = isEditorCaptureFlag ? "Editor" : "Player";    // legacy platform string
            }
            else
                captureOrigin = areaNameEditorSignature ? "Editor" : "Uncertain"; // AreaName heuristic

            signals = new CaptureOriginSignals
            {
                platformIsEditor = platformIsEditor,
                areaNameEditorSignature = areaNameEditorSignature,
                isEditorCaptureFlag = isEditorCaptureFlag,
                legacyPlatformKnown = legacyPlatformKnown,
            };
        }

        // ---------------------------------------------------------------- Phase A: Overview

        [AgentTool(
            "Summary-tab overview of the loaded snapshot: the 5-bucket Allocated distribution, the managed " +
            "heap breakdown, the resident total, and a fragmentation (empty heap) signal. Call once after Initialize.",
            "Unity.MemoryProfiler.GetMemoryOverview")]
        public static MemoryOverview GetMemoryOverview()
        {
            if (s_Snapshot == null || !s_Snapshot.Valid) return null;
            return BuildOverviewFor(s_Snapshot, ResidentAvailable);
        }

        // Overview computation for an arbitrary snapshot (used by GetMemoryOverview and the comparison
        // overview, which subtracts two of these). residentAvailable is the caller's gate for that snapshot.
        static MemoryOverview BuildOverviewFor(CachedSnapshot cs, bool residentAvailable)
        {
            var allocated = new AllMemorySummaryModelBuilder(cs, null).Build();
            var managed = new ManagedMemorySummaryModelBuilder(cs, null).Build();
            var resident = new ResidentMemorySummaryModelBuilder(cs, null).Build();

            var result = new MemoryOverview
            {
                residentAvailable = residentAvailable,
                allocatedDistribution = new List<AllocatedBucket>(),
                managedBreakdown = new List<ManagedBucket>(),
            };

            // Allocated 5-bucket (+ platform rows)
            for (int i = 0; i < allocated.Rows.Count; i++)
            {
                var r = allocated.Rows[i];
                bool rowResidentNA = !residentAvailable || r.ResidentSizeUnavailable;
                result.allocatedDistribution.Add(new AllocatedBucket
                {
                    name = r.Name,
                    committed = r.BaseSize.Committed,
                    resident = rowResidentNA ? (ulong?)null : r.BaseSize.Resident,
                    residentUnavailable = rowResidentNA,
                    categoryId = r.CategoryId.ToString(),
                });
            }
            result.totalAllocatedCommitted = allocated.TotalA;

            // Managed breakdown (order: VM, Objects, EmptyHeapSpace)
            ulong managedTotal = 0, emptyHeap = 0;
            for (int i = 0; i < managed.Rows.Count; i++)
            {
                var r = managed.Rows[i];
                managedTotal += r.BaseSize.Committed;
                result.managedBreakdown.Add(new ManagedBucket
                {
                    name = r.Name,
                    committed = r.BaseSize.Committed,
                    resident = residentAvailable ? (ulong?)r.BaseSize.Resident : null,
                });
            }
            // EmptyHeapSpace = last managed row (builder order: VM, Objects, FreeHeap). See source-access-map §2.
            if (managed.Rows.Count > 0) emptyHeap = managed.Rows[managed.Rows.Count - 1].BaseSize.Committed;

            // Resident total
            ulong residentBytes = resident.Rows.Count > 0 ? resident.Rows[0].BaseSize.Resident : 0;
            result.residentTotal = new ResidentTotal
            {
                available = residentAvailable,
                committed = residentAvailable ? (ulong?)resident.TotalA : null,
                resident = residentAvailable ? (ulong?)residentBytes : null,
            };

            // Fragmentation signal
            result.fragmentation = new Fragmentation
            {
                emptyHeapSpace = emptyHeap,
                emptyHeapRatioOfManaged = managedTotal > 0 ? (double)emptyHeap / managedTotal : 0.0,
            };

            return result;
        }

        // ---------------------------------------------------------------- Phase A: Top Unity Objects

        [AgentTool(
            "Top Unity object types by memory, grouped by native type and sorted by resident share " +
            "(committed when resident is unavailable). The primary ranked-list material for the survey.",
            "Unity.MemoryProfiler.GetTopUnityObjectCategories")]
        public static TopUnityObjectCategories GetTopUnityObjectCategories(
            [ToolParameter("How many top type groups to return (default 20).")]
            int topN = 20)
        {
            if (s_Snapshot == null || !s_Snapshot.Valid) return null;
            var cs = s_Snapshot;

            // Aggregate ProcessedNativeRoots (NativeObject roots) by NativeTypeArrayIndex.
            // Same caches as UnityObjectsModelBuilder.BuildNativeObjectIndexToSize (source-access-map §4).
            var byType = new Dictionary<int, TypeAgg>();
            var roots = cs.ProcessedNativeRoots;
            for (long i = 0; i < roots.Count; i++)
            {
                var idx = roots.Data[i].NativeObjectOrRootIndex;
                if (idx.Id != CachedSnapshot.SourceIndex.SourceId.NativeObject)
                    continue;
                var sizes = roots.Data[i].AccumulatedRootSizes;
                int typeIndex = cs.NativeObjects.NativeTypeArrayIndex[idx.Index];

                if (!byType.TryGetValue(typeIndex, out var agg)) agg = new TypeAgg();
                agg.Native += sizes.NativeSize;
                agg.Managed += sizes.ManagedSize;
                agg.Gpu += sizes.GfxSize;
                agg.Count++;
                byType[typeIndex] = agg;
            }

            var list = new List<KeyValuePair<int, TypeAgg>>(byType);
            list.Sort((a, b) =>
            {
                ulong ra = a.Value.TotalResident, rb = b.Value.TotalResident;
                if (ra != rb) return rb.CompareTo(ra);
                return b.Value.TotalCommitted.CompareTo(a.Value.TotalCommitted);
            });

            var snapTotal = roots.TotalMemoryInSnapshot;
            var result = new TopUnityObjectCategories
            {
                residentAvailable = ResidentAvailable,
                totalSnapshotCommitted = snapTotal.Committed,
                totalSnapshotResident = ResidentAvailable ? (ulong?)snapTotal.Resident : null,
                groups = new List<TypeGroup>(),
            };

            int n = 0;
            foreach (var kv in list)
            {
                if (n >= topN) break;
                var a = kv.Value;
                result.groups.Add(new TypeGroup
                {
                    typeName = cs.NativeTypes.TypeName[kv.Key],
                    nativeCommitted = a.Native.Committed,
                    managedCommitted = a.Managed.Committed,
                    gpuCommitted = a.Gpu.Committed,
                    totalCommitted = a.TotalCommitted,
                    totalResident = ResidentAvailable ? (ulong?)a.TotalResident : null,
                    objectCount = a.Count,
                });
                n++;
            }
            return result;
        }

        struct TypeAgg
        {
            public MemorySize Native, Managed, Gpu;
            public long Count;
            public ulong TotalCommitted => Native.Committed + Managed.Committed + Gpu.Committed;
            public ulong TotalResident => Native.Resident + Managed.Resident + Gpu.Resident;
        }

        // ---------------------------------------------------------------- Phase A: Native Subsystems

        [AgentTool(
            "All native subsystems (NativeRootReferences grouped by AreaName) with committed size, root-ref " +
            "count, sample object names and a baseline classification (Actionable/Redirect/Artifact/InformOnly/EditorOnly/Unknown).",
            "Unity.MemoryProfiler.GetNativeSubsystems")]
        public static NativeSubsystems GetNativeSubsystems()
        {
            if (s_Snapshot == null || !s_Snapshot.Valid) return null;
            var cs = s_Snapshot;
            var roots = cs.NativeRootReferences;

            var totalByArea = new Dictionary<string, ulong>();
            var countByArea = new Dictionary<string, long>();
            var sampleByArea = new Dictionary<string, List<string>>();
            for (long i = 0; i < roots.Count; i++)
            {
                var area = roots.AreaName[i];
                if (string.IsNullOrEmpty(area)) area = "<empty>";
                totalByArea.TryGetValue(area, out var t); totalByArea[area] = t + roots.AccumulatedSize[i];
                countByArea.TryGetValue(area, out var c); countByArea[area] = c + 1;
                if (!sampleByArea.TryGetValue(area, out var s)) sampleByArea[area] = s = new List<string>();
                var on = roots.ObjectName[i];
                if (!string.IsNullOrEmpty(on) && s.Count < 5 && !s.Contains(on)) s.Add(on);
            }

            var areas = new List<string>(totalByArea.Keys);
            areas.Sort((a, b) => totalByArea[b].CompareTo(totalByArea[a]));

            var result = new NativeSubsystems { subsystems = new List<NativeSubsystem>() };
            for (int i = 0; i < areas.Count; i++)
            {
                var area = areas[i];
                result.subsystems.Add(new NativeSubsystem
                {
                    areaName = area,
                    committed = totalByArea[area],
                    rootRefCount = countByArea[area],
                    classification = ClassifyArea(area),
                    sampleObjectNames = sampleByArea[area],
                });
            }
            return result;
        }

        // Mirrors references/native-subsystems-catalog.md (baseline classification).
        static readonly HashSet<string> k_Artifact = new HashSet<string> { "Profiling", "MemoryProfiling", "ProfilerUnsafeUtility", "SymbolCache", "Profiler" };
        static readonly HashSet<string> k_Redirect = new HashSet<string> { "Objects" };
        static readonly HashSet<string> k_InformOnly = new HashSet<string> { "System", "Managers", "PersistentManager.Remapper", "Job System", "File System", "FileSystem", "Log System", "Insights Module", "UnityConsent", "Serialization" };
        // Only DEFINITIVE editor-only areas. Insights Module / UnityConsent appear in standalone Players too → NOT here.
        static readonly HashSet<string> k_EditorOnly = new HashSet<string> { "Editor", "EditorUtilities", "Asset Database", "PackageManager", "Menus", "Undo", "Services" };
        static readonly HashSet<string> k_Actionable = new HashSet<string>
        {
            "SerializedFile", "AssetBundle", "Rendering", "MemoryPools", "UnsafeUtility", "UnityWebRequest",
            "Font Engine", "TextRendering", "Text Engine", "Physics", "Physics2D", "Physics Module", "Physics2D Module",
            "Navigation", "Input", "UIElements", "ParticleSystem Module", "Animation Module", "Animation", "Audio Module"
        };

        static string ClassifyArea(string area)
        {
            if (string.IsNullOrEmpty(area)) return "Unknown";
            if (k_EditorOnly.Contains(area) || area.EndsWith("Editor Module") || area.EndsWith("Editor")) return "EditorOnly";
            if (k_Artifact.Contains(area)) return "Artifact";
            if (k_Redirect.Contains(area)) return "Redirect";
            if (k_InformOnly.Contains(area)) return "InformOnly";
            if (k_Actionable.Contains(area)) return "Actionable";
            return "Unknown";
        }

        // ================================================================ Phase B (group drill)

        // ---------------------------------------------------------------- B1: Unity Object Type detail

        // Per-object detail for a single Unity object type group (source-access-map §4/§4b).
        // Wraps UnityObjectsModelBuilder.Build with an exact type-name filter, then reads per-object
        // flags/refcount/instanceId from cs.NativeObjects via each leaf's SourceIndex.
        [AgentTool(
            "Per-object detail for one Unity object type group (use a typeName from GetTopUnityObjectCategories): " +
            "each object's native/managed/gpu size, flags (persistent/DontDestroyOnLoad), ref count, and the " +
            "nativeObjectIndex used by GetObjectRetentionPath, plus a group summary.",
            "Unity.MemoryProfiler.GetUnityObjectTypeDetail")]
        public static UnityObjectTypeDetail GetUnityObjectTypeDetail(
            [ToolParameter("Exact Unity object type name to drill (e.g. \"Texture2D\", \"ParticleSystem\").")]
            string typeName,
            [ToolParameter("How many objects to return, largest first (default 50).")]
            int topN = 50)
        {
            if (s_Snapshot == null || !s_Snapshot.Valid) return null;
            var cs = s_Snapshot;

            var args = new UnityObjectsModelBuilder.BuildArgs(
                unityObjectTypeNameFilter: MatchesTextFilter.Create(typeName));
            var model = new UnityObjectsModelBuilder().Build(cs, args);

            var leaves = new List<UnityObjectsModel.ItemData>();
            CollectLeaves(model.RootNodes, leaves);

            // duplicate-suspect count: objects sharing (name, totalCommitted) within this type.
            var dupKeyCount = new Dictionary<string, int>();
            foreach (var d in leaves)
            {
                var key = d.Name + "|" + (d.NativeSize.Committed + d.ManagedSize.Committed);
                dupKeyCount.TryGetValue(key, out var c); dupKeyCount[key] = c + 1;
            }

            leaves.Sort((a, b) =>
            {
                ulong ra = a.TotalSize.Resident, rb = b.TotalSize.Resident;
                if (ResidentAvailable && ra != rb) return rb.CompareTo(ra);
                return b.TotalSize.Committed.CompareTo(a.TotalSize.Committed);
            });

            var result = new UnityObjectTypeDetail
            {
                typeName = typeName,
                residentAvailable = ResidentAvailable,
                comparisonDrillTarget = s_ComparisonDrillTarget,
                summary = new ObjectDetailSummary(),
                objects = new List<ObjectDetail>(),
            };

            foreach (var d in leaves)
            {
                result.summary.objectCount++;
                result.summary.totalNative += d.NativeSize.Committed;
                result.summary.totalManaged += d.ManagedSize.Committed;
                result.summary.totalGpu += d.GpuSize.Committed;

                long idx = d.Source.Index;
                bool isNativeObj = d.Source.Id == CachedSnapshot.SourceIndex.SourceId.NativeObject;
                var flags = isNativeObj ? cs.NativeObjects.Flags[idx] : default;
                bool persistent = (flags & ObjectFlags.IsPersistent) != 0;
                if (persistent) result.summary.persistentCount++;

                var key = d.Name + "|" + (d.NativeSize.Committed + d.ManagedSize.Committed);
                if (dupKeyCount.TryGetValue(key, out var c) && c > 1) result.summary.duplicateSuspectCount++;

                if (result.objects.Count >= topN) continue;

                result.objects.Add(new ObjectDetail
                {
                    name = d.Name,
                    nativeCommitted = d.NativeSize.Committed,
                    managedCommitted = d.ManagedSize.Committed,
                    gpuCommitted = d.GpuSize.Committed,
                    totalCommitted = d.TotalSize.Committed,
                    totalResident = ResidentAvailable ? (ulong?)d.TotalSize.Resident : null,
                    instanceId = isNativeObj ? cs.NativeObjects.InstanceId[idx].ToString() : null,
                    nativeObjectIndex = isNativeObj ? idx : -1,
                    isDontDestroyOnLoad = (flags & ObjectFlags.IsDontDestroyOnLoad) != 0,
                    isPersistent = persistent,
                    refCount = isNativeObj ? cs.NativeObjects.RefCount[idx] : 0,
                });
            }
            return result;
        }

        // ---------------------------------------------------------------- B2: Potential duplicates

        // Cross-cutting duplicate-suspect groups (same type+name+size). Wraps the package's built-in
        // potential-duplicates filter (UnityObjectsModelBuilder.cs:456) by setting the BuildArgs flag.
        [AgentTool(
            "Cross-cutting duplicate-suspect groups (objects of the same type, name and size): each group's " +
            "per-instance size, copy count and wasted-estimate (instanceSize × (count-1)), sorted by waste. " +
            "Optionally restrict to one typeName.",
            "Unity.MemoryProfiler.GetPotentialDuplicates")]
        public static PotentialDuplicates GetPotentialDuplicates(
            [ToolParameter("Optional Unity object type name to restrict to. Omit to scan all types.")]
            string typeName = null)
        {
            if (s_Snapshot == null || !s_Snapshot.Valid) return null;
            var cs = s_Snapshot;

            var args = new UnityObjectsModelBuilder.BuildArgs(
                unityObjectTypeNameFilter: typeName != null ? MatchesTextFilter.Create(typeName) : null,
                potentialDuplicatesFilter: true); // do NOT combine with disambiguateByInstanceId (assert :465)
            var model = new UnityObjectsModelBuilder().Build(cs, args);

            var result = new PotentialDuplicates { typeNameFilter = typeName, comparisonDrillTarget = s_ComparisonDrillTarget, duplicateGroups = new List<DuplicateGroup>() };

            // tree: typeGroup -> duplicate-set node (ChildCount = N, sizes summed across the N copies).
            foreach (var typeGroup in model.RootNodes)
            {
                var typeGroupName = typeGroup.data.Name;
                if (!typeGroup.hasChildren) continue;
                foreach (var dup in typeGroup.children)
                {
                    int count = dup.data.ChildCount;
                    if (count <= 1) continue;
                    ulong summed = dup.data.NativeSize.Committed + dup.data.ManagedSize.Committed;
                    ulong instanceSize = summed / (ulong)count;
                    result.duplicateGroups.Add(new DuplicateGroup
                    {
                        typeName = typeGroupName,
                        name = dup.data.Name,
                        instanceSize = instanceSize,
                        count = count,
                        wastedEstimate = instanceSize * (ulong)(count - 1),
                    });
                }
            }

            result.duplicateGroups.Sort((a, b) => b.wastedEstimate.CompareTo(a.wastedEstimate));
            return result;
        }

        // ---------------------------------------------------------------- B3: Native subsystem detail

        // Individual native root references within one AreaName (source-access-map §5).
        // Resident is joined from ProcessedNativeRoots.Data[i] (1:1 by root index) when available.
        [AgentTool(
            "Per-root detail for one native subsystem (use an areaName from GetNativeSubsystems): each root " +
            "reference's object name, committed/resident size and rootId, plus a summary. Resident is joined " +
            "from ProcessedNativeRoots when the index mapping is 1:1.",
            "Unity.MemoryProfiler.GetNativeSubsystemDetail")]
        public static NativeSubsystemDetail GetNativeSubsystemDetail(
            [ToolParameter("Exact native subsystem AreaName to drill (e.g. \"Font Engine\", \"Rendering\").")]
            string areaName,
            [ToolParameter("How many root references to return, largest first (default 50).")]
            int topN = 50)
        {
            if (s_Snapshot == null || !s_Snapshot.Valid) return null;
            var cs = s_Snapshot;
            var roots = cs.NativeRootReferences;
            var processed = cs.ProcessedNativeRoots;
            // ProcessedNativeRoots.Data is indexed by root index only when it wasn't built in the
            // legacy no-root-info mode (where Count falls back to NativeObjects.Count).
            bool canJoinResident = ResidentAvailable && processed != null
                && roots.Count > 0 && processed.Count == roots.Count;

            var result = new NativeSubsystemDetail
            {
                areaName = areaName,
                residentAvailable = canJoinResident,
                comparisonDrillTarget = s_ComparisonDrillTarget,
                summary = new NativeSubsystemSummary(),
                roots = new List<NativeRootDetail>(),
            };

            var rows = new List<NativeRootDetail>();
            ulong totalCommitted = 0, totalResident = 0;
            for (long i = 0; i < roots.Count; i++)
            {
                if (!string.Equals(roots.AreaName[i], areaName, StringComparison.Ordinal)) continue;
                ulong committed;
                ulong? resident = null;
                if (canJoinResident)
                {
                    var sz = processed.Data[i].AccumulatedRootSizes.SumUp();
                    committed = sz.Committed;
                    resident = sz.Resident;
                    totalResident += sz.Resident;
                }
                else
                {
                    committed = roots.AccumulatedSize[i];
                }
                totalCommitted += committed;
                result.summary.rootRefCount++;
                rows.Add(new NativeRootDetail
                {
                    objectName = roots.ObjectName[i],
                    committed = committed,
                    resident = resident,
                    rootId = roots.Id[i],
                });
            }

            result.summary.totalCommitted = totalCommitted;
            result.summary.totalResident = canJoinResident ? (ulong?)totalResident : null;

            rows.Sort((a, b) => b.committed.CompareTo(a.committed));
            for (int i = 0; i < rows.Count && i < topN; i++) result.roots.Add(rows[i]);
            return result;
        }

        // ---------------------------------------------------------------- B4: Object retention path

        // "Why is this object resident" — shortest path from a root down to the object.
        // Replicates PathsToRootDetailView.BuildShortestPath: a parent-pointer walk over the
        // pre-computed RootAndImpactInfo.ShortestPathInfo tree (populated by PostProcess).
        // Keyed by nativeObjectIndex (the Source.Index emitted by GetUnityObjectTypeDetail) to avoid
        // EntityId version-portability issues (EntityId aliases differ by ENTITY_ID_CHANGED_SIZE).
        [AgentTool(
            "Shortest reference path from a GC/scene root down to a single native object — explains why it " +
            "stays resident (leak / Hypothesize material). Pass a nativeObjectIndex from GetUnityObjectTypeDetail. " +
            "Degrades gracefully when path-to-root data is unavailable.",
            "Unity.MemoryProfiler.GetObjectRetentionPath")]
        public static ObjectRetentionPath GetObjectRetentionPath(
            [ToolParameter("The nativeObjectIndex of the target object (from GetUnityObjectTypeDetail's objects[].nativeObjectIndex).")]
            long nativeObjectIndex)
        {
            var res = new ObjectRetentionPath { nativeObjectIndex = nativeObjectIndex, comparisonDrillTarget = s_ComparisonDrillTarget, path = new List<RetentionNode>() };
            if (s_Snapshot == null || !s_Snapshot.Valid) { res.note = "no snapshot loaded; call Initialize first"; return res; }
            var cs = s_Snapshot;
            if (nativeObjectIndex < 0 || nativeObjectIndex >= cs.NativeObjects.Count)
            { res.note = "invalid nativeObjectIndex"; return res; }

            var rai = cs.RootAndImpactInfo;
            if (rai == null || !rai.SuccessfullyBuilt)
            {
                res.pathDataAvailable = false;
                res.note = "path-to-root data unavailable (RootsAndImpact disabled or capture has no scene-roots/assetbundles info)";
                return res;
            }
            res.pathDataAvailable = true;

            var start = new CachedSnapshot.SourceIndex(CachedSnapshot.SourceIndex.SourceId.NativeObject, nativeObjectIndex);
            var step = SourceIndexToRootAndImpactInfoMapper.GetNestedElement(in rai.ShortestPathInfo, start);
            if (!step.Valid)
            {
                res.isReachableFromRoot = false;
                res.note = "object has no recorded path to root";
                return res;
            }
            res.isReachableFromRoot = true;

            // Collect object -> root, then reverse for root -> object output order.
            var chain = new List<CachedSnapshot.SourceIndex>();
            var depths = new List<long>();
            chain.Add(start); depths.Add(step.Depth);
            int guard = 0;
            while (step.Valid && !step.IsRoot && guard++ < 8192)
            {
                var parent = step.Parent;
                step = SourceIndexToRootAndImpactInfoMapper.GetNestedElement(in rai.ShortestPathInfo, parent);
                chain.Add(parent); depths.Add(step.Valid ? step.Depth : 0);
                if (!step.Valid || step.IsRoot) break;
            }

            for (int i = chain.Count - 1; i >= 0; i--)
                res.path.Add(MakeRetentionNode(cs, chain[i], depths[i]));
            return res;
        }

        static RetentionNode MakeRetentionNode(CachedSnapshot cs, CachedSnapshot.SourceIndex idx, long depth)
        {
            var node = new RetentionNode
            {
                name = idx.GetName(cs),
                sourceKind = idx.Id.ToString(),
                depth = depth,
            };
            if (idx.Id == CachedSnapshot.SourceIndex.SourceId.NativeObject)
            {
                int ti = cs.NativeObjects.NativeTypeArrayIndex[idx.Index];
                if (ti >= 0) node.typeName = cs.NativeTypes.TypeName[ti];
            }
            return node;
        }

        static void CollectLeaves(IEnumerable<TreeViewItemData<UnityObjectsModel.ItemData>> nodes,
            List<UnityObjectsModel.ItemData> outLeaves)
        {
            foreach (var n in nodes)
            {
                if (n.hasChildren) CollectLeaves(n.children, outLeaves);
                else outLeaves.Add(n.data);
            }
        }

        // ================================================================ Unrooted allocation callstacks (step 9)

        // Attributes the Unrooted native memory bucket (allocations with no root reference) to allocation
        // sites + human-readable callstacks, when the capture was taken with -enable-memoryprofiler-callstacks
        // (Unity 6000.3+; gate = NativeAllocationSites.Count > 0). Wraps the package's GetReadableCallstack
        // (source-access-map §9 step 9). The total Unrooted size is ALWAYS computed (RootReferenceId <= 0);
        // only the per-site callstack attribution needs the callstack-enabled capture, so this degrades
        // gracefully (available:false) like GetObjectRetentionPath's pathDataAvailable.
        [AgentTool(
            "Breaks down the Unrooted native memory bucket (native allocations with no root reference — the " +
            "'Unrooted'/'Unknown' subsystem). Always reports total Unrooted committed size. When the capture was " +
            "taken with -enable-memoryprofiler-callstacks (Unity 6000.3+), attributes the top allocation sites to " +
            "memory labels + readable callstacks and flags whether each looks app-code-actionable or engine-internal. " +
            "Otherwise returns available:false with guidance to recapture with callstacks. (Managed GC.Alloc spikes " +
            "are a CPU-Profiler concern, not this tool.)",
            "Unity.MemoryProfiler.GetUnrootedAllocationBreakdown")]
        public static UnrootedAllocationBreakdown GetUnrootedAllocationBreakdown(
            [ToolParameter("Max number of top allocation sites to return, ranked by committed size. Default 20.")]
            int topN = 20)
        {
            var res = new UnrootedAllocationBreakdown
            {
                comparisonDrillTarget = s_ComparisonDrillTarget,
                topSites = new List<UnrootedSite>(),
            };
            if (s_Snapshot == null || !s_Snapshot.Valid) { res.note = "no snapshot loaded; call Initialize first"; return res; }
            var cs = s_Snapshot;
            var allocs = cs.NativeAllocations;
            bool residentAvail = ResidentAvailable;
            res.residentAvailable = residentAvail;

            // Unrooted = RootReferenceId <= 0 ("not rooted to anything"; 0 = unrooted, see
            // NativeAllocationEntriesCache). Site ids are only present when callstacks were captured.
            bool haveSiteIds = cs.NativeAllocationSites.Count > 0 && allocs.AllocationSiteId.Count == allocs.Count;
            var committedBySite = new Dictionary<ulong, ulong>();
            var residentBySite = new Dictionary<ulong, ulong>();
            var countBySite = new Dictionary<ulong, long>();

            // Pass 1 — allocation COUNT from the allocation table (definitional # of unrooted allocations).
            for (long i = 0; i < allocs.Count; i++)
            {
                if (allocs.RootReferenceId[i] > 0) continue;
                res.unrootedAllocationCount++;
                if (!haveSiteIds) continue;
                var siteId = allocs.AllocationSiteId[i];
                if (siteId == CachedSnapshot.NativeAllocationSiteEntriesCache.SiteIdNullPointer) continue;
                countBySite.TryGetValue(siteId, out var n); countBySite[siteId] = n + 1;
            }

            // Pass 2 — committed/resident FOOTPRINT via the unified memory map (EntriesMemoryMap), the same
            // primitive the Memory Profiler window and GetMemoryOverview use (source-access-map §0). It
            // attributes actual committed spans (reconciling region boundaries/overlap), so totals match the
            // MP "Unrooted" node — unlike a raw sum of NativeAllocations.Size, which slightly over-counts and
            // has no resident.
            ulong totalCommitted = 0, totalResident = 0;
            cs.EntriesMemoryMap.ForEachFlatWithResidentSize((index, address, size, residentSize, source) =>
            {
                if (source.Id != CachedSnapshot.SourceIndex.SourceId.NativeAllocation) return;
                long ai = source.Index;
                if (allocs.RootReferenceId[ai] > 0) return; // rooted → not Unrooted
                totalCommitted += size; totalResident += residentSize;
                if (!haveSiteIds) return;
                var siteId = allocs.AllocationSiteId[ai];
                if (siteId == CachedSnapshot.NativeAllocationSiteEntriesCache.SiteIdNullPointer) return;
                committedBySite.TryGetValue(siteId, out var c); committedBySite[siteId] = c + size;
                residentBySite.TryGetValue(siteId, out var r); residentBySite[siteId] = r + residentSize;
            });
            res.totalUnrootedCommitted = totalCommitted;
            res.totalUnrootedResident = residentAvail ? (ulong?)totalResident : null;

            if (cs.NativeAllocationSites.Count == 0)
            {
                res.available = false;
                res.note = "Allocation callstacks not present in this capture — total Unrooted size is reported but " +
                    "cannot be attributed to code paths. Recapture with -enable-memoryprofiler-callstacks (Unity 6000.3+) " +
                    "to break Unrooted memory down by allocation site and callstack.";
                return res;
            }

            res.available = true;
            res.siteCount = committedBySite.Count;
            var sites = new List<ulong>(committedBySite.Keys);
            sites.Sort((a, b) => committedBySite[b].CompareTo(committedBySite[a]));
            int limit = topN > 0 ? Math.Min(topN, sites.Count) : sites.Count;
            for (int i = 0; i < limit; i++)
            {
                var siteId = sites[i];
                string callstack = null, memLabel = null;
                var info = cs.NativeAllocationSites.GetCallStackInfo(siteId);
                if (info.Valid)
                {
                    var rc = cs.NativeAllocationSites.GetReadableCallstack(
                        cs.NativeMemoryLabels, cs.NativeCallstackSymbols, info.Index,
                        simplifyCallStacks: true, clickableCallStacks: false);
                    callstack = rc.Callstack;
                    memLabel = rc.MemLabel;
                }
                // Heuristic (not authoritative) — classify by the callstack's managed frames:
                //   user-code      : a managed (.cs) frame whose declaring type is OUTSIDE engine/system
                //                    namespaces (your Assembly-CSharp / plugin) → app-actionable.
                //   engine-managed : managed frames present but all in UnityEngine.*/Unity.*/System.* (e.g.
                //                    URP render loop) → not your code, but usually influenceable via settings.
                //   native         : no managed frame at all → engine-internal native → referral.
                var frameOrigin = ClassifyCallstackOrigin(callstack);
                bool appActionable = frameOrigin == "user-code";
                countBySite.TryGetValue(siteId, out var siteAllocCount);
                res.topSites.Add(new UnrootedSite
                {
                    siteId = siteId,
                    committedBytes = committedBySite[siteId],
                    residentBytes = residentAvail ? (ulong?)(residentBySite.TryGetValue(siteId, out var rb) ? rb : 0UL) : null,
                    allocationCount = siteAllocCount,
                    memoryLabel = memLabel,
                    frameOrigin = frameOrigin,
                    appActionable = appActionable,
                    actionabilityHint =
                        frameOrigin == "user-code" ? "reachable from your script code (Assembly-CSharp) or a plugin — review your code" :
                        frameOrigin == "engine-managed" ? "Unity-managed subsystem (e.g. render pipeline) — not your code, but often influenceable via project/quality/component settings" :
                        "engine-internal native allocation — usually not directly fixable; consider a Unity bug report (refer)",
                    callstack = callstack,
                });
            }
            return res;
        }

        // Engine/system namespace prefixes — Mono managed frames in these are Unity-owned or BCL, not user code.
        static readonly string[] k_EngineNamespacePrefixes =
        {
            "UnityEngine.", "UnityEditor.", "UnityEngineInternal.", "Unity.", "System.", "Mono.", "Microsoft."
        };

        // IL2CPP strips namespaces from the mangled symbol, so we can't match by namespace prefix like Mono does.
        // Instead we denylist known engine/BCL declaring-type TOKENS (namespace-stripped). Coarser than the Mono
        // path (type-name collisions possible), so an unknown token defaults to user-code — consistent with the
        // "any user frame → user-code" rule and hedged by presenting the raw callstack. Extensible list.
        static readonly HashSet<string> k_Il2cppEngineTypeTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "Object", "GameObject", "Component", "Behaviour", "MonoBehaviour", "Transform", "RectTransform",
            "Animator", "Animation", "Renderer", "Mesh", "Material", "Shader", "Texture", "Texture2D", "Sprite",
            "Camera", "Light", "Rigidbody", "Collider", "Resources", "AsyncOperation", "AsyncOperationBase",
            "AsyncOperationHandle", "Task", "ExecutionContext", "MoveNextRunner", "AwaitTaskContinuation",
            "AsyncTaskMethodBuilder", "SynchronizationContext", "UnitySynchronizationContext", "WorkRequest",
            // Render pipeline (SRP/URP) + IMGUI — pure-engine C# render/GUI loops that IL2CPP strips of their
            // UnityEngine.* namespace; distinctive names, low collision risk with user types.
            "UniversalRenderPipeline", "RenderPipeline", "RenderPipelineManager", "RenderPipelineManagerProxy",
            "ScriptableRenderContext", "GUI", "GUIStyle", "GUILayout", "GUIUtility", "GUIContent"
        };

        enum FrameKind { None, EngineManaged, UserManaged }

        // Classify a single readable-callstack line as a managed frame (engine vs user) or not a managed frame,
        // supporting BOTH Mono JIT and IL2CPP symbolication formats (a stack may even mix them).
        static FrameKind ClassifyFrame(string line)
        {
            // Mono: "(Mono JIT Code) [File.cs:NN] Namespace.Type:Method (params)"
            var monoType = ExtractManagedTypeName(line);
            if (monoType != null)
            {
                foreach (var p in k_EngineNamespacePrefixes)
                    if (monoType.StartsWith(p, StringComparison.Ordinal)) return FrameKind.EngineManaged;
                return FrameKind.UserManaged;
            }

            // IL2CPP: "<idx> <module> <0xADDR> <Il2CppMangledSymbol>_m<hex> + <offset>"
            var il2cppType = ExtractIl2cppTypeToken(line);
            if (il2cppType != null)
                return k_Il2cppEngineTypeTokens.Contains(il2cppType) ? FrameKind.EngineManaged : FrameKind.UserManaged;

            return FrameKind.None;
        }

        // Classify a readable callstack: "user-code" (a managed frame outside engine/system), "engine-managed"
        // (managed frames present but all Unity/system/BCL), or "native" (no managed frame). Works on Mono and IL2CPP.
        static string ClassifyCallstackOrigin(string callstack)
        {
            if (string.IsNullOrEmpty(callstack)) return "native";
            bool anyManaged = false;
            foreach (var line in callstack.Split('\n'))
            {
                var kind = ClassifyFrame(line);
                if (kind == FrameKind.None) continue;
                anyManaged = true;
                if (kind == FrameKind.UserManaged) return "user-code";
            }
            return anyManaged ? "engine-managed" : "native";
        }

        // Extract the declaring type of a managed frame line, or null.
        // Line shape: "(Mono JIT Code) [File.cs:NN] Namespace.Type:Method (params)". Parse only the type
        // (before ':Method' and before the ' (' params) so engine types in the PARAMS don't skew the result.
        static string ExtractManagedTypeName(string line)
        {
            int bracketEnd = line.IndexOf("] ", StringComparison.Ordinal);
            if (bracketEnd < 0 || line.IndexOf(".cs", StringComparison.Ordinal) < 0) return null;
            int start = bracketEnd + 2;
            int paren = line.IndexOf(" (", start, StringComparison.Ordinal);
            string typeMethod = paren > start ? line.Substring(start, paren - start) : line.Substring(start);
            int colon = typeMethod.IndexOf(':');
            return colon > 0 ? typeMethod.Substring(0, colon) : null;
        }

        // Matches an IL2CPP mangled managed-method symbol: "<Type>_<Method>_m<32-hex>" with optional
        // "_gshared"/"_inline" suffixes. Used to tell a transpiled C# frame from a native C++ one.
        static readonly Regex s_Il2cppManagedSymbol = new Regex(@"_m[0-9A-Fa-f]{16,}(_gshared|_inline)*$", RegexOptions.Compiled);

        // Extract the declaring-type token of an IL2CPP frame, or null. Line shape (Xcode-style symbolication):
        // "0   UnityFramework   0x0000000119042dc0 Character_ChangeAnimatorController_mDB46...E6D48 + 1184".
        // The symbol is namespace-stripped, so we return the leading token before the first '_' (e.g. "Character").
        // Native C++ frames (Itanium mangling, "_Z...") and system-module frames are rejected → treated as native.
        static string ExtractIl2cppTypeToken(string line)
        {
            var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries); // split on any whitespace run
            if (parts.Length < 4) return null;
            if (parts[1] != "UnityFramework") return null;                         // only the app binary carries IL2CPP managed symbols
            var symbol = parts[3];
            if (symbol.StartsWith("_Z", StringComparison.Ordinal)) return null;    // C++ Itanium mangling = native
            if (!s_Il2cppManagedSymbol.IsMatch(symbol)) return null;               // not an IL2CPP managed method
            int us = symbol.IndexOf('_');
            return us > 0 ? symbol.Substring(0, us) : symbol;
        }

        // ---- Unrooted comparison aggregation (step 9, comparison mode)

        struct UnrootedAgg { public ulong committed; public ulong resident; public long count; }
        struct UnrootedSiteAgg
        {
            public ulong committed; public ulong resident; public long count;
            public string memoryLabel; public string frameOrigin; public string callstack;
        }

        // A readable callstack keyed for cross-snapshot join. siteId/symbols are process addresses (differ per
        // capture), so we key on the SYMBOLICATED callstack instead — stable for the same code. Per-capture noise
        // is normalized away so the key survives across captures/builds:
        //   Mono   — line numbers ("File.cs:315]" → "File.cs]").
        //   IL2CPP — runtime addresses ("0x000000011904...") and "+ <offset>" tails (both vary per capture),
        //            leaving "<idx> <module> <symbol>" as the stable join key.
        static readonly Regex s_CallstackLineNo = new Regex(@":\d+\]", RegexOptions.Compiled);
        static readonly Regex s_CallstackAddr = new Regex(@"0x[0-9A-Fa-f]+", RegexOptions.Compiled);
        static readonly Regex s_CallstackOffset = new Regex(@" \+ \d+", RegexOptions.Compiled);
        static string CallstackSignature(string callstack)
        {
            if (string.IsNullOrEmpty(callstack)) return "<no-callstack>";
            var s = s_CallstackLineNo.Replace(callstack, "]");
            s = s_CallstackAddr.Replace(s, "");
            s = s_CallstackOffset.Replace(s, "");
            return s;
        }

        // Aggregate one snapshot's Unrooted allocations: total committed/resident (memory-map footprint, always),
        // and — when callstacks are present — per-memory-label and per-callstack-signature rollups (all sites).
        static void AggregateUnrooted(CachedSnapshot cs,
            out ulong totalCommitted, out ulong totalResident,
            out Dictionary<string, UnrootedAgg> byLabel,
            out Dictionary<string, UnrootedSiteAgg> bySignature,
            out bool haveCallstacks)
        {
            var allocs = cs.NativeAllocations;
            bool haveCs = cs.NativeAllocationSites.Count > 0 && allocs.AllocationSiteId.Count == allocs.Count;
            haveCallstacks = haveCs; // local copy — an out param can't be used inside the lambda below (CS1628)
            byLabel = new Dictionary<string, UnrootedAgg>();
            bySignature = new Dictionary<string, UnrootedSiteAgg>();

            // committed/resident footprint per site via the unified memory map (matches MP).
            var committedBySite = new Dictionary<ulong, ulong>();
            var residentBySite = new Dictionary<ulong, ulong>();
            var countBySite = new Dictionary<ulong, long>();
            ulong tc = 0, tr = 0;
            cs.EntriesMemoryMap.ForEachFlatWithResidentSize((index, address, size, residentSize, source) =>
            {
                if (source.Id != CachedSnapshot.SourceIndex.SourceId.NativeAllocation) return;
                long ai = source.Index;
                if (allocs.RootReferenceId[ai] > 0) return;
                tc += size; tr += residentSize;
                if (!haveCs) return;
                var siteId = allocs.AllocationSiteId[ai];
                if (siteId == CachedSnapshot.NativeAllocationSiteEntriesCache.SiteIdNullPointer) return;
                committedBySite.TryGetValue(siteId, out var c); committedBySite[siteId] = c + size;
                residentBySite.TryGetValue(siteId, out var r); residentBySite[siteId] = r + residentSize;
                countBySite.TryGetValue(siteId, out var n); countBySite[siteId] = n + 1;
            });
            totalCommitted = tc; totalResident = tr;
            if (!haveCallstacks) return;

            foreach (var kv in committedBySite)
            {
                var siteId = kv.Key;
                var committed = kv.Value;
                residentBySite.TryGetValue(siteId, out var resident);
                countBySite.TryGetValue(siteId, out var count);

                string callstack = null, memLabel = null;
                var info = cs.NativeAllocationSites.GetCallStackInfo(siteId);
                if (info.Valid)
                {
                    var rc = cs.NativeAllocationSites.GetReadableCallstack(
                        cs.NativeMemoryLabels, cs.NativeCallstackSymbols, info.Index,
                        simplifyCallStacks: true, clickableCallStacks: false);
                    callstack = rc.Callstack; memLabel = rc.MemLabel;
                }
                if (string.IsNullOrEmpty(memLabel)) memLabel = "<no label>";

                byLabel.TryGetValue(memLabel, out var la);
                la.committed += committed; la.resident += resident; la.count += count;
                byLabel[memLabel] = la;

                var sig = CallstackSignature(callstack);
                bySignature.TryGetValue(sig, out var sa);
                sa.committed += committed; sa.resident += resident; sa.count += count;
                if (sa.callstack == null) { sa.callstack = callstack; sa.memoryLabel = memLabel; sa.frameOrigin = ClassifyCallstackOrigin(callstack); }
                bySignature[sig] = sa;
            }
        }

        // ================================================================ Comparison (step 8 — 2-snapshot diff)

        // Loaded-pair priority, file fallback. A = baseline, B = candidate/later.
        //  - both paths empty → reuse the Memory Profiler window's Compare pair (Base + Compared);
        //    requires CompareMode and two valid snapshots. Not owned.
        //  - both paths given → load each via FileReader (owned, like ResolveSnapshot).
        //  - exactly one path given → rejected (ambiguous).
        static bool ResolveSnapshotPair(
            string filePathA, string filePathB,
            out CachedSnapshot csA, out CachedSnapshot csB,
            out bool ownsA, out bool ownsB,
            out string sourceA, out string sourceB,
            out string error)
        {
            csA = csB = null; ownsA = ownsB = false; sourceA = sourceB = null; error = null;
            bool aEmpty = string.IsNullOrEmpty(filePathA);
            bool bEmpty = string.IsNullOrEmpty(filePathB);

            if (aEmpty && bEmpty)
            {
                var windows = Resources.FindObjectsOfTypeAll<MemoryProfilerWindow>();
                var service = (windows != null && windows.Length > 0) ? windows[0].SnapshotDataService : null;
                if (service == null || !service.CompareMode)
                {
                    error = "No comparison pair in the Memory Profiler window. Load two snapshots in Compare mode, " +
                            "or pass both filePathA and filePathB.";
                    return false;
                }
                var b = service.Base;
                var c = service.Compared;
                if (b == null || !b.Valid || c == null || !c.Valid)
                {
                    error = "Memory Profiler window is in Compare mode but one or both snapshots are not valid.";
                    return false;
                }
                csA = b; csB = c; ownsA = ownsB = false; sourceA = sourceB = "loaded";
                return true;
            }

            if (aEmpty || bEmpty)
            {
                error = "Pass both filePathA and filePathB, or omit both to use the window's Compare pair.";
                return false;
            }

            csA = ResolveSnapshot(filePathA, out ownsA, out sourceA, out var errA);
            if (csA == null) { error = $"filePathA: {errA}"; return false; }

            csB = ResolveSnapshot(filePathB, out ownsB, out sourceB, out var errB);
            if (csB == null)
            {
                if (ownsA) { try { csA.Dispose(); } catch { } } // we just built A — release it before bailing
                csA = null; ownsA = false;
                error = $"filePathB: {errB}";
                return false;
            }
            return true;
        }

        [AgentTool(
            "Loads a PAIR of memory snapshots for comparison/diff (A = baseline, B = candidate/later) and reports " +
            "each one's capture origin and resident-data availability. Omit both paths to use the two snapshots " +
            "already open in the Memory Profiler window in Compare mode; pass both filePathA and filePathB to load " +
            "from files. Call before GetComparisonOverview.",
            "Unity.MemoryProfiler.InitializeComparison")]
        public static ComparisonInitializeResult InitializeComparison(
            [ToolParameter("Absolute path to the baseline .snap (A). Omit together with filePathB to use the window's Compare pair.")]
            string filePathA = null,
            [ToolParameter("Absolute path to the candidate/later .snap (B). Omit together with filePathA to use the window's Compare pair.")]
            string filePathB = null)
        {
            UnloadComparisonInternal();
            UnloadInternal(); // also release any prior single-snapshot before we re-point the drill target

            if (!ResolveSnapshotPair(filePathA, filePathB,
                out var csA, out var csB, out var ownsA, out var ownsB,
                out var srcA, out var srcB, out var resolveError))
            {
                return new ComparisonInitializeResult { error = resolveError };
            }

            s_SnapshotA = csA; s_OwnsA = ownsA;
            s_SnapshotB = csB; s_OwnsB = ownsB;

            // Point the single-snapshot Phase B drill tools (GetUnityObjectTypeDetail, GetPotentialDuplicates,
            // GetNativeSubsystemDetail, GetObjectRetentionPath) at B (the candidate) by default — that is the
            // natural drill target when investigating "what grew". s_Snapshot ALIASES the pair member (the pair
            // owns disposal), so s_OwnsSnapshot stays false. Use SetComparisonDrillTarget to switch to A.
            s_Snapshot = csB; s_OwnsSnapshot = false; s_ComparisonDrillTarget = "B";

            ResolveCaptureOrigin(csA, out var platformA, out var originA, out _);
            ResolveCaptureOrigin(csB, out var platformB, out var originB, out _);

            return new ComparisonInitializeResult
            {
                sourceA = srcA, sourceB = srcB,
                filePathA = filePathA, filePathB = filePathB,
                snapshotNameA = SnapshotNameOf(csA), snapshotNameB = SnapshotNameOf(csB),
                productNameA = ProductNameOf(csA), productNameB = ProductNameOf(csB),
                platformA = platformA, platformB = platformB,
                captureOriginA = originA, captureOriginB = originB,
                residentAvailableA = ResidentAvailableA,
                residentAvailableB = ResidentAvailableB,
                residentAvailableBoth = ResidentAvailableBoth,
                drillTarget = "B",
            };
        }

        [AgentTool(
            "Switches which snapshot of the loaded comparison pair the single-snapshot drill tools " +
            "(GetUnityObjectTypeDetail, GetPotentialDuplicates, GetNativeSubsystemDetail, GetObjectRetentionPath) " +
            "operate on. After InitializeComparison the target is B (candidate); call this with \"A\" to inspect the " +
            "baseline instead. Only valid while a comparison pair is loaded.",
            "Unity.MemoryProfiler.SetComparisonDrillTarget")]
        public static ComparisonDrillTargetResult SetComparisonDrillTarget(
            [ToolParameter("Which snapshot of the comparison pair to drill: \"A\" (baseline) or \"B\" (candidate/later).")]
            string which)
        {
            if (s_SnapshotA == null || !s_SnapshotA.Valid || s_SnapshotB == null || !s_SnapshotB.Valid)
                return new ComparisonDrillTargetResult { error = "No comparison pair loaded; call InitializeComparison first." };

            CachedSnapshot target;
            string normalized;
            if (string.Equals(which, "A", StringComparison.OrdinalIgnoreCase)) { target = s_SnapshotA; normalized = "A"; }
            else if (string.Equals(which, "B", StringComparison.OrdinalIgnoreCase)) { target = s_SnapshotB; normalized = "B"; }
            else return new ComparisonDrillTargetResult { error = $"Invalid target '{which}'. Use \"A\" or \"B\"." };

            UnloadInternal();                       // release any prior file-owned single snapshot
            s_Snapshot = target; s_OwnsSnapshot = false; s_ComparisonDrillTarget = normalized; // alias the pair member (pair owns disposal)

            ResolveCaptureOrigin(target, out var platform, out var origin, out _);
            return new ComparisonDrillTargetResult
            {
                drillTarget = normalized,
                platform = platform,
                captureOrigin = origin,
                residentAvailable = ResidentAvailable,
            };
        }

        [AgentTool(
            "Diff overview for the loaded comparison pair (call InitializeComparison first): a ranked list of Unity " +
            "object type changes, native subsystem changes, and whole-snapshot totals. B − A (positive delta = grew " +
            "from baseline to candidate). Each change is classified new/grew/shrank/freed/unchanged. The single-" +
            "snapshot resident-first / GPU / Untracked rules carry over.",
            "Unity.MemoryProfiler.GetComparisonOverview")]
        public static SnapshotComparisonOverview GetComparisonOverview(
            [ToolParameter("How many CHANGED type-group deltas to return, ranked by |committedDelta| (default 50). " +
                "Unchanged types are excluded. If changedTypeGroupCount exceeds this, raise topN to see more.")]
            int topN = 50)
        {
            if (s_SnapshotA == null || !s_SnapshotA.Valid || s_SnapshotB == null || !s_SnapshotB.Valid)
                return new SnapshotComparisonOverview { error = "No comparison pair loaded; call InitializeComparison first." };

            var csA = s_SnapshotA;
            var csB = s_SnapshotB;
            bool residentBoth = ResidentAvailableBoth;

            var result = new SnapshotComparisonOverview
            {
                residentAvailableA = ResidentAvailableA,
                residentAvailableB = ResidentAvailableB,
                residentAvailableBoth = residentBoth,
                typeGroupDeltas = new List<TypeGroupDelta>(),
                subsystemDeltas = new List<SubsystemDelta>(),
            };

            // (a) Unity object type-group deltas — wrap the package's comparison builder. The root nodes are
            //     per-native-type groups whose TotalSizeInA/B sum every object of that type in each snapshot.
            var compArgs = new UnityObjectsComparisonModelBuilder.BuildArgs(
                searchStringFilter: null,
                unityObjectNameFilter: null,
                unityObjectInstanceIDFilter: null,
                flattenHierarchy: false,
                includeUnchanged: true,
                disambiguateByInstanceId: false,
                unityObjectNameGroupComparisonSelectionProcessor: null,
                unityObjectTypeComparisonSelectionProcessor: null);
            var compModel = new UnityObjectsComparisonModelBuilder().Build(csA, csB, compArgs);

            foreach (var typeNode in compModel.RootNodes)
            {
                var d = typeNode.data;
                ulong cA = d.TotalSizeInA.Committed, cB = d.TotalSizeInB.Committed;
                var kind = ChangeKind(cA, cB, d.CountInA, d.CountInB);
                if (kind == "unchanged") continue; // a diff shows changes; unchanged types are noise
                result.typeGroupDeltas.Add(new TypeGroupDelta
                {
                    typeName = string.IsNullOrEmpty(d.NativeTypeName) ? d.Name : d.NativeTypeName,
                    committedA = cA,
                    committedB = cB,
                    committedDelta = (long)cB - (long)cA,
                    residentA = residentBoth ? (ulong?)d.TotalSizeInA.Resident : null,
                    residentB = residentBoth ? (ulong?)d.TotalSizeInB.Resident : null,
                    residentDelta = residentBoth ? (long?)((long)d.TotalSizeInB.Resident - (long)d.TotalSizeInA.Resident) : null,
                    countA = (int)d.CountInA,
                    countB = (int)d.CountInB,
                    countDelta = d.CountDelta,
                    changeKind = kind,
                });
            }
            result.changedTypeGroupCount = result.typeGroupDeltas.Count;
            result.typeGroupDeltas.Sort((x, y) => Math.Abs(y.committedDelta).CompareTo(Math.Abs(x.committedDelta)));
            if (result.typeGroupDeltas.Count > topN)
                result.typeGroupDeltas = result.typeGroupDeltas.GetRange(0, topN);

            // (b) Native subsystem deltas — no package comparison builder; run the single aggregation on both
            //     and subtract, joining by AreaName. Resident is joined from ProcessedNativeRoots (step 8 decision).
            var subA = BuildSubsystemMap(csA, ResidentAvailableA, out var residentJoinedA);
            var subB = BuildSubsystemMap(csB, ResidentAvailableB, out var residentJoinedB);
            bool subResidentBoth = residentJoinedA && residentJoinedB;
            var allAreas = new HashSet<string>(subA.Keys);
            allAreas.UnionWith(subB.Keys);
            foreach (var area in allAreas)
            {
                subA.TryGetValue(area, out var aAgg);
                subB.TryGetValue(area, out var bAgg);
                var kind = ChangeKind(aAgg.committed, bAgg.committed, 0, 0);
                if (kind == "unchanged") continue; // diff shows changes only
                result.subsystemDeltas.Add(new SubsystemDelta
                {
                    areaName = area,
                    committedA = aAgg.committed,
                    committedB = bAgg.committed,
                    committedDelta = (long)bAgg.committed - (long)aAgg.committed,
                    residentA = subResidentBoth ? (ulong?)aAgg.resident : null,
                    residentB = subResidentBoth ? (ulong?)bAgg.resident : null,
                    residentDelta = subResidentBoth ? (long?)((long)bAgg.resident - (long)aAgg.resident) : null,
                    classification = ClassifyArea(area),
                    changeKind = kind,
                });
            }
            result.changedSubsystemCount = result.subsystemDeltas.Count;
            result.subsystemDeltas.Sort((x, y) => Math.Abs(y.committedDelta).CompareTo(Math.Abs(x.committedDelta)));

            // (c) Whole-snapshot totals — subtract two independent overviews (no comparison builder for the summary).
            var ovA = BuildOverviewFor(csA, ResidentAvailableA);
            var ovB = BuildOverviewFor(csB, ResidentAvailableB);
            result.totalCommittedA = ovA.totalAllocatedCommitted;
            result.totalCommittedB = ovB.totalAllocatedCommitted;
            result.totalCommittedDelta = (long)ovB.totalAllocatedCommitted - (long)ovA.totalAllocatedCommitted;
            if (residentBoth && ovA.residentTotal != null && ovB.residentTotal != null
                && ovA.residentTotal.resident.HasValue && ovB.residentTotal.resident.HasValue)
            {
                result.totalResidentA = ovA.residentTotal.resident;
                result.totalResidentB = ovB.residentTotal.resident;
                result.totalResidentDelta = (long)ovB.residentTotal.resident.Value - (long)ovA.residentTotal.resident.Value;
            }

            return result;
        }

        struct SubsystemAgg { public ulong committed; public ulong resident; }

        // Per-AreaName committed (from NativeRootReferences.AccumulatedSize, matching GetNativeSubsystems) and,
        // when joinable, resident (from ProcessedNativeRoots, matching GetNativeSubsystemDetail).
        static Dictionary<string, SubsystemAgg> BuildSubsystemMap(CachedSnapshot cs, bool residentAvailable, out bool residentJoined)
        {
            var roots = cs.NativeRootReferences;
            var processed = cs.ProcessedNativeRoots;
            residentJoined = residentAvailable && processed != null && roots.Count > 0 && processed.Count == roots.Count;
            var map = new Dictionary<string, SubsystemAgg>();
            for (long i = 0; i < roots.Count; i++)
            {
                var area = roots.AreaName[i];
                if (string.IsNullOrEmpty(area)) area = "<empty>";
                map.TryGetValue(area, out var agg);
                agg.committed += roots.AccumulatedSize[i];
                if (residentJoined)
                    agg.resident += processed.Data[i].AccumulatedRootSizes.SumUp().Resident;
                map[area] = agg;
            }
            return map;
        }

        // Classify a committed/count change. "new"/"freed" mean the group is absent in one snapshot.
        static string ChangeKind(ulong committedA, ulong committedB, uint countA, uint countB)
        {
            bool inA = committedA > 0 || countA > 0;
            bool inB = committedB > 0 || countB > 0;
            if (!inA && inB) return "new";
            if (inA && !inB) return "freed";
            if (committedB > committedA) return "grew";
            if (committedB < committedA) return "shrank";
            if (countB > countA) return "grew";
            if (countB < countA) return "shrank";
            return "unchanged";
        }

        [AgentTool(
            "Diffs the Unrooted native memory bucket across the loaded comparison pair (call InitializeComparison " +
            "first) — for tracking native growth/leaks between two snapshots. Always reports the total Unrooted " +
            "committed/resident delta (B − A). When BOTH captures have -enable-memoryprofiler-callstacks, also diffs " +
            "by memory label and by callstack signature (joined across snapshots by symbolicated callstack, not by " +
            "volatile addresses), each classified new/grew/shrank/freed. Sites are ranked by |committedDelta| and " +
            "capped at topN. Otherwise returns available:false with only the total delta.",
            "Unity.MemoryProfiler.GetComparisonUnrootedBreakdown")]
        public static ComparisonUnrootedBreakdown GetComparisonUnrootedBreakdown(
            [ToolParameter("How many CHANGED callstack-signature site deltas to return, ranked by |committedDelta| (default 20). Unchanged sites are excluded.")]
            int topN = 20)
        {
            if (s_SnapshotA == null || !s_SnapshotA.Valid || s_SnapshotB == null || !s_SnapshotB.Valid)
                return new ComparisonUnrootedBreakdown { error = "No comparison pair loaded; call InitializeComparison first." };

            var csA = s_SnapshotA;
            var csB = s_SnapshotB;
            bool residentBoth = ResidentAvailableBoth;

            AggregateUnrooted(csA, out var tcA, out var trA, out var labelA, out var sigA, out var hasCsA);
            AggregateUnrooted(csB, out var tcB, out var trB, out var labelB, out var sigB, out var hasCsB);

            var res = new ComparisonUnrootedBreakdown
            {
                residentAvailableBoth = residentBoth,
                totalUnrootedCommittedA = tcA,
                totalUnrootedCommittedB = tcB,
                totalUnrootedCommittedDelta = (long)tcB - (long)tcA,
                totalUnrootedResidentA = residentBoth ? (ulong?)trA : null,
                totalUnrootedResidentB = residentBoth ? (ulong?)trB : null,
                totalUnrootedResidentDelta = residentBoth ? (long?)((long)trB - (long)trA) : null,
                labelDeltas = new List<UnrootedLabelDelta>(),
                siteDeltas = new List<UnrootedSiteDelta>(),
            };

            if (!hasCsA || !hasCsB)
            {
                res.available = false;
                res.note = "Allocation callstacks missing in " +
                    (!hasCsA && !hasCsB ? "both snapshots" : !hasCsA ? "snapshot A" : "snapshot B") +
                    " — only the total Unrooted delta is available. Recapture BOTH with " +
                    "-enable-memoryprofiler-callstacks (Unity 6000.3+) to diff by memory label and callstack.";
                return res;
            }
            res.available = true;

            // Memory-label rollup diff — memoryLabel is a stable string, joins cleanly across snapshots.
            var allLabels = new HashSet<string>(labelA.Keys); allLabels.UnionWith(labelB.Keys);
            foreach (var label in allLabels)
            {
                labelA.TryGetValue(label, out var a);
                labelB.TryGetValue(label, out var b);
                var kind = ChangeKind(a.committed, b.committed, (uint)a.count, (uint)b.count);
                if (kind == "unchanged") continue;
                res.labelDeltas.Add(new UnrootedLabelDelta
                {
                    memoryLabel = label,
                    committedA = a.committed, committedB = b.committed, committedDelta = (long)b.committed - (long)a.committed,
                    residentA = residentBoth ? (ulong?)a.resident : null,
                    residentB = residentBoth ? (ulong?)b.resident : null,
                    residentDelta = residentBoth ? (long?)((long)b.resident - (long)a.resident) : null,
                    countA = a.count, countB = b.count, countDelta = (long)b.count - (long)a.count,
                    changeKind = kind,
                });
            }
            res.changedLabelCount = res.labelDeltas.Count;
            res.labelDeltas.Sort((x, y) => Math.Abs(y.committedDelta).CompareTo(Math.Abs(x.committedDelta)));

            // Callstack-signature site diff — key is the symbolicated (line-stripped) callstack, stable across
            // captures/builds. Merges ASLR-duplicated siteIds that share a call path.
            var allSigs = new HashSet<string>(sigA.Keys); allSigs.UnionWith(sigB.Keys);
            foreach (var sig in allSigs)
            {
                sigA.TryGetValue(sig, out var a);
                sigB.TryGetValue(sig, out var b);
                var kind = ChangeKind(a.committed, b.committed, (uint)a.count, (uint)b.count);
                if (kind == "unchanged") continue;
                var rep = b.callstack != null ? b : a; // prefer B (candidate) metadata, else A (freed sites)
                res.siteDeltas.Add(new UnrootedSiteDelta
                {
                    committedA = a.committed, committedB = b.committed, committedDelta = (long)b.committed - (long)a.committed,
                    residentA = residentBoth ? (ulong?)a.resident : null,
                    residentB = residentBoth ? (ulong?)b.resident : null,
                    residentDelta = residentBoth ? (long?)((long)b.resident - (long)a.resident) : null,
                    countA = a.count, countB = b.count, countDelta = (long)b.count - (long)a.count,
                    memoryLabel = rep.memoryLabel, frameOrigin = rep.frameOrigin, callstack = rep.callstack,
                    changeKind = kind,
                });
            }
            res.changedSiteCount = res.siteDeltas.Count;
            res.siteDeltas.Sort((x, y) => Math.Abs(y.committedDelta).CompareTo(Math.Abs(x.committedDelta)));
            if (res.siteDeltas.Count > topN)
                res.siteDeltas = res.siteDeltas.GetRange(0, topN);

            return res;
        }

        // ---------------------------------------------------------------- Customization IO (ADR 0006 — step 7)
        // These two tools are snapshot-independent (pure file IO; s_Snapshot never touched).

        // Overrides for tests or explicit path injection. Null = auto-resolve.
        static string s_PlaybookPathOverride;            // layer "playbook"  → playbook.md
        static string s_ProjectCustomizationPathOverride; // layer "project"  → project-customization.md

        // Write overlays as UTF-8 WITHOUT a BOM. Encoding.UTF8 emits a BOM (EF BB BF), which the
        // Assistant's skill reference loader (ReadSkillResource) rejects as "not a text file" — so a
        // BOM-prefixed overlay silently fails to load during analysis even though the write succeeded.
        static readonly UTF8Encoding s_Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // The two overlay layers (ADR 0006, 2-overlay split):
        //   "playbook" = general, project-independent know-how  → playbook.md
        //   "project"  = project-dependent (accepted costs etc.) → project-customization.md
        static string OverlayFileName(string layer)
            => string.Equals(layer, "playbook", StringComparison.OrdinalIgnoreCase)
                ? "playbook.md" : "project-customization.md";

        // Resolve the overlay file for a layer: override → Application.dataPath subtree
        // (skill path preferred) → user skills folder.
        static string ResolveOverlayPath(string layer, out string error)
        {
            error = null;
            var fileName = OverlayFileName(layer);
            var overridePath = string.Equals(layer, "playbook", StringComparison.OrdinalIgnoreCase)
                ? s_PlaybookPathOverride : s_ProjectCustomizationPathOverride;
            if (!string.IsNullOrEmpty(overridePath))
            {
                if (File.Exists(overridePath))
                    return overridePath;
                error = $"override path not found: {overridePath}";
                return null;
            }

            // 1. Application.dataPath subtree — prefer paths that contain "unity-memory-profiling-skill".
            try
            {
                var dataPath = Application.dataPath;
                if (Directory.Exists(dataPath))
                {
                    var candidates = Directory.GetFiles(dataPath, fileName, SearchOption.AllDirectories);
                    string preferred = null, fallback = null;
                    foreach (var c in candidates)
                    {
                        var normalized = c.Replace('\\', '/');
                        if (normalized.IndexOf("unity-memory-profiling-skill", StringComparison.OrdinalIgnoreCase) >= 0)
                        { preferred = c; break; }
                        if (fallback == null) fallback = c;
                    }
                    var found = preferred ?? fallback;
                    if (found != null) return found;
                }
            }
            catch { /* ignore — try next candidate */ }

            // 2. User skills folder: ~/Library/Application Support/Unity/AIAssistantSkills/ (macOS)
            //    and equivalent locations on other platforms.
            var candidates2 = new List<string>();
            try
            {
                var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var appData   = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var home      = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                // macOS: ~/Library/Application Support/
                var macLibrary = Path.Combine(home, "Library", "Application Support");
                foreach (var root in new[] { localApp, appData, macLibrary })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    var skillsDir = Path.Combine(root, "Unity", "AIAssistantSkills");
                    if (!Directory.Exists(skillsDir)) continue;
                    var cs = Directory.GetFiles(skillsDir, fileName, SearchOption.AllDirectories);
                    foreach (var c in cs)
                    {
                        var normalized = c.Replace('\\', '/');
                        if (normalized.IndexOf("unity-memory-profiling-skill", StringComparison.OrdinalIgnoreCase) >= 0)
                            candidates2.Insert(0, c);
                        else
                            candidates2.Add(c);
                    }
                }
            }
            catch { /* ignore */ }
            if (candidates2.Count > 0) return candidates2[0];

            error = $"{fileName} not found; pass an explicit path or deploy the analysis skill";
            return null;
        }

        // Parse scope string "platform=iOS;type=ParticleSystem" into a dictionary (lowercase keys+values).
        static Dictionary<string, string> ParseScope(string scope)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(scope)) return d;
            foreach (var part in scope.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                var k = part.Substring(0, eq).Trim();
                var v = part.Substring(eq + 1).Trim();
                if (!string.IsNullOrEmpty(k)) d[k] = v;
            }
            return d;
        }

        // Returns true when requested scope matches an entry's scope line.
        // Each key in filter must match: entry value equals filter value, OR entry value is "*", OR key absent in entry.
        static bool ScopeMatches(string entryScopeLine, Dictionary<string, string> filter)
        {
            if (filter == null || filter.Count == 0) return true;
            var entryScope = ParseScope(entryScopeLine);
            foreach (var kv in filter)
            {
                if (entryScope.TryGetValue(kv.Key, out var ev))
                {
                    if (!ev.Equals("*", StringComparison.OrdinalIgnoreCase)
                        && !ev.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                // key absent in entry → treat as match
            }
            return true;
        }

        // Slugify: lowercase, keep alphanumeric/dot/hyphen, spaces→hyphen, collapse hyphens, trim.
        static string Slugify(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            bool lastHyphen = false;
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '.' )
                { sb.Append(c); lastHyphen = false; }
                else if ((c == ' ' || c == '-' || c == '_') && !lastHyphen)
                { sb.Append('-'); lastHyphen = true; }
            }
            return sb.ToString().Trim('-');
        }

        // Sanitize classification for use in slug: §1→s1, §2→s2, §3→s3, §4→s4, cross-cutting→xc, accepted→acc, else slugify.
        static string SanitizeClassificationForSlug(string classification)
        {
            if (string.IsNullOrEmpty(classification)) return "unk";
            var c = classification.Trim();
            if (c == "§1") return "s1";
            if (c == "§2") return "s2";
            if (c == "§3") return "s3";
            if (c == "§4") return "s4";
            if (c.Equals("cross-cutting", StringComparison.OrdinalIgnoreCase)) return "xc";
            if (c.Equals("accepted", StringComparison.OrdinalIgnoreCase)) return "acc";
            return Slugify(c);
        }

        // Derive entry id from classification, scope fields and subject.
        static string DeriveEntryId(string classification, string scopePlatform, string scopeProject,
            string scopeType, string scopeCaptureOrigin, string subject)
        {
            var parts = new List<string>();
            parts.Add(SanitizeClassificationForSlug(classification));
            // Include non-null, non-*, non-empty scope fields in order.
            foreach (var v in new[] { scopePlatform, scopeProject, scopeType, scopeCaptureOrigin })
            {
                if (!string.IsNullOrEmpty(v) && v != "*")
                    parts.Add(Slugify(v));
            }
            parts.Add(Slugify(subject));
            return string.Join(".", parts);
        }

        static readonly Regex s_EntryStartRe = new Regex(
            @"<!--\s*entry:start\s+id=([^\s>]+)\s*-->", RegexOptions.Compiled);
        static readonly Regex s_ScopeLineRe = new Regex(
            @"-\s*\*\*scope\*\*:\s*(.+)", RegexOptions.Compiled);

        // The region markers may also appear literally inside the overlay file's own prose
        // (e.g. backtick-quoted in the format docs). Match them ONLY when alone on a line so
        // those inline mentions are skipped and we hit the real machine-managed region.
        static readonly Regex s_EntriesStartRe = new Regex(
            @"(?m)^<!-- entries:start -->[ \t]*$", RegexOptions.Compiled);
        static readonly Regex s_EntriesEndRe = new Regex(
            @"(?m)^<!-- entries:end -->[ \t]*$", RegexOptions.Compiled);

        static int FindEntriesMarker(string content, bool start)
        {
            var m = (start ? s_EntriesStartRe : s_EntriesEndRe).Match(content);
            return m.Success ? m.Index : -1;
        }

        // Neutralize HTML-comment delimiters in LLM-supplied text so it cannot inject or spoof the
        // <!-- entry ... --> / <!-- entries ... --> markers that structure the overlay file (a spoofed
        // marker would corrupt region slicing / merge on the next RecordCustomization or GetCustomization).
        static string StripCommentMarkers(string s)
            => string.IsNullOrEmpty(s) ? s : s.Replace("<!--", "<! --").Replace("-->", "-- >");

        // Single-line entry field (subject/scope/id/sourceContext): neutralize markers, collapse newlines, cap length.
        static string SanitizeSingleLine(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = StripCommentMarkers(s).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > maxLen ? s.Substring(0, maxLen).TrimEnd() : s;
        }

        // Multi-line entry body: keep markdown/newlines, neutralize markers, cap length.
        static string SanitizeBody(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = StripCommentMarkers(s);
            return s.Length > maxLen ? s.Substring(0, maxLen).TrimEnd() + " …(truncated)" : s;
        }

        [AgentTool(
            "Returns one Memory Profiling overlay (seed sections + learned entries) for dedup before recording. " +
            "layer=\"playbook\" is the general, project-independent know-how (playbook.md); layer=\"project\" is the " +
            "project-dependent overlay with accepted costs etc. (project-customization.md). Optional scope filters which learned entries are returned.",
            "Unity.MemoryProfiler.GetCustomization")]
        public static CustomizationResult GetCustomization(
            [ToolParameter("Which overlay: \"playbook\" (general, project-independent) or \"project\" (this project's accepted costs / overrides). Default \"project\".")]
            string layer = "project",
            [ToolParameter("Optional scope filter, e.g. \"platform=iOS;type=ParticleSystem\". Returns only learned entries whose scope matches (a missing/'*' scope field matches anything). Omit to return all entries.")]
            string scope = null)
        {
            try
            {
                var path = ResolveOverlayPath(layer, out var resolveErr);
                if (path == null)
                    return new CustomizationResult { layer = layer, error = resolveErr };

                var content = File.ReadAllText(path, Encoding.UTF8);

                // Find entries region markers (neutral; both overlays use the same markers).
                const string startMarker = "<!-- entries:start -->";
                const string endMarker   = "<!-- entries:end -->";
                int startIdx = FindEntriesMarker(content, start: true);
                int endIdx   = FindEntriesMarker(content, start: false);

                if (startIdx < 0 || endIdx < 0 || endIdx < startIdx)
                {
                    // No markers — return file as-is, 0 entries.
                    return new CustomizationResult { layer = layer, resolvedPath = path, content = content, entryCount = 0 };
                }

                // Seed = everything before+including startMarker, and after+including endMarker.
                string seedBefore = content.Substring(0, startIdx + startMarker.Length);
                string seedAfter  = content.Substring(endIdx); // includes endMarker and everything after

                // Entries region (between the two markers).
                string entriesRegion = content.Substring(startIdx + startMarker.Length,
                    endIdx - (startIdx + startMarker.Length));

                Dictionary<string, string> filter = null;
                if (!string.IsNullOrWhiteSpace(scope))
                    filter = ParseScope(scope);

                // Split into individual entry blocks.
                var entryBlocks = new List<string>();
                int pos = 0;
                while (pos < entriesRegion.Length)
                {
                    var m = s_EntryStartRe.Match(entriesRegion, pos);
                    if (!m.Success) break;
                    int blockStart = m.Index;
                    const string entryEnd = "<!-- entry:end -->";
                    int endBlock = entriesRegion.IndexOf(entryEnd, blockStart, StringComparison.Ordinal);
                    if (endBlock < 0) break;
                    int blockEnd = endBlock + entryEnd.Length;
                    entryBlocks.Add(entriesRegion.Substring(blockStart, blockEnd - blockStart));
                    pos = blockEnd;
                }

                // Apply scope filter.
                var included = new List<string>();
                foreach (var block in entryBlocks)
                {
                    if (filter == null || filter.Count == 0)
                    { included.Add(block); continue; }
                    // Extract scope line from block.
                    var sm = s_ScopeLineRe.Match(block);
                    var entryScopeLine = sm.Success ? sm.Groups[1].Value.Trim() : string.Empty;
                    if (ScopeMatches(entryScopeLine, filter))
                        included.Add(block);
                }

                var sb = new StringBuilder();
                sb.Append(seedBefore);
                sb.Append("\n");
                foreach (var b in included)
                { sb.Append(b); sb.Append("\n"); }
                sb.Append(seedAfter);

                return new CustomizationResult
                {
                    layer = layer,
                    resolvedPath = path,
                    content = sb.ToString(),
                    entryCount = included.Count,
                };
            }
            catch (Exception ex)
            {
                return new CustomizationResult { layer = layer, error = ex.Message };
            }
        }

        [AgentTool(
            "Records a curated insight into one Memory Profiling overlay's learned-entries region (additive — advisory text only, never mechanical numbers). " +
            "layer=\"playbook\" writes general, project-independent know-how (playbook.md); layer=\"project\" writes a project-dependent entry such as an accepted cost (project-customization.md). " +
            "If an entry with the same id exists it is MERGED (body updated, observation appended, count incremented); otherwise appended. The seed sections are never touched.",
            "Unity.MemoryProfiler.RecordCustomization")]
        public static RecordCustomizationResult RecordCustomization(
            [ToolParameter("Classification — one of: \"§1\" (threshold), \"§2\" (difficulty), \"§3\" (subsystem), \"§4\" (type group), \"cross-cutting\", \"accepted\" (a project-intentional accepted cost / expected characteristic the analysis should exclude from improvement candidates within scope). \"accepted\" belongs only in the project layer.")]
            string classification,
            [ToolParameter("Short subject line for the entry (e.g. \"ParticleSystem duplicate instances dominate\").")]
            string subject,
            [ToolParameter("The advisory body in markdown. Advice/thresholds/priorities only — never restate or alter mechanical measured numbers (additive invariant).")]
            string body,
            [ToolParameter("Which overlay to write: \"playbook\" (general, project-independent — use only when the insight generalizes to other projects) or \"project\" (this project's accepted costs / overrides). Default \"project\". Rule: if scopeProject names a specific project, use \"project\".")]
            string layer = "project",
            [ToolParameter("Optional scope: target platform (iOS/Android/Editor) or '*'/null for any.")]
            string scopePlatform = null,
            [ToolParameter("Optional scope: project name or genre, or '*'/null for any.")]
            string scopeProject = null,
            [ToolParameter("Optional scope: Unity object type name or native subsystem AreaName, or '*'/null for any.")]
            string scopeType = null,
            [ToolParameter("Optional scope: captureOrigin (Player/Editor) or '*'/null for any.")]
            string scopeCaptureOrigin = null,
            [ToolParameter("Confidence: \"low\" | \"medium\" | \"high\" (default low). Raise as the same insight recurs across snapshots.")]
            string confidence = "low",
            [ToolParameter("A short context note for this observation (e.g. \"iOS build 2024-05-10\"), appended to the entry's observation list.")]
            string sourceContext = null,
            [ToolParameter("Optional stable entry id (dedup key). Omit to derive one from classification+scope+subject. Pass an existing id to update/reinforce that entry.")]
            string id = null)
        {
            try
            {
                // Sanitize all LLM-supplied free text before it is written into the structured overlay file:
                //  - neutralize HTML-comment delimiters so a value can't inject/spoof the <!-- entry ... -->
                //    markers the file is sliced on (which would corrupt parsing on the next read/merge);
                //  - cap length so a runaway body can't bloat the overlay.
                subject       = SanitizeSingleLine(subject, 200);
                body          = SanitizeBody(body, 2000);
                sourceContext = SanitizeSingleLine(sourceContext, 200);
                id            = SanitizeSingleLine(id, 120);

                var path = ResolveOverlayPath(layer, out var resolveErr);
                if (path == null)
                    return new RecordCustomizationResult { layer = layer, error = resolveErr };

                var content = File.ReadAllText(path, Encoding.UTF8);

                const string startMarker = "<!-- entries:start -->";
                const string endMarker   = "<!-- entries:end -->";
                int startIdx = FindEntriesMarker(content, start: true);
                int endIdx   = FindEntriesMarker(content, start: false);
                if (startIdx < 0 || endIdx < 0 || endIdx < startIdx)
                    return new RecordCustomizationResult { layer = layer, resolvedPath = path, error = "overlay file is missing <!-- entries:start --> / <!-- entries:end --> markers" };

                // Normalize scope fields: null/"" → "*".
                string pf = string.IsNullOrEmpty(scopePlatform)      || scopePlatform      == "*" ? "*" : SanitizeSingleLine(scopePlatform, 80);
                string pr = string.IsNullOrEmpty(scopeProject)        || scopeProject        == "*" ? "*" : SanitizeSingleLine(scopeProject, 80);
                string ty = string.IsNullOrEmpty(scopeType)           || scopeType           == "*" ? "*" : SanitizeSingleLine(scopeType, 80);
                string co = string.IsNullOrEmpty(scopeCaptureOrigin)  || scopeCaptureOrigin  == "*" ? "*" : SanitizeSingleLine(scopeCaptureOrigin, 80);

                string entryId = !string.IsNullOrWhiteSpace(id)
                    ? id.Trim()
                    : DeriveEntryId(classification, pf == "*" ? null : pf, pr == "*" ? null : pr,
                                    ty == "*" ? null : ty, co == "*" ? null : co, subject);

                string today = DateTime.Now.ToString("yyyy-MM-dd");
                string entriesRegion = content.Substring(startIdx + startMarker.Length,
                    endIdx - (startIdx + startMarker.Length));

                // Search for existing entry with this id.
                string searchTag = $"<!-- entry:start id={entryId} -->";
                int existingIdx = entriesRegion.IndexOf(searchTag, StringComparison.Ordinal);

                string action;
                int observationCount;
                string newEntriesRegion;

                if (existingIdx >= 0)
                {
                    // ---- MERGE path ----
                    const string entryEnd = "<!-- entry:end -->";
                    int existingEndIdx = entriesRegion.IndexOf(entryEnd, existingIdx, StringComparison.Ordinal);
                    if (existingEndIdx < 0)
                        return new RecordCustomizationResult { layer = layer, resolvedPath = path, id = entryId, error = "Malformed entry: missing <!-- entry:end --> for id=" + entryId };
                    int existingBlockEnd = existingEndIdx + entryEnd.Length;

                    string oldBlock = entriesRegion.Substring(existingIdx, existingBlockEnd - existingIdx);

                    // Parse existing observations line: "- **observations**: <count> (<ctx1>; <ctx2>; ...)"
                    var obsRe = new Regex(@"-\s*\*\*observations\*\*:\s*(\d+)\s*(?:\(([^)]*)\))?");
                    var obsMatch = obsRe.Match(oldBlock);
                    int existingCount = obsMatch.Success ? int.Parse(obsMatch.Groups[1].Value) : 0;
                    string existingContexts = obsMatch.Success ? obsMatch.Groups[2].Value.Trim() : string.Empty;

                    // Add sourceContext if new and non-empty.
                    var ctxList = new List<string>();
                    foreach (var ctx in existingContexts.Split(';'))
                    {
                        var t = ctx.Trim();
                        if (!string.IsNullOrEmpty(t)) ctxList.Add(t);
                    }
                    bool added = false;
                    if (!string.IsNullOrWhiteSpace(sourceContext)
                        && !ctxList.Contains(sourceContext.Trim(), StringComparer.OrdinalIgnoreCase))
                    {
                        ctxList.Add(sourceContext.Trim());
                        added = true;
                    }
                    observationCount = existingCount + (added ? 1 : 0);
                    // If no sourceContext but we're merging, still increment count.
                    if (!added) observationCount = existingCount + 1;

                    // Build merged block: update confidence, observations, updated, body.
                    string newBlock = BuildEntryBlock(entryId, classification, subject,
                        pf, pr, ty, co, confidence, observationCount, ctxList, today, body);

                    newEntriesRegion = entriesRegion.Substring(0, existingIdx)
                        + newBlock
                        + entriesRegion.Substring(existingBlockEnd);
                    action = "merged";
                }
                else
                {
                    // ---- ADD path ----
                    var ctxList = new List<string>();
                    if (!string.IsNullOrWhiteSpace(sourceContext)) ctxList.Add(sourceContext.Trim());
                    observationCount = 1;

                    string newBlock = BuildEntryBlock(entryId, classification, subject,
                        pf, pr, ty, co, confidence, observationCount, ctxList, today, body);

                    // Insert just before the end marker (preserve any trailing newline before marker).
                    newEntriesRegion = entriesRegion.TrimEnd() + "\n" + newBlock + "\n";
                    action = "added";
                }

                // Reconstruct full file: seedBefore + newEntriesRegion + endMarker + seedAfterEnd
                string seedBefore = content.Substring(0, startIdx + startMarker.Length);
                string seedAfterEnd = content.Substring(endIdx); // starts with endMarker
                string newContent = seedBefore + "\n" + newEntriesRegion + seedAfterEnd;

                File.WriteAllText(path, newContent, s_Utf8NoBom);

                return new RecordCustomizationResult
                {
                    layer = layer,
                    resolvedPath = path,
                    id = entryId,
                    action = action,
                    observationCount = observationCount,
                };
            }
            catch (Exception ex)
            {
                return new RecordCustomizationResult { layer = layer, error = ex.Message };
            }
        }

        // Build a canonical entry block string (no trailing newline after <!-- entry:end -->).
        static string BuildEntryBlock(string id, string classification, string subject,
            string pf, string pr, string ty, string co,
            string confidence, int obsCount, List<string> ctxList, string updated, string body)
        {
            string ctxStr = ctxList != null && ctxList.Count > 0
                ? $" ({string.Join("; ", ctxList)})"
                : string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine($"<!-- entry:start id={id} -->");
            sb.AppendLine($"### [{classification}] {subject}");
            sb.AppendLine($"- **scope**: platform={pf}; project={pr}; type={ty}; captureOrigin={co}");
            sb.AppendLine($"- **confidence**: {confidence}");
            sb.AppendLine($"- **observations**: {obsCount}{ctxStr}");
            sb.AppendLine($"- **updated**: {updated}");
            sb.AppendLine();
            sb.AppendLine(body);
            sb.Append("<!-- entry:end -->");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- lifecycle

        static void UnloadInternal()
        {
            if (s_Snapshot != null && s_OwnsSnapshot)
            {
                try { s_Snapshot.Dispose(); } catch { }
            }
            s_Snapshot = null;
            s_OwnsSnapshot = false;
            s_ComparisonDrillTarget = null;
        }

        // Release the comparison pair. Only dispose snapshots WE built from a file (s_OwnsA/B);
        // window-borrowed snapshots are owned by the Memory Profiler window.
        static void UnloadComparisonInternal()
        {
            // If the drill target aliases a pair member, drop the alias first so we don't leave
            // s_Snapshot dangling at a disposed snapshot.
            if (s_Snapshot != null && (ReferenceEquals(s_Snapshot, s_SnapshotA) || ReferenceEquals(s_Snapshot, s_SnapshotB)))
            {
                s_Snapshot = null; s_OwnsSnapshot = false; s_ComparisonDrillTarget = null;
            }
            if (s_SnapshotA != null && s_OwnsA) { try { s_SnapshotA.Dispose(); } catch { } }
            if (s_SnapshotB != null && s_OwnsB) { try { s_SnapshotB.Dispose(); } catch { } }
            s_SnapshotA = null; s_SnapshotB = null;
            s_OwnsA = false; s_OwnsB = false;
        }

        // ================================================================ return POCOs
        // [Serializable] public fields (Unity JsonUtility convention). See file header re: properties.

        // ---- Initialize
        [Serializable] public class LoadedSnapshotState
        {
            public bool windowOpen;      // Memory Profiler window is open
            public string loadedMode;    // "none" | "single" | "comparison"
            public bool compareMode;     // raw MP window Compare toggle (may be on with only one snapshot loaded)
            public string platform;      // single mode: RuntimePlatform of the loaded snapshot
            public string platformA;     // comparison mode: baseline (Base)
            public string platformB;     // comparison mode: candidate (Compared)
            public string note;          // guidance when nothing / a half-loaded pair is present
        }
        [Serializable] public class AvailableSnapshots
        {
            public string captureDirectory;  // MemoryProfilerSettings.AbsoluteMemorySnapshotStoragePath
            public bool directoryExists;
            public string productName;        // current project name, used for the matchesCurrentProject flag
            public int totalCount;            // total .snap files found (may exceed snapshots.Count when capped)
            public List<SnapshotCandidate> snapshots; // sorted: project match first, then most-recent
            public string note;               // guidance when the directory is missing / empty / truncated
        }
        [Serializable] public class SnapshotCandidate
        {
            public string fileName;
            public string filePath;           // absolute path to pass to Initialize(filePath)
            public long sizeBytes;
            public string lastModifiedUtc;    // ISO 8601 ("o") — lexically sortable
            public bool matchesCurrentProject; // filename contains the current project name (case-insensitive)
        }
        [Serializable] public class InitializeResult
        {
            public string error;          // non-null only when no snapshot could be resolved
            public string source;         // "loaded" | "file"
            public string filePath;       // null when analyzing the loaded snapshot
            public string snapshotName;   // capture file name (no extension) — state this at the top of the analysis
            public string productName;    // captured project's name (MetaData.ProductName); null for legacy "Unknown Project" captures. Use for project scope-matching.
            public string runtimePlatform;
            public string captureOrigin;  // Player | Editor | Uncertain
            public CaptureOriginSignals captureOriginSignals;
            public SnapshotAvailability availability;
        }
        [Serializable] public class CaptureOriginSignals
        {
            public bool platformIsEditor;       // TargetInfo.RuntimePlatform is an Editor platform
            public bool areaNameEditorSignature; // AreaName heuristic detected EditorOnly areas
            public bool isEditorCaptureFlag;    // MetaData.IsEditorCapture (legacy platform-string derived)
            public bool legacyPlatformKnown;    // legacy MetaData.Platform was recorded → IsEditorCapture meaningful
        }
        [Serializable] public class SnapshotAvailability
        {
            public bool isSupportedFormat;
            public bool hasSystemMemoryRegionsInfo;
            public bool hasSystemMemoryResidentPages;
            public bool hasManagedHeap;
            public bool hasNativeObjects;
            public bool hasAllocationCallstacks; // capture has -enable-memoryprofiler-callstacks data → Unrooted attribution possible
        }

        // ---- GetMemoryOverview
        [Serializable] public class MemoryOverview
        {
            public bool residentAvailable;
            public List<AllocatedBucket> allocatedDistribution;
            public ulong totalAllocatedCommitted;
            public List<ManagedBucket> managedBreakdown;
            public ResidentTotal residentTotal;
            public Fragmentation fragmentation;
        }
        [Serializable] public class AllocatedBucket
        {
            public string name;
            public ulong committed;
            public ulong? resident;
            public bool residentUnavailable;
            public string categoryId;
        }
        [Serializable] public class ManagedBucket
        {
            public string name;
            public ulong committed;
            public ulong? resident;
        }
        [Serializable] public class ResidentTotal
        {
            public bool available;
            public ulong? committed;
            public ulong? resident;
        }
        [Serializable] public class Fragmentation
        {
            public ulong emptyHeapSpace;
            public double emptyHeapRatioOfManaged;
        }

        // ---- GetTopUnityObjectCategories
        [Serializable] public class TopUnityObjectCategories
        {
            public bool residentAvailable;
            public ulong totalSnapshotCommitted;
            public ulong? totalSnapshotResident;
            public List<TypeGroup> groups;
        }
        [Serializable] public class TypeGroup
        {
            public string typeName;
            public ulong nativeCommitted, managedCommitted, gpuCommitted, totalCommitted;
            public ulong? totalResident;
            public long objectCount;
        }

        // ---- GetNativeSubsystems
        [Serializable] public class NativeSubsystems
        {
            public List<NativeSubsystem> subsystems;
        }
        [Serializable] public class NativeSubsystem
        {
            public string areaName;
            public ulong committed;
            public long rootRefCount;
            public string classification;
            public List<string> sampleObjectNames;
        }

        // ---- GetUnityObjectTypeDetail
        [Serializable] public class UnityObjectTypeDetail
        {
            public string typeName;
            public bool residentAvailable;
            public string comparisonDrillTarget; // "A"|"B" when drilling a comparison pair; null in single-snapshot mode
            public ObjectDetailSummary summary;
            public List<ObjectDetail> objects;
        }
        [Serializable] public class ObjectDetailSummary
        {
            public long objectCount;
            public ulong totalNative, totalManaged, totalGpu;
            public long persistentCount, duplicateSuspectCount;
        }
        [Serializable] public class ObjectDetail
        {
            public string name;
            public ulong nativeCommitted, managedCommitted, gpuCommitted, totalCommitted;
            public ulong? totalResident;
            public string instanceId;
            public long nativeObjectIndex;
            public bool isDontDestroyOnLoad, isPersistent;
            public int refCount;
        }

        // ---- GetPotentialDuplicates
        [Serializable] public class PotentialDuplicates
        {
            public string typeNameFilter;
            public string comparisonDrillTarget; // "A"|"B" when drilling a comparison pair; null in single-snapshot mode
            public List<DuplicateGroup> duplicateGroups;
        }
        [Serializable] public class DuplicateGroup
        {
            public string typeName, name;
            public ulong instanceSize;
            public int count;
            public ulong wastedEstimate;
        }

        // ---- GetNativeSubsystemDetail
        [Serializable] public class NativeSubsystemDetail
        {
            public string areaName;
            public bool residentAvailable;
            public string comparisonDrillTarget; // "A"|"B" when drilling a comparison pair; null in single-snapshot mode
            public NativeSubsystemSummary summary;
            public List<NativeRootDetail> roots;
        }
        [Serializable] public class NativeSubsystemSummary
        {
            public long rootRefCount;
            public ulong totalCommitted;
            public ulong? totalResident;
        }
        [Serializable] public class NativeRootDetail
        {
            public string objectName;
            public ulong committed;
            public ulong? resident;
            public long rootId;
        }

        // ---- GetObjectRetentionPath
        [Serializable] public class ObjectRetentionPath
        {
            public long nativeObjectIndex;
            public string comparisonDrillTarget; // "A"|"B" when drilling a comparison pair; null in single-snapshot mode
            public bool pathDataAvailable;
            public bool isReachableFromRoot;
            public string note;
            public List<RetentionNode> path; // ordered root -> object
        }
        [Serializable] public class RetentionNode
        {
            public string name;
            public string typeName;
            public string sourceKind;
            public long depth;
        }

        // ---- GetUnrootedAllocationBreakdown (step 9)
        [Serializable] public class UnrootedAllocationBreakdown
        {
            public bool available;               // per-site callstack attribution available (gate: callstacks captured)
            public string note;                  // guidance when !available, or "call Initialize first"
            public string comparisonDrillTarget; // "A"|"B" when drilling a comparison pair; null in single-snapshot mode
            public bool residentAvailable;       // resident meaningful only when capture has system memory regions info
            public ulong totalUnrootedCommitted; // always computed; memory-map footprint (matches MP "Unrooted" committed)
            public ulong? totalUnrootedResident; // memory-map resident footprint (matches MP "Unrooted" resident); null if unavailable
            public long unrootedAllocationCount; // always computed (# of RootReferenceId <= 0 allocations)
            public int siteCount;                // distinct allocation sites (0 when !available)
            public List<UnrootedSite> topSites;  // ranked by committedBytes desc, capped at topN; empty when !available
        }
        [Serializable] public class UnrootedSite
        {
            public ulong siteId;
            public ulong committedBytes;         // memory-map committed footprint for this site
            public ulong? residentBytes;         // memory-map resident footprint; null if unavailable
            public long allocationCount;
            public string memoryLabel;           // memory label the site allocated under
            public string frameOrigin;           // "user-code" | "engine-managed" | "native" (heuristic, from callstack)
            public bool appActionable;           // heuristic: true only when frameOrigin == "user-code"
            public string actionabilityHint;
            public string callstack;             // readable, simplified, non-clickable
        }

        // ---- InitializeComparison (step 8)
        [Serializable] public class ComparisonInitializeResult
        {
            public string error;          // non-null only when the pair could not be resolved
            public string sourceA;        // "loaded" | "file"
            public string sourceB;
            public string filePathA;      // null when using the window's Compare pair
            public string filePathB;
            public string snapshotNameA;  // A (baseline) capture file name (no extension) — state A=…/B=… at the top of the diff
            public string snapshotNameB;  // B (candidate/later) capture file name (no extension)
            public string productNameA;   // A captured project's name; null for legacy "Unknown Project" captures
            public string productNameB;   // B captured project's name; null for legacy "Unknown Project" captures
            public string platformA;
            public string platformB;
            public string captureOriginA; // Player | Editor | Uncertain
            public string captureOriginB;
            public bool residentAvailableA;
            public bool residentAvailableB;
            public bool residentAvailableBoth; // resident delta is only meaningful when true
            public string drillTarget;    // which snapshot the single-snapshot drill tools now target ("B" by default)
        }

        // ---- SetComparisonDrillTarget (step 8)
        [Serializable] public class ComparisonDrillTargetResult
        {
            public string error;          // non-null when no pair is loaded or target invalid
            public string drillTarget;    // "A" | "B"
            public string platform;
            public string captureOrigin;  // Player | Editor | Uncertain
            public bool residentAvailable;
        }

        // ---- GetComparisonOverview (step 8) — B − A (positive delta = grew from baseline to candidate)
        [Serializable] public class SnapshotComparisonOverview
        {
            public string error;          // non-null when no pair is loaded
            public bool residentAvailableA;
            public bool residentAvailableB;
            public bool residentAvailableBoth;
            public ulong totalCommittedA;
            public ulong totalCommittedB;
            public long totalCommittedDelta;
            public ulong? totalResidentA;
            public ulong? totalResidentB;
            public long? totalResidentDelta;
            public int changedTypeGroupCount;  // total CHANGED type groups (before topN cut) — if > typeGroupDeltas.Count the list is truncated
            public int changedSubsystemCount;  // total CHANGED subsystems (all returned; not capped)
            public List<TypeGroupDelta> typeGroupDeltas; // changed only, ranked by |committedDelta|, capped at topN
            public List<SubsystemDelta> subsystemDeltas; // changed only, ranked by |committedDelta|
        }
        [Serializable] public class TypeGroupDelta
        {
            public string typeName;
            public ulong committedA, committedB;
            public long committedDelta;
            public ulong? residentA, residentB;   // null unless residentAvailableBoth
            public long? residentDelta;
            public int countA, countB, countDelta;
            public string changeKind;             // new | grew | shrank | freed | unchanged
        }
        [Serializable] public class SubsystemDelta
        {
            public string areaName;
            public ulong committedA, committedB;
            public long committedDelta;
            public ulong? residentA, residentB;   // null unless resident joinable in both
            public long? residentDelta;
            public string classification;         // Actionable/Redirect/Artifact/InformOnly/EditorOnly/Unknown
            public string changeKind;             // new | grew | shrank | freed | unchanged
        }

        // ---- GetComparisonUnrootedBreakdown (step 9 — comparison mode)
        [Serializable] public class ComparisonUnrootedBreakdown
        {
            public string error;                  // non-null when no pair is loaded
            public bool available;                // label/site diff available (BOTH snapshots have callstacks)
            public string note;                   // guidance when !available (total delta still provided)
            public bool residentAvailableBoth;
            public ulong totalUnrootedCommittedA, totalUnrootedCommittedB;
            public long totalUnrootedCommittedDelta;      // B − A
            public ulong? totalUnrootedResidentA, totalUnrootedResidentB;
            public long? totalUnrootedResidentDelta;
            public int changedLabelCount;         // changed memory labels (all returned)
            public int changedSiteCount;          // changed callstack sites (before topN cut)
            public List<UnrootedLabelDelta> labelDeltas; // changed only, ranked by |committedDelta|
            public List<UnrootedSiteDelta> siteDeltas;   // changed only, ranked by |committedDelta|, capped at topN
        }
        [Serializable] public class UnrootedLabelDelta
        {
            public string memoryLabel;
            public ulong committedA, committedB;
            public long committedDelta;
            public ulong? residentA, residentB;
            public long? residentDelta;
            public long countA, countB, countDelta;
            public string changeKind;             // new | grew | shrank | freed
        }
        [Serializable] public class UnrootedSiteDelta
        {
            public ulong committedA, committedB;
            public long committedDelta;
            public ulong? residentA, residentB;
            public long? residentDelta;
            public long countA, countB, countDelta;
            public string memoryLabel;
            public string frameOrigin;            // "user-code" | "engine-managed" | "native" (heuristic)
            public string callstack;              // representative readable callstack (from B if present, else A)
            public string changeKind;             // new | grew | shrank | freed
        }

        // ---- GetCustomization
        [Serializable] public class CustomizationResult
        {
            public string layer;         // "playbook" | "project"
            public string resolvedPath;
            public string content;       // seed sections + (filtered) learned entries
            public int entryCount;       // learned entries included
            public string error;         // non-null only on failure
        }

        // ---- RecordCustomization
        [Serializable] public class RecordCustomizationResult
        {
            public string layer;         // "playbook" | "project"
            public string resolvedPath;
            public string id;            // created or merged id
            public string action;        // "added" | "merged"
            public int observationCount;
            public string error;
        }
    }
}
