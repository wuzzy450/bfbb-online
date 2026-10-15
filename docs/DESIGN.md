# BFBB Online: design

Online co-op for **SpongeBob SquarePants: Battle for Bikini Bottom DELUXE** (GameCube, played in
Dolphin). Modeled on Coopfall (WorldBox co-op): rooms → **parties**, a relay → a **server that
simulates every occupied map**, the same server code runs dedicated (`start_server.bat`) or inside
the launcher ("Host on this PC").

## Pieces

| Piece | What it is |
|---|---|
| `patch/` | C code compiled for the GameCube CPU (zig/clang, `-mcpu=750`) and linked at `0x803D8A80`. The launcher patches it into the player's own Deluxe `main.dol` at install time (no game files are shipped). |
| `shared/` | C#: Dolphin RAM access (`DolphinMemory`), the mailbox layout (`Mailbox.cs`), the wire protocol. |
| `client/` | `BFBB-Online.exe`, the launcher + bridge: installs the patch, starts Dolphin, attaches to its RAM, talks to the server. |
| `server/` | the server library + `BfbbOnlineServer.exe` (dedicated). Manages parties, players, and **map sims**. |

## In-game patch

Hooks (all verified against the original instruction before patching):

| Site | Original | Ours |
|---|---|---|
| `zGameSetup+0x58` | `bl zGameSetupPlayer` | `bbol_setup_player`: build the local player, then one extra player context per remote slot (`MAX_REMOTE` = 5) |
| `zGameLoop+0x480` | `bctrl` player update | `bbol_player_update`: update local player, publish it to the mailbox, handle warps, update puppets |
| `zSceneRenderPreFX+0x190` | `bctrl` player render | `bbol_player_render`: render local player + puppets |
| `zSceneReset+0x12C` | `bl zEntPlayerReset` | `bbol_player_reset`: also reset puppets |
| `zGameLoop`, `zMenuLoop` | `bl xPadUpdate` (5 sites) | `bbol_pad_update`: optional pad override (sims, tests) |
| `zMainLoop` | `lwz r0, sShowMenuOnBoot` | `bbol_boot_stub`: wait ≤0.5 s for the bridge; a sim boots straight into a scene |
| `OSInit` | arena start `0x803D8A50` | moved above the patch |

**Puppets** (remote players on the same map): a full player context each (the game's
`zGameSetupPlayer`, built with `xModelInstStaticAlloc` so the model pools don't run out). Per frame
a puppet is swapped into `globals` (player block, camera, zEntPlayer statics, pad 0), fed the remote
player's buttons/sticks and camera basis (so the game animates jumps, bubble bashes, etc. itself),
updated, and pulled toward the remote position (snap if > 6 units off). Puppets are
invulnerable and silent. Pickups, triggers and enemy AI only test the live context during
`zSceneUpdate`, so a puppet can never collect or trigger anything in another player's game; the
player update's own pickup check and conversation starts (villager talk goal, task/talk boxes, OOB
hints) are blocked while a puppet runs, so a teammate walking up to a villager never opens that
conversation in your game.

Everything a player update reads or writes per player is swapped with the puppet: the player
block, camera, pad, `zEntPlayer` statics in .sbss/.sdata/.bss (motion, collision results, model
tags, surface state), the common .bss block with `rendermat`/`gPlayerAbsMat`/`floor_*` (the matrix
the player is drawn with, ground detection), `zcam_playervel`, and the bungee / cruise-bubble /
out-of-bounds mode states. Missing any of these shows up as your own player being drawn at a
teammate's spot (only the shadow stays) or not being able to jump.
`RF_CHEAP` skips physics and just places the model (far away players, sims).

## World state = the save system

Everything the campaign needs (spatulas, shinies, socks, collected pickups, dead enemies,
pressed buttons, opened gates, triggered scripts, counters, cutscenes seen) is what the game
already persists through `xSerial`: one buffer (`g_xserdata.bitbuf`, ~50 KB) with a slot per
client tag: `PLYR` (inventory), `CNTR` (global counters), and one per visited scene (`HB01`, ...).
`zSceneSave` writes every persistent object (`baseFlags & 2`) of a scene into its slot;
`zSceneLoad` reads it back.

The server keeps each party's world as `tag → bytes` (like Cuberite region files):

- **Entering a scene**: `zGameSetup` calls our setup hook right before `zSceneLoad`; the patch
  waits briefly for the bridge to import the party's canonical `PLYR`, `CNTR` and scene slots,
  then the game loads them as if from a save.
- **In a scene**: per-object deltas. The patch saves each persistent object into a scratch
  serializer and fingerprints it; changed objects are sent; receivers apply them with the
  object's own Load function. Collectibles are monotonic (a scene reset never "un-collects").
- **Inventory/progress** counters are merged as a union/max across the party; live changes are
  sent as deltas (`inv`), including max health (golden underwear raises it for everyone).
- **Not saved but shared while playing**: Deluxe keys, breakables, enemy generators, and whether
  each enemy is alive. These live in the server's per-scene object store, so they also stay that
  way for players who come back or join later.
- **Personal**: underwear and sundaes (respawning buffs), story-cinematic triggers and the
  cinematics' played flags.

## Cutscenes and conversations

- Story cinematics (cutscene managers) play for everyone in the same scene; players elsewhere get a
  notice and are not interrupted. The trigger stays personal, so whoever missed it gets it later.
- Conversations that hold the player are offered to teammates in the scene: a prompt in a sign's
  text box ("A is talking. Watch along? Up: yes Down: no"); yes starts the same task-box
  conversation in your game.

## Map sims (server-side simulation)

For every (party, scene) with players in it the server runs a **headless Dolphin** with the
patch in `MODE_SIM`: Null video, no audio, 30 fps (`minVSyncCnt = 2`), boots straight into the
scene (title menu skipped) with the party's world imported, its own player parked and hidden,
every party member present as a cheap puppet. The sim:

- holds the scene's authoritative object state while players come and go,
- runs what ticks on its own (enemies, timers, platforms) and streams enemy transforms to clients,
- is started on the first player's scene load and stopped (state saved to disk) a little after
  the last one leaves, so idle parties cost nothing.

Cheap by design: one sim per *occupied* map only, render skipped, 30 fps, single-core Dolphin,
own user folder with every expensive option off. A cap (`maxSims`) protects small hosts; above it
maps fall back to client-held state (the first player in the scene acts as the scene's host, like
Coopfall's room host).

## Hosting

`BfbbOnline.Server` is a library. `BfbbOnlineServer.exe` (dedicated, `start_server.bat`) and the
launcher's **Host on this PC** run the same code. Default port **TCP 25599**.
