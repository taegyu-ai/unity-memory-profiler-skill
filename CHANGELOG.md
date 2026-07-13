# Changelog

All notable changes to this package are documented in this file.

## [0.1.0] - 2026-07-13

Initial public preview release, distributed as a UPM package (git URL install).

- Analysis skill (`unity-memory-profiling-skill`): single-snapshot Wide Survey, 2-snapshot comparison
  (diff) mode, Unrooted allocation-callstack breakdown.
- Customization skill (`unity-memory-customization-skill`, experimental): records project-accepted
  costs and general playbook know-how via `RecordCustomization`.
- Overlay data (`playbook.md` / `project-customization.md`) now lives in the consuming project's
  `Assets/`, not inside this package, so it survives package updates/removal. A starter template is
  available via the package's "Default Overlays" sample.
