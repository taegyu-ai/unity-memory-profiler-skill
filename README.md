# Memory Profiler Skill (Preview)

**Language:** English | [한국어](Documentation~/README.ko.md)

A **preview, user-installable skill** for analyzing Unity **Memory Profiler** snapshots through the **Unity AI Assistant** — usable today, ahead of an official built-in skill.

Ask the Assistant about a project's memory footprint and it runs a **Wide Survey**: it ranks improvement candidates across Unity object types, native subsystems, and the managed heap by **resident** memory, then drills into the groups you pick (sizes, duplicates, retention paths) and suggests reductions.

## What's in this package

| Folder | What it is |
|---|---|
| `Editor/` | Editor assembly exposing the tools both skills call, as Unity AI Assistant `[AgentTool]`s (`Unity.MemoryProfiler.*`): 13 analysis tools (single-snapshot survey, 2-snapshot comparison, Unrooted callstack breakdown) + 2 customization tools. Wraps the Memory Profiler package's own model builders — measured numbers are byte-accurate. |
| `AIAssistantSkills/unity-memory-profiling-skill/` | The **analysis** skill (`SKILL.md` + `references/`). Drives the 2-phase survey, the 2-snapshot comparison mode, and the Unrooted allocation drill. |
| `AIAssistantSkills/unity-memory-customization-skill/` | The **customization** skill (experimental). Curates two overlays: project-accepted costs and intentional characteristics (project layer) and general portable know-how (playbook layer). Records entries via `RecordCustomization(layer=…)`; the analysis skill loads both (`baseline → playbook → project-customization`) and marks matching items `✅ accepted (project)` so health checks surface only actionable deviations. |
| `Samples~/DefaultOverlays/` | Optional starter templates for the two overlay files (see **Customization data lives in your project**, below). Not part of the installed package content — import it explicitly if you want it. |

## Requirements

- Unity **6000.3.13+**
- `com.unity.memoryprofiler` **>= 1.1.0**
- `com.unity.ai.assistant` (Unity AI Assistant)
- The Memory Profiler package must be consumed **from the registry** (not embedded as a local source package). This package's Editor assembly reaches the Memory Profiler package's internals by naming its asmdef `Unity.MemoryProfiler.Editor.Tests` — the package already has `[assembly: InternalsVisibleTo("Unity.MemoryProfiler.Editor.Tests")]`, so no package edit is needed. If `com.unity.memoryprofiler` is **embedded**, its own test assembly already claims that name → conflict; a registry install leaves the name free. (This constraint is about `com.unity.memoryprofiler`, not about this skill package itself — installing *this* package via git URL is the normal, supported path.)

## Install

**Package Manager (recommended)**: Window ▸ Package Manager ▸ `+` ▸ *Add package from git URL* ▸ paste:

```
https://github.com/taegyu-ai/unity-memory-profiler-skill.git
```

Or add it directly to `Packages/manifest.json`:
```json
"com.taegyu-ai.unity-memory-profiler-skill": "https://github.com/taegyu-ai/unity-memory-profiler-skill.git"
```

**Local/embedded source (for development or debugging the skill itself)**: clone this repo and either add it as a `file:` dependency in `manifest.json`, or drop the folder directly under your project's `Packages/`.

