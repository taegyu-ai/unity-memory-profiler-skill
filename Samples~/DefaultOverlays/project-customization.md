# Memory Profiler Skill — Project Customization Overlay (project-dependent)

The **project-dependent layer** of the analysis skill's optional overlay (the counterpart to the general `playbook.md` layer).
It applies **additively** on top of the baseline and `playbook.md` (the general layer) — it never changes mechanical numbers and only adds advice, threshold overrides, and accepted-cost annotations that are **specific to this project**.

> **This file = information valid only for this project.** Examples: a deliberately large sprite atlas / several 4K textures kept resident on purpose, a big ECS entity system in a specific scene, a stop threshold tuned to this project's characteristics.
> **Do not apply it to another project** (project A's accepted cost would wrongly suppress project B's warning) → this overlay **travels with the project** and is, by default, **not shared or distributed**.
> Do not put generalizable know-how (valid for any project) here — put it in `playbook.md` → boundary rule: **if the insight is valid only for this project (accepted costs, project-tuned overrides), it goes here; if it generalizes to other projects, playbook**.

> **How to use**
> - A new project **starts with this file empty** (or deleted). As you run analyses, accumulate entries via the learning skill `unity-memory-customization-skill` (Curator) with `layer=project`, or hand-edit it.
> - **Foundation for health-check automation**: "what this project has accepted" is gathered in this one file, so periodic checks can skip the accepted costs and surface only new deviations.
> - **Load order**: `baseline → playbook → project-customization` (this file is the most specific → it overrides the general layer). Both apply scope-matched entries only.

---

## Format (the canonical block shared with playbook.md)
Only the `<!-- entries:start -->` ~ `<!-- entries:end -->` region at the bottom is managed by the store tool (`RecordCustomization`, `layer=project`). The block format and fields are identical to the "Learned Entries format spec" in `playbook.md`. Differences:
- `classification`: mainly **`accepted`** (accepted cost / intentional characteristic, §A below) + project-specific `§1` (threshold override) and `§3`/`§4` (project-specific recommendations).
- `scope`: **set `project` to `*`** — this file itself is the project boundary (every entry in it is project-dependent by definition), and the analysis skill cannot read a project name from a snapshot (`Initialize` does not expose it), so a concrete value would fail scope-matching and silently disable the entry. Record the originating project in `sourceContext`/`observations` instead. For `accepted`, scope `type`/`subsystem` tightly.

---

## A. Accepted Costs & Expected Characteristics (`accepted`)
Memory costs this project **deliberately incurs**. Examples:
- "Keeping 10 resident 4K Texture2D assets is required for a special rendering purpose — not an improvement target" (scope: project=MyProject, type=Texture2D)
- "Several large sprite atlases are used intentionally" (scope: project=…, type=Texture2D/Sprite)
- "The large ECS entity system in the battle scene is a cost we accept for gameplay reasons" (scope: project=…, type=…)

**How the analysis skill applies this (additive — numbers unchanged)**: when a candidate (type/subsystem/group) matches a scope-matched `accepted` entry,
mark that candidate **`✅ accepted (project)` in the ranked list/Recommend and exclude it from improvement candidates (or demote it to a separate 'accepted' section)**, with a one-line reason.
**The measured numbers (resident/committed/count) are still shown** — only the judgment/recommendation is suppressed.

> ⚠️ `accepted` is powerful, so scope it **tightly** (project + type/subsystem). If it is too broad it can mask a real regression.

## B. Project-specific overrides (optional)
- **Threshold override (`§1`)**: stop-coverage/priority criteria tuned to this project (overrides the playbook §1 defaults for this project only). E.g. "mobile target, so stop coverage is 65%."
- **Project-specific recommendations (`§3`/`§4`)**: subsystem/type recommendations specialized to this project's pipeline/conventions (general recommendations stay in the playbook).

---

## Learned Entries (project)
The region where the learning skill accumulates **project-dependent** entries with `layer=project`. **Only the `RecordCustomization` tool manages this region.**

<!-- entries:start -->
<!-- entries:end -->
