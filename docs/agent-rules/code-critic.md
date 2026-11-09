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
  Exemption (ADR 0006): synced delivery writes Milieu's own files (`sky_*.atex`,
  `dome_*.avfx`, `*.tmp`) into `<Penumbra ModDirectory>\_milieu_sync\` and nowhere else under the mod directory; cleanup
  touches only those patterns, and the folder is never created while `Plugin.SyncedAvailable` is off. Any other write there is still `FAIL`.
- `TreatWarningsAsErrors` stays on in `Milieu.csproj`. `FAIL` if a diff turns it off or adds a blanket `NoWarn`.
- Workflows (`.github/workflows/`, ADR 0007): every action is pinned to a full commit SHA with a `# vX.Y.Z` comment;
  only `release.yml` and `testing-release.yml` may hold `contents: write`, `secrets.` or a release action; no guard is
  ever disabled (`continue-on-error`, step-level `if:`, `catch`, `trap`, `|| true`, `-ErrorAction`,
  `$ErrorActionPreference`, `-SkipHttpErrorCheck`); every native command in a `run:` block is its last line or is
  followed by an exit-code check; the package guards stay in `tools/check-package.ps1`. Backed by `WorkflowTests` and
  `PackageCheckScriptTests`. `FAIL` if a diff weakens any of this.

## Privacy anchors

- **Sensitive categories**: character names and full local paths of user images; neither goes to chat (beyond the file name) or to logs at Information level or above.
- **Public surfaces**: none inbound. Outbound only: HTTPS GETs for the bundled backdrops (`Milieu/Assets/`), to the
  `dl.solona.info/milieu/` mirror first and GitHub second, which send no user data (Cloudflare sees the IP and the
  `Milieu-Plugin` user agent). The repo itself has two live surfaces: the dl.solona.info worker serves `README.md` and
  `Milieu/images/icon.png` from the `test` branch (`https://dl.solona.info/milieu`, `/milieu/icon.png`), so a `test`
  push publishes them within minutes (ADR 0007).
- **User images leaving the machine**: only through synced delivery (ADR 0006): PSync uploads the
  shown image to its server and sends it to the user's pairs. Milieu must show the one-time notice before the first
  user image is shared (the image goes only on the next click); a diff that can upload a user image without that gate
  having fired is `FAIL`. Milieu itself never sends the image anywhere.
- **Identifier exemptions**: none.
- **Privacy tests**: none yet.

## Checklist

- [ ] New/changed game-file writers have a parse-back test (if applicable)
- [ ] Every create path has a matching teardown in Dispose/clear
- [ ] Game memory and native calls only on the framework thread
- [ ] Image processing off the framework/draw thread
- [ ] New native signatures centralised with a source comment (if applicable)
- [ ] No writes outside the plugin config directory (except `_milieu_sync`, ADR 0006)
- [ ] No user image reaches PSync before the first-upload notice
- [ ] Warnings-as-errors untouched
- [ ] Workflows: SHA-pinned, write/secrets only in the release workflows, no disabled guard, native exit codes checked
- [ ] A change to `README.md` or `Milieu/images/icon.png` is fit to go live on the next `test` push
