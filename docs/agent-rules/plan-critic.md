# Project lenses — plan-critic

When applying the four methods, give explicit attention to:

- **Game-crashing binaries.** `.avfx` and `.atex` are written by hand; a wrong chunk size, padding, vertex stride or mip offset crashes the client with no managed exception. Plans need parse-back tests and a minimal, known-good structure, cross-checked against VFXEditor's reader (`E:\repos\Dalamud-VFXEditor`).
- **Native lifetime.** A spawned VFX is a game object the plugin owns. Zone changes, GPose enter/exit, plugin unload and applying a new image all need explicit removal, or the pointer dangles. Watch for VFX spawned before the player is loaded.
- **How the file reaches the game.** A custom game path only loads if something redirects it (Penumbra temporary mod, or a resource hook). The plan must say which, what happens when Penumbra is missing, and how the redirect is removed.
- **Texture limits and memory.** HDRIs are large (8K+, float). Decoding, tone mapping, resizing to a 2:1 power-of-two size the game accepts (the reference uses 8192x4096 BC7) and compressing must run off-thread; peak memory and time matter.
- **Projection correctness.** Equirectangular mapping on a sphere seen from inside: seam placement, U direction (mirrored?), pole pinching, the reference's U-scale-2 convention, and for the small room dome the camera must sit near the centre or the image distorts.
- **Only verifiable in game.** Visual results and crash-freedom can't be proven by unit tests; plans should separate what tests prove from what needs an in-game check.
