# BFBB Online protocol v2

TCP, default port **25599**, UTF-8 JSON, one object per line (`\n`), every object has `"t"`.
Scene ids are 4-character strings (`"JF01"`). Serializer slots and object states are base64.

## Client → server

| t | fields | meaning |
|---|---|---|
| `hello` | `name, color "#rrggbb", version:2` | first message; names are made unique |
| `join` | `party` | join (or create) a party; leaves the previous one |
| `leave` | | leave the party |
| `st` | `s, p[x,y,z], y (yaw), v[vx,vy,vz], b (pad buttons), a[ax,ay,cx,cy], c[9] (camera right/up/at), ch (0 SpongeBob, 1 Patrick, 2 Sandy), hp, co?` | my player, ~20 Hz; `co` = my controls are locked (dialog/cutscene) |
| `load` | `seq, s, ch, up{tag: slot}` | my game is about to load scene `s` and waits: `up` = serializer slots that changed since the server last sent me the world |
| `obj` | `s, id, i, ty, n, d, k?, p?` | an object in my scene changed: asset id, index in the scene's base list, base type, bit count, its save-format bits; `k` pickup kind, `p` = a race's reward (count it, don't share it) |
| `np` | `s, d` | poses of the enemies/bosses my game drives, ~20 Hz, only those that changed (all once a second): `d` = base64 of 64-byte `NpcPose` records (patch/src/mailbox.h) |
| `objsync` | `s` | I entered scene `s`: send me every live object state you have for it |
| `cs` | `s, id` | a story cutscene (cutscene manager `id`) started in my game |
| `talk` | `s, id` | a conversation that holds the player (talk box `id`) started in my game |
| `inv` | `d[[i, delta]...]` | my live counters changed: index into `PlyrSlot.LiveCounters` (0 shinies, 2 socks, 3.. per-level socks/level pickups, 33 max health; 1 spatulas is ignored: the server counts those). Sent live and before every `load`; uploads don't carry counters |
| `chat` | `text` | party chat (lobby chat when not in a party) |
| `ping` | `ts` | keep-alive |
| `bye` | | disconnect |

## Server → client

| t | fields | meaning |
|---|---|---|
| `welcome` | `id, name, version, parties[{name, players}]` | |
| `joined` | `party, fresh` | `fresh`: the party has no progress yet; my first upload seeds it |
| `members` | `party, members[{id, name, color, s}], sims[{s, state}]` | party roster and running map sims |
| `st` | (as above) `+ id` | a teammate in my scene |
| `world` | `seq, slots{tag: slot}` | answer to `load`: the party's world for me (my own character and story-cutscene flags kept) |
| `obj` | (as above) `+ from?` | a teammate (or the map sim) changed an object in my scene |
| `np` | (as above) `+ from` | poses of the enemies/bosses a teammate's game drives in my scene (relayed, not stored) |
| `objs` | `s, list[obj...]` | answer to `objsync` (obj: `s, id, i, ty, n` bits, `d` base64 save bits, `k` pickup kind = asset type<<8 / index) |
| `cs` | `s, id, from, name` | a teammate in my scene started a story cutscene: play it too |
| `talk` | `s, id, from, name` | a teammate in my scene is in a conversation: my game shows the watch-along prompt (D-pad Up starts the same conversation here, Down or 8 s declines) |
| `inv` | `d, from, name` | a teammate's counter changes: add them |
| `spat` | `n` | the party's spatula count (absolute): the server counts each spatula pickup once, from object states with `k` = 0x0100 that are collected |
| `chat` | `id, name, color, text` | |
| `notice` | `text` | e.g. "Alice is watching a cutscene in JF01" |
| `error` | `msg` | |
| `pong` | `ts` | |

## World merging

The party world is the game's save serializer: `PLYR` (inventory, plus per-player story-cutscene
flags), `CNTR` (global counters), one slot per visited scene. An upload is merged against what that
player was last given (their baseline):

- `PLYR` counters (shinies, spatulas, socks, level pickups) are NOT merged from uploads: they move
  only through live `inv` changes and the server's spatula count (`spat`);
  max health and power-ups take the maximum; the character and everything after the counters
  (story cutscenes seen) stay personal.
- every other bit takes the uploader's value only where the uploader changed it.

## Live object states (`obj`)

`d` is the object's own save-format bits, applied with the object's Load function, except:

- **enemies** (base type 0x2B): one bit, alive. A dead enemy is killed quietly in the receiver's game
  (no second loot drop); an alive state never revives anything. Each game spawns and runs its own AI.
- **keys** (Deluxe, pickup item 13): after loading the collected state, the receiver fires the key's
  own pickup event once, so its key counter counts down and the chest opens in every game.
- **task boxes** (0x35): applied with their OnComplete links muted (what those do arrives as other
  objects' states; locally they would start the follow-up dialog), and only while the receiver has
  no conversation open.
- triggers that start a cinematic are never synced: each player's own trigger plays it for them.
- **races / timed challenges**: a trigger that gives a pickup with Pickup(35) (a finish line giving
  the reward) and everything it sets off (4 links deep, not portals or cinematics) is per player:
  a teammate winning doesn't end the race for anyone else. The reward pickup is still reported
  (`p: true`) so the server counts its spatula once; it is never stored, relayed or applied.

## Enemy and boss movement (`np`)

Every game runs its own AI; an NPC's AI only sees its own game's player. Each NPC is driven by one
game, which streams its pose (position, at/up rows), first animation layer (state pointer + time;
scene memory has the same layout in every game, the receiver checks the state is in its own table),
AI goal and, for bosses, hit points/round and an ownership epoch. The others set their copy to it
every frame (enemies lerp, > 12 units snap).

- enemies (robots, tikis, duplotrons, jellyfish): driven by the game whose player is nearest
  (2-unit hysteresis, ties to the lower party id): they chase whoever is actually closest.
- bosses and sub-bosses (Robo-Sandy, Robo-Patrick, King Jellyfish, Flying Dutchman, Prawn, the
  Robo-SpongeBob parts): driven by the lowest party id in the arena. When a follower's hit points
  drop (its player hit the boss), it takes the boss over with epoch + 1 and everyone follows its
  state, so the hit counts once for everyone. A newer epoch always wins; equal epochs go to the
  lower party id. If the owner's poses stop for 1.5 s, a follower takes over.

Puppets (teammates' players in my game) never collect pickups, start conversations or enter
trigger volumes: only the player's own game does that.

A player's first upload after joining a party that already has progress is not merged (it's their
own unrelated save); they receive the party's world instead.
