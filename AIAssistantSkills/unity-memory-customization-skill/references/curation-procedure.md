# Curation Procedure

Detailed procedure reference — an expanded version of the 6-step summary in `unity-memory-customization-skill/SKILL.md`.

---

## Entry Schema (mirrors the common format spec shared by both overlays)

Each entry is recorded into one of two overlays (the `layer` from Step 2b):
- `layer=playbook` → `playbook.md` (general, project-independent)
- `layer=project` → `project-customization.md` (project-specific; `accepted` only goes here)

Both files record entries in the `<!-- entries:start -->` ~ `<!-- entries:end -->` region as a canonical block (same format):

```
<!-- entry:start id=<slug> -->
### [<classification>] <subject>
- **scope**: platform=<iOS|Android|Editor|*>; project=*; type=<typeName|areaName|*>; captureOrigin=<Player|Editor|*>
- **confidence**: <low|medium|high>
- **observations**: <count> (<context1>; <context2>; ...)
- **updated**: <YYYY-MM-DD>

<body — markdown advice. If §1, include the threshold override + rationale. Do not alter machine-measured figures (additive).>
<!-- entry:end -->
```

### Field rules

| Field | Rule |
|---|---|
| `layer` | `playbook` (general) or `project` (project-specific). Decided in Step 2b. Determines which file the entry is recorded into. `accepted` is `project`-only. |
| `id` | The **dedup key** — if the id matches an existing entry, its body is updated, an observation is appended, and count is incremented. **For a new entry, call with an empty id** — the tool (`DeriveEntryId`) derives a canonical slug (e.g. `s4.ios.texture2d.astc-mobile`, `acc.` prefix, dot-separated) from `classification+scope+subject`. If the Curator constructs the slug itself and passes it directly, it can diverge from the tool's derivation rule (e.g. `accepted_..._...` with underscores), which **causes the same insight to fail dedup and accumulate duplicates in the next session**. **Only when updating an existing entry**, pass back the **exact id** of that entry as returned by `GetCustomization`, unchanged. |
| `classification` | `§1` / `§2` / `§3` / `§4` / `cross-cutting` / `accepted`. See the "Classification Guide" below. |
| `scope` | All 4 fields must be specified. Use `*` where not applicable — **`project` is always `*`** (both layers; see the `accepted` note below). The analysis skill only applies entries whose scope matches. |
| `confidence` | `low` (1 snapshot) / `medium` (2-3) / `high` (4+). Managed by the Curator — see "Confidence Upgrade Criteria" below. |
| `observations` | A count plus source context in parentheses (snapshot name, date, project). Count increments on merge. |
| `body` | Markdown advice. If §1, include the threshold override value + rationale. **Must never include machine-measured figures (resident bytes, object count).** |

---

## Step 1 — Capture

Collect the user's experiential input. Any single one of the following is not sufficient:
- "The texture was large" → scope/rationale unclear
- "This is a problem" → not generalizable

Confirm the following:
1. Under what circumstances did this occur (platform, project type)?
2. Which type/subsystem was involved?
3. What action actually worked (or what action is expected to work)?
4. Is this insight a one-off case, or a recurring pattern?

---

## Step 2 — Generalize + Scope

### Scope Generalization Guide

Scope is a **contract for the applicability range**. Narrower scope applies more precisely; broader scope can influence more analyses but carries a higher risk of misapplication.

| When to narrow by platform | Example |
|---|---|
| Behavior differs between mobile/PC/console | iOS uses ASTC, Android uses ETC2 — record separately with platform=iOS |
| Memory budget is platform-dependent | Mobile stop threshold differs from PC |
| captureOrigin=Editor is out of scope for analysis v1 | Editor captures are almost always scoped out |

