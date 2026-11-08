# Milieu

Guidance for AI coding agents working in this repository. This file
(`AGENTS.md`) is the canonical copy; `CLAUDE.md` imports it via `@AGENTS.md` so
Claude Code (and its sub-agents) load it. Keep it lean: deep detail belongs in
`docs/` (including `docs/adr/`) and the code. This file is the map.

## Rules — non-negotiable

These apply to **every** agent, the main agent and any sub-agents it invokes.

1. **Use the project's canonical commands.** Build, run and test only through
   the documented commands (see *Commands*). If a needed capability has no
   command, STOP and report the gap (Rule 2) rather than substituting raw shell.

2. **If a command or tool fails — or succeeds without doing what it should —
   STOP and report it; never silently work around it.** Give the exact step and
   its full output. Do not retry with different flags or skip the step.

3. **Feature work runs on its own branch off `test`.** Standing permission:
   create a feature branch from `test` at the start of a feature, before
   planning. Commit only there; never commit feature work directly onto `test`
   or `main`. At the end, after the review (Rule 4), merge the branch back into
   `test` locally.

4. **Get reviewed before merging; never push unless asked.** Work is merged into
   `test` only after a passing `code-critic` review of the current commit. Any
   commit made after the review needs a re-review. There is no pre-push hook.

5. **Every build meant for the game is loadable there.** Bump
   `Plugin.BuildNumber`, build Release, and make sure the output is at
   `E:\repos\Milieu\Milieu\bin\Release\win-x64` (the primary checkout's build
   output, the path the game dev-loads). From a worktree, copy the build there
   and check the copied `Milieu.dll` timestamp.

## Project Overview

Milieu is a Dalamud plugin for FFXIV (C# 14, .NET 10, x64, ImGui via Dalamud
bindings, Dalamud.NET.Sdk 15) that replaces the backdrop with an HDRI or
panorama image. The image is projected onto a sphere drawn as a game VFX
(`.avfx` model particle + `.atex` texture), either as a large outdoor sky dome
or a small dome for room interiors. The technique follows the "Pickles Venue"
mod: a unit-sphere model particle whose UV set scales U by 2, textured with an
8192x4096 BC7 equirectangular image.

**Before building features, read the product docs** in `docs/product-context/`.
Architecture decision records are in `docs/adr/`.

## Commands

- `dotnet build Milieu/Milieu.csproj -c Release` — build
- `dotnet test Milieu.Tests/Milieu.Tests.csproj -c Release` — run all tests (the full suite: before a release tag)
- `dotnet test Milieu.Tests/Milieu.Tests.csproj -c Release --filter "Speed!=Slow"` — the quick suite: before every push
- all checks: same as run all tests (the build treats warnings as errors)
- run: bump `Plugin.BuildNumber`, build, output at `E:\repos\Milieu\Milieu\bin\Release\win-x64` — the game dev-loads it (no app URL; verification is in game)
- stop: none — unload the plugin in game via `/xlplugins`
- single test: `dotnet test Milieu.Tests/Milieu.Tests.csproj -c Release --filter "FullyQualifiedName~PluginManifestTests"`
- reference-parity tests (run before code review): set `MILIEU_REFERENCE_AVFX` / `MILIEU_REFERENCE_ATEX` to an extracted "Pickles Venue" `ve0002.avfx` / `Sky1.atex` and `MILIEU_HDRI_SOURCE_DIR` to the source images, then filter `FullyQualifiedName~Reference|FullyQualifiedName~WhenSourcePresent|FullyQualifiedName~BundledBackdropsTests|FullyQualifiedName~CancelInsideMip0`; report pass/skip counts
- prepare bundled backdrops (rarely): `dotnet run --project tools/PrepareBackdrops -c Release -- <source dir> Milieu/DefaultBackdrops`, then `-- atex Milieu/DefaultBackdrops artifacts/backdrops`, paste the printed pins into `Milieu/Assets/BackdropPins.cs` and bump its `Tag`, `-- verify artifacts/backdrops --tag <tag>`, `-- thumbs Milieu/DefaultBackdrops Milieu/Resources/Thumbs`; publish (the owner): tag that commit, push the tag, `powershell -File tools/publish-backdrops.ps1 -Tag <tag>` (ADR 0005)
- default branch: `test` — reviews diff against it; work merges into it (`main` is the stable release branch)

## Architecture

```
Milieu/            the Dalamud plugin (Plugin.cs: services, events, /milieu, dispose order)
  Backdrop/        lifecycle controller, background converter, cache, config types, messages
  GameFiles/       AVFX chunk I/O, dome sphere mesh, dome .avfx builder, BC7 .atex writer
  Imaging/         header probes, decode budget, tone mapping, resampling, band fill, seam check, mips, BC7
  Interop/         native signatures, static VFX spawner + remove hook, Penumbra IPC, game state
  Assets/          bundled-backdrop downloads (pinned release assets, resilient downloader)
  Gui/             main window and its pure view models
  DefaultBackdrops/  committed source JPEGs of the bundled scenes (NOT shipped; pre-converted .atex are built into artifacts/backdrops and published with tools/publish-backdrops.ps1)
  Resources/Thumbs/  embedded 256x128 previews of the bundled scenes
Milieu.Tests/      xunit tests (Support/: fakes, synthetic images, reference-file facts)
tools/PrepareBackdrops/  offline tool that produces Milieu/DefaultBackdrops
docs/              ADRs (0002 Penumbra delivery, 0003 dome AVFX, 0004 bundled backdrops), agent guidance
```

## Testing

xunit in `Milieu.Tests/`, one `*Tests.cs` per unit, synthetic fixtures built in
code. Tests needing real game or user files must skip when the file is absent;
never hard-code a local path. Exception: product-asset tests (`BundledBackdropsTests`, the pinned-folder test) read the committed `Milieu/DefaultBackdrops` images (ADR 0004). While iterating run only targeted `--filter`
tests. A test that takes 5 s or more gets `[Trait("Speed", "Slow")]`.

## Sensitive Areas — the security surface

- Binary game-file writers (`.avfx`, `.atex`/`.tex` output) — a malformed VFX or texture can crash the game
- Native interop: function signatures, hooks, unsafe struct access, VFX spawn/remove — a wrong signature or a dangling VFX pointer crashes the game
- Penumbra IPC (temporary mods/redirects) — must be removed on dispose and never touch the user's own mods
- `Milieu/Configuration.cs` migrations — a mistake loses user settings
- Downloads (`Milieu/Assets/`) and `tools/publish-backdrops.ps1` — outbound GETs that write into the config directory, downloaded textures handed to the game; pinned size + header + SHA-256 before use; the publish script's guards keep pins and release assets identical
- `.claude/settings.json` — loaded by every agent session in the repo

## Review & Planning Guidance

- Code review guidance: `docs/agent-rules/code-critic.md`
- Planning guidance: `docs/agent-rules/plan-critic.md`
