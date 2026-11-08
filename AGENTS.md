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
- default branch: `test` — reviews diff against it; work merges into it (`main` is the stable release branch)

## Architecture

```
Milieu/          the Dalamud plugin (Plugin.cs entry point, /milieu command)
  Gui/           ImGui windows
Milieu.Tests/    xunit tests
docs/            product context, ADRs, agent review/planning guidance
```

## Testing

xunit in `Milieu.Tests/`, one `*Tests.cs` per unit, synthetic fixtures built in
code. Tests needing real game or user files must skip when the file is absent;
never hard-code a local path. While iterating run only targeted `--filter`
tests. A test that takes 5 s or more gets `[Trait("Speed", "Slow")]`.

## Sensitive Areas — the security surface

- Binary game-file writers (`.avfx`, `.atex`/`.tex` output) — a malformed VFX or texture can crash the game
- Native interop: function signatures, hooks, unsafe struct access, VFX spawn/remove — a wrong signature or a dangling VFX pointer crashes the game
- Penumbra IPC (temporary mods/redirects) — must be removed on dispose and never touch the user's own mods
- `Milieu/Configuration.cs` migrations — a mistake loses user settings
- `.claude/settings.json` — loaded by every agent session in the repo

## Review & Planning Guidance

- Code review guidance: `docs/agent-rules/code-critic.md`
- Planning guidance: `docs/agent-rules/plan-critic.md`