| Project-tied insight? | Handling (the scope's `project` field stays `*` either way) |
|---|---|
| Insight is tied to a specific project's architecture ("this project doesn't use Addressables, so AssetBundle recommendations aren't needed") | Record it in the **project layer** (Step 2b) — the file itself is the project boundary |
| Threshold depends on genre/scale (hyper-casual 50MB budget vs AAA mobile 350MB) | Note the genre in the body/`sourceContext`; if project-tuned, use the project layer |
| Generalizes to any project (e.g. duplicate-texture detection) | Playbook layer |

| When to narrow by type/subsystem | Example |
|---|---|
| Applies only to a specific Unity type | ASTC advice that applies only to `Texture2D` |
| Subsystem grouped by AreaName | `SerializedFile`, `AssetBundle` |
| For general-purpose insights, use `*` | "Drill starting from the top 10% resident group" is cross-cutting |

### Step 2b — Choosing a Layer (`playbook` vs `project`)
Once scope is set, decide which overlay to record into:
- **`playbook`** (general): insights valid for other projects too — perspective, default thresholds, difficulty rating, general type/subsystem recommendations. Record the originating project in `sourceContext`/`observations`, **not** in the scope's `project` field (a concrete value never scope-matches).
- **`project`** (project-specific): unique to *this* project — `accepted` acceptance costs, intentional characteristics, project-scoped threshold overrides. **Set the scope's `project` to `*`** here (the file location itself is the project boundary — see the `accepted` note above).
- **Rule**: if the insight is valid *only for this project* (`accepted` / project-scoped override), use `project`. If it generalizes to other projects, use `playbook`. If uncertain, use `project` (narrower and safer). **The criterion for choosing the layer is whether the insight is project-specific, not the scope's `project` value.**

---

## Step 3 — Classification Guide

| Classification | Applies to | Example |
|---|---|---|
| `§1` | Threshold overrides — stop-condition X/Y%, high-resident criteria, priority-mapping changes | "For this project, a stop coverage of 65% is appropriate" |
| `§2` | Difficulty/analyzability rating for a group or type | "This project's RenderTexture is high-difficulty due to a studio-specific pipeline" |
| `§3` | Recommendation for an AreaName-keyed subsystem | Additional Recommend body text for `SerializedFile` |
| `§4` | Recommendation for a type group | `Texture2D` — empirical guidance recommending ASTC 512 or below on iOS |
| `cross-cutting` | Signal/pattern spanning multiple groups | "For this project, duplicate detection is always top priority" |
| `accepted` | **A project's intentionally accepted cost/characteristic** — something the analysis should exclude from improvement candidates (§5). The measured figure is still shown as-is; only the judgment is suppressed. | "10 resident 4K Texture2Ds are required for a special purpose — not an improvement target"; "Large ECS entity systems in battle scenes are an accepted cost" |

> `accepted` is powerful, so scope it **narrowly by type/subsystem** (e.g. `type=ParticleSystem`). Too broad a scope can mask a genuine regression. In the body, include a one-line reason for why this is an intentional cost.
> **Set `project` to `*`** — `project-customization.md` is a *per-project file*, so every entry inside it already belongs to this project (file location = project boundary). Moreover, the analysis skill cannot read the project name from a snapshot (`Initialize` does not expose the product name), so a concrete `project` value will fail scope-matching and instead **cause valid entries to be skipped**. This applies to **both layers**: record the originating project in `sourceContext`/`observations`, never in the scope's `project` field.

---

## Step 4 — Dedup / Contradiction Detection

### Procedure

1. Call `GetCustomization(layer, scope)` (using the layer decided in Step 2b) — receive all entries with matching scope from that overlay.
2. Compare the new insight against the returned entries by **classification+scope+subject similarity** (do not construct a slug yourself for comparison — it may diverge from the tool's derivation rule):
   - **An existing entry addresses the same insight** → merge path: compare the existing body against the new insight.
     - Identical/complementary → propose an updated body, observation count+1. **Pass back that entry's exact id in the call**.
     - Contradictory → present both conflicting versions to the user in Step 5.
   - **No match** → new path: propose adding a new entry. **Leave id empty in the call** (the tool derives the canonical slug).
3. If a very similar subject exists under the same classification + scope, add a duplicate warning.

### Handling Contradictions

Show both versions side by side:
```
[Existing]  id=xxx  confidence=medium  observations=3
        body: ...

[New proposal]
        body: ...

→ Which version should be kept? (keep existing / replace with new / merge both)
```

Proceed to Step 5 after the user chooses.

---

## Step 5 — Confirmation Gate Before Saving

### Checklist (before calling RecordCustomization)

- [ ] Did you show the user the full proposed entry?
  - layer, id, classification, scope (4 fields), confidence, subject, body
- [ ] Did you get approval: "Record this? (yes / edit / cancel)"?
- [ ] Does the body contain no machine-measured figures (resident bytes, %)?
- [ ] Is scope set correctly for the input context?
- [ ] Has the merge/new path been decided after checking for dedup?

**Do not call `RecordCustomization` unless this checklist passes.**

After a successful save, tell the user the entry takes effect only after **Rescan (Project Settings ▸ AI ▸ Skills) + a new Assistant chat** (overlays are skill references, refreshed only then).

### Standard Proposal Format

```
Proposed entry:
  layer:          playbook
  id:             (new — tool-derived, expected s4.ios.texture2d.astc-mobile)
  classification: §4
  scope:          platform=iOS; project=*; type=Texture2D; captureOrigin=Player
  confidence:     low
  subject:        ASTC max-size 512 on iOS texture-heavy projects
  body:           When the iOS Texture2D group is high in the resident ranking, check ASTC + max size 512 or below first.
                  Repeated cases have been observed where textures 2048 or larger are loaded without mipmap stripping.

Record this entry? (yes / edit / cancel)
```

(The example generalizes across projects, so `layer=playbook`. For `accepted` and other project-specific entries, use `layer=project` — the scope's `project` stays `*` in both cases; the layer choice follows the insight's project-specificity, not the scope value.)

---

## Step 6 — Confidence Upgrade Criteria

| observations count | Recommended confidence |
|---|---|
| 1 | `low` |
| 2–3 | `medium` |
| 4+ | `high` |

### Upgrade Proposal Trigger

When merging into an existing entry pushes the observation count past a threshold:
```
This entry's observations increased from 3→4.
Upgrade confidence from medium → high? (yes / no)
```

After user confirmation, update the confidence field too and handle it in a single `RecordCustomization` call.

---

## Reconfirming the Additive Invariant

What a customization entry may include:
- Advisory text (Recommend body)
- Difficulty/priority annotations
- Threshold overrides (§1) — numbers are allowed, **but only as policy parameters, not raw measured values used in the analysis skill's baseline calculations**

What must never be included:
- Snapshot measured values such as resident bytes, committed bytes, object count
- Recording a figure read from a tool call's result verbatim

On violation: the Curator removes the figure from the body and notifies the user.
