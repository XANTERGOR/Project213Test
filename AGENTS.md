# Unity project safety

These rules apply to maintenance, tests, diagnostics and optimization in this project.

## Deferred work — user decision, 2026-09-24

- Defer standalone-player vegetation performance / streaming / memory checks until
  the project has a character and vehicles and the user resumes this work.
- Defer vegetation quality presets (low / medium / high, draw distance, distant
  thinning and shadow distance) until the user explicitly requests them again.
- Do not implement or repeatedly propose these deferred items as immediate next
  steps. No scheduled follow-up was requested.
- The user confirmed that changing mask-size settings now updates vegetation size
  immediately in their scene. This is not a packaged-player or GTX 1660 benchmark.

## Isolated validation

- Never create a nested Unity project in this project's `Temp`, `Library`, `Assets`
  or other Unity-managed directories. Use a separately owned validation directory
  outside the working Unity project; request filesystem permission when required.
- Never use junctions, symlinks, hard links or other reparse points to share the
  working project's files or package cache with a validation project. Use independent
  copies or let the validation project's Package Manager resolve its own packages.
- Treat the working `Library/PackageCache`, the Unity installation and global UPM
  cache as read-only for tests. Do not patch package sources to make a test pass.
  Explicitly authorized package repair is a separate operation, not a test shortcut.
- Preserve locked package versions. Do not upgrade/remove packages or change
  `Packages/manifest.json` or `Packages/packages-lock.json` to fix test infrastructure.

## Cleanup and process ownership

- Do not automatically delete validation directories. Before any approved recursive
  cleanup or move, inspect the target, its parent chain and descendants for links /
  reparse points. Validate the resolved absolute paths and stop if ownership is unclear.
- Never recursively clean the working project, its `Temp`, `Library` or package cache
  as a generic troubleshooting step. Prefer targeted, recoverable repair after diagnosis.
- Do not close or kill the user's Unity editor for testing. Only terminate a batch
  process started for the current check, after verifying its PID, start time and identity.
- Save validation logs outside `Temp`. Do not launch a graphics-dependent check with
  `-nographics` and treat the resulting shader/import failures as normal scene failures.

## Verification and reporting

- Before recovery or checks that open the working project, record hashes of relevant
  scene, source and settings files; verify them afterward. Do not save user scenes.
- Separate C# build results, native Unity import results, graphics tests and live-scene
  performance results. Passing one does not prove the others. Report remaining failures.
- If a tool or test causes unexpected damage, stop that workflow, disclose the impact
  and restore only with the necessary authorization. Never hide it by deleting logs.

Incident reference: a validation project under `Temp` linked to the working package
cache; subsequent cleanup emptied 24 package directories. The cache was restored at
locked versions. Do not recreate that validation arrangement.
