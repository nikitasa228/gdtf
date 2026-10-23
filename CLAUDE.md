# Candela — notes for Claude Code

Candela is a C#/.NET 8 + Vulkan stage-lighting visualiser and editor: GDTF fixtures, MVR scenes, photometric light
(cd, lux), volumetric beams in haze, smoke and fire simulation, DMX/power cabling, live control from consoles (Art-Net,
sACN, OSC, PSN, EntTec), a real-time renderer and a path tracer for final renders. Windows is the target platform.

**Talk to the user in Russian.** The user is a lighting designer; their original requirements are
[docs/REQUIREMENTS.ru.md](docs/REQUIREMENTS.ru.md) (verbatim, Russian) — they are the acceptance criteria.

Start with [docs/HANDOFF.md](docs/HANDOFF.md) (current state, open work, how work is run) and
[docs/AUDIT-PLAN.md](docs/AUDIT-PLAN.md) (prioritised work packages with acceptance criteria).

## Map
- `docs/ARCHITECTURE.md` — stack, module dependency rules, conventions. `docs/ROADMAP.md` — milestones M0–M7.
- One doc per module: `gdtf.md`, `photometry.md`, `dmx.md`, `mvr.md`, `assets.md`, `simulation.md`, `electrical.md`,
  `protocols.md`, `project-format.md`, `rendering.md`, `engine.md`, `editor.md`, `install.md`.
- `src/Candela.*` modules, `tests/Candela.*.Tests` (xUnit). `src/Candela.App` = editor (`candela-app`),
  `src/Candela.Cli` = CLI (`candela`: render, render-test, artnet-test, info, validate, import-worker).
- The repository root holds the 1091-file GDTF fixture library (`*.gdtf`); `_specs/` researched fixture data
  (`_specs/SCHEMA.md`, `fixtures_index.json`); `stages/Фокина.mvr` is the reference test scene (`.skp` files cannot be
  read — closed format).
- `packaging/windows/` — installer (NSIS) and build scripts; version in `Directory.Build.props`
  (`CandelaVersion` / `CandelaPreview`).

## Conventions (see docs/ARCHITECTURE.md)
- SI units, right-handed Z-up metres (MVR millimetres converted), linear Rec.2020 working colour space; light colour =
  chromaticity normalised to luminance 1 plus a photometric magnitude in candela.
- Nullable warnings are errors; XML docs on public members (units and intent); `dotnet format --verify-no-changes`
  must pass; no TODO stubs; robust against malformed input (report an issue, never crash a whole load).
- Match the surrounding code's style and comment density.

## Build and test
```
dotnet build Candela.sln
dotnet test tests/Candela.<Module>.Tests        # one project at a time; set CANDELA_CACHE_DIR to a temp folder
dotnet format <project>.csproj --verify-no-changes
dotnet run --project src/Candela.Cli -- render stages/Фокина.mvr --out f.png --look demo
dotnet run --project src/Candela.App -- --open stages/Фокина.mvr
```
- GPU tests run on any Vulkan 1.3 device; in the cloud they used lavapipe (software) and Xvfb. On a real GPU they are
  much faster — and some artefacts only show there (lavapipe clamps half-float overflow, AMD does not).
- Editor headless screenshots/scripts: `candela-app --screenshot out.png --frames N --open <scene> --script <json>`
  (docs/editor.md) — use them to look at UI changes.
- Windows installer: `packaging/windows/build-installer.ps1` (Windows, needs NSIS) or `build-installer.sh` (Linux).

## Git
- Work branch: `claude/ecstatic-darwin-bal444`. Commit messages end with the session's attribution lines.
- Never commit build outputs or installers (`artifacts/` is ignored).