After install:
1. Let Unity compile, then open **Project Settings ▸ AI ▸ Skills**, **Rescan**, and confirm both skills are active (they'll show as installed from a **Package**, not from `Assets/`).
2. Open the **Memory Profiler** window and load a `.snap` capture.
3. Start a **new** Assistant chat and ask it to analyze the snapshot's memory.

> **Why a new chat each time the skill changes?** The Assistant sends skill *definitions* (`SKILL.md`, tool list) only on the *first prompt of a new conversation*. After installing/updating this package, start a new chat for the updated skill definition to take effect. You can confirm activation in the chat's **Thoughts** (`Activate Skill: unity-memory-profiling-skill`).

### Customization data lives in your project, not in this package

The customization skill records project-specific notes (accepted costs, tuned thresholds) into two files
that must **survive this package being updated or removed** — so they are never installed as part of the
package itself. They live under your project's `Assets/`, owned and version-controlled by your project,
exactly like any other project asset.

- **Baseline analysis works immediately after installing the package — no extra step required.**
- To start with editable defaults (general recommendations, default thresholds) instead of an empty slate,
  open **Package Manager ▸ this package ▸ Samples** and click **Import** next to "Default Overlays". This
  copies a starter `playbook.md`/`project-customization.md` into `Assets/Samples/.../DefaultOverlays/`,
  where the customization/analysis skills will find and use them.
- If you skip the import, the skill just runs baseline-only until you either import the sample or record
  your first customization (which creates the files itself).

## Troubleshooting

- **The Assistant answers about memory but never calls these tools** (falls back to Unity's built-in profiler skill): the skill isn't active. Check **Project Settings ▸ AI ▸ Skills**, **Rescan**, and ensure both skills are *Allow*ed; then use a **new** chat. To isolate whether the tools registered, ask explicitly: *"Use the Unity.MemoryProfiler tools to initialize and give a memory overview."*
- **`SKILL.md` doesn't load at all**: check the frontmatter. `required_editor_version` must be a plain `MAJOR.MINOR.PATCH` — a beta/alpha suffix like `6000.4.0b7` is *not a valid version constraint* and silently fails the whole skill. `required_packages` must be a **map** (`com.unity.memoryprofiler: ">=1.1.0"`), not a list.
- **Compile error `CS0246` on `[AgentTool]`/`[ToolParameter]`**: the asmdef must reference the assembly that provides them, `Unity.AI.Assistant.Runtime` (namespace `Unity.AI.Assistant.FunctionCalling`). If your `com.unity.ai.assistant` version exposes those attributes from a different assembly, find it in Package Manager and update the reference.
- **`internal` access errors** from this package's Editor assembly: confirm its asmdef name is exactly `Unity.MemoryProfiler.Editor.Tests` and `com.unity.memoryprofiler` is **not embedded** (see Requirements).
- **A recorded customization isn't reflected in analysis**: the analysis skill reads overlays live via the `GetCustomization` tool (not a cached skill reference bundle), so a newly recorded entry is picked up on the **very next** analysis call — no Rescan or new chat needed for overlay *content* changes (Rescan + new chat is still needed after installing/updating the *package itself*). If it's still not reflected, check the entry's `scope` actually matches the snapshot (platform/type/captureOrigin); for project-layer entries leave `project=*` (the file itself is already the project boundary).

## Usage

- **Analyze**: load a capture, then ask e.g. *"survey this snapshot's memory"* / *"what's using the most memory?"*. The analysis skill activates and walks the survey.
- **Compare two snapshots**: load a Compare pair in the Memory Profiler window (or give two `.snap` paths), then ask e.g. *"compare these two snapshots — what grew?"* / *"find the leak between these captures"*. The skill diffs types, subsystems, and totals (B − A), highlights new/grown groups, and drills into the changed ones.
- **Unrooted (native) allocations**: if the capture was taken with the `-enable-memoryprofiler-callstacks` player argument (Unity 6000.3+), asking about a large `Native > Unrooted` group breaks it down by allocation callstack and classifies each site as user-code / engine-managed / native. Without callstacks the skill still reports the Unrooted total and suggests how to recapture.
- **Customize** (experimental): after an analysis, tell the customization skill what's intentional, e.g. *"these 4K textures are required for our art style — don't flag them"*. It generalizes and scopes the note, confirms with you, and records it; later analyses mark matching items `✅ accepted (project)` and drop them from the candidate list. Measured numbers are always still shown — only the judgment is suppressed.

## Scope & status

- **Player captures only.** Editor-capture memory is distorted (Native/Subsystem analysis is unreliable); the skill detects and declines Editor captures.
- **Metric**: prefers **resident** (physical) memory; falls back to committed when resident is unavailable (older captures, some Android), stating the caveat.
- **Preview status**: the analysis skill is validated end-to-end against real captures and an independent oracle. The **customization skill is experimental** — its full loop (record → persist → reload → scope-match → `✅ accepted` exclusion in a fresh analysis) has been validated end-to-end in the Assistant runtime, but the curation UX is still evolving.
- This is an **unofficial preview**, independent of any future official Memory Profiler skill.
