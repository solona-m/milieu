# Project rules — code-critic

Project-specific extension of the AI Workflow plugin's `code-critic` skill.
Each rule states its severity. Prefer encoding mechanically checkable rules as
tests; for a rule backed by a test, a diff that weakens the test is `FAIL`.

## Rules

- Every game-file writer (`.avfx`, `.atex`/`.tex`) has a round-trip or structure test that parses its output back and checks sizes, offsets and counts. `FAIL` if a writer lands without one: a malformed file crashes the game, not the plugin.
- Everything the plugin creates in game (spawned VFX, Penumbra temporary mods/redirects, hooks, command handlers, windows) is torn down in `Dispose` and when the backdrop is cleared. `FAIL` if a create path has no matching removal.
- Game-memory access and native calls happen on the framework thread (`IFramework.RunOnFrameworkThread` / `Update`), never from a background task. `FAIL`.
- Heavy work (HDRI decode, tone mapping, resize, BC compression) runs off the framework/draw thread. `FAIL` if it can block a frame.
- Native signatures and offsets live in one interop file with a comment naming their source (FFXIVClientStructs, VFXEditor, …). `NEEDS_DECISION` if a new signature has no source.
- User images are read only from paths the user chose; nothing is written outside the plugin's config directory. `FAIL`.
- `TreatWarningsAsErrors` stays on in `Milieu.csproj`. `FAIL` if a diff turns it off or adds a blanket `NoWarn`.

## Privacy anchors

- **Sensitive categories**: character names and full local paths of user images; neither goes to chat (beyond the file name) or to logs at Information level or above.
- **Public surfaces**: none inbound. Outbound only: HTTPS GETs for the bundled backdrops (`Milieu/Assets/`), which send no user data.
- **Identifier exemptions**: none.
- **Privacy tests**: none yet.

## Checklist

- [ ] New/changed game-file writers have a parse-back test (if applicable)
- [ ] Every create path has a matching teardown in Dispose/clear
- [ ] Game memory and native calls only on the framework thread
- [ ] Image processing off the framework/draw thread
- [ ] New native signatures centralised with a source comment (if applicable)
- [ ] No writes outside the plugin config directory
- [ ] Warnings-as-errors untouched
