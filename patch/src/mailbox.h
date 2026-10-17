// Shared memory block between the in-game patch and the BFBB Online launcher/server
// (which reads and writes it through Dolphin's emulated RAM). Big-endian, fixed address.
// Keep in sync with shared/Mailbox.cs.
#ifndef BBOL_MAILBOX_H
#define BBOL_MAILBOX_H
#include "game.h"

#define BBOL_MAGIC      0x42424F4Cu  // 'BBOL'
#define BBOL_BRIDGE     0x42524447u  // 'BRDG' written by the bridge once attached
#define BBOL_VERSION    1u
#define MAX_REMOTE      5            // puppets per map (6 players on one map)
#define EXTRA_RAM 0x81800000u     // MEM1 above 24 MB: only with Dolphin's RAM override + our DBAT3
#define EXTRA_OSHEAP_LO 0x81A00000u // with the RAM override: 0x81800000.. puppets, this.. the OS heap
#define EXTRA_OSHEAP_HI 0x82000000u  // the OS maps 32 MB (BAT), more MEM1 isn't reachable
#define HEAP_RESERVE_DEFAULT 0x6000u  // free heap kept after building puppets (24 KB)
#define PUPPET_COST_GUESS 0x10000u    // scene heap one puppet needs, before one was measured (64 KB)

#define MODE_OFFLINE    0u
#define MODE_CLIENT     1u
#define MODE_SIM        2u           // headless server-side map simulation

#define MBF_WARP_FORCE  1u           // mb.flags: a warp to the current scene reloads it
#define MBF_WARP_ANYTIME 2u
#define MBF_NO_PUPPET_RENDER 16u      // mb.flags: draw no puppets (diagnostics)
#define MBF_ALL_CHEAP   8u           // mb.flags: every puppet RF_CHEAP (diagnostics)
#define MBF_PVP         0x800000u    // mb.flags: teammates' attacks hurt us (host setting)
#define MBF_NO_NAMETAGS 0x20000u     // mb.flags: no names over teammates
#define MBF_NO_PUPPETS  4u           // mb.flags: don't build puppet contexts (diagnostics)          // mb.flags: also warp while gameplay is paused (cutscenes); tests only

#define RF_CHEAP        1u           // no physics: place the puppet, play no input
#define RF_HIDDEN       2u
#define RF_NOINPUT      4u           // owner's controls are locked (dialog, cutscene): feed no input

typedef struct {
    u32 active;          // 0x00 1 = slot in use
    u32 seq;             // 0x04 bumped by the bridge for every new sample
    u32 character;       // 0x08 0 SpongeBob, 1 Patrick, 2 Sandy
    u32 on;              // 0x0C pad buttons held (xPad "on" bits)
    s8  ax, ay, cx, cy;  // 0x10 raw analog sticks
    f32 pos[3];          // 0x14
    f32 yaw;             // 0x20
    f32 vel[3];          // 0x24 units/s
    f32 cam[9];          // 0x30 camera right, up, at
    u32 health;          // 0x54
    u32 flags;           // 0x58 RF_*
    u32 color;           // 0x5C 0xRRGGBBAA
    char name[16];       // 0x60
} Remote;                // 0x70

typedef struct {
    u32 seq;             // 0x00 odd while the game is writing
    u32 frame;           // 0x04
    u32 sceneId;         // 0x08 e.g. 'HB01'
    u32 inGame;          // 0x0C 1 while zGameLoop runs a scene with a live player
    u32 character;       // 0x10
    u32 on;              // 0x14
    s8  ax, ay, cx, cy;  // 0x18
    f32 pos[3];          // 0x1C
    f32 yaw;             // 0x28
    f32 vel[3];          // 0x2C
    f32 cam[9];          // 0x38
    u32 health;          // 0x5C
    u32 shiny;           // 0x60
    u32 spatula;         // 0x64
    u32 seqEnd;          // 0x68 equals seq once the block is consistent
    u32 controlOff;      // 0x6C nonzero while the game ignores the player's pad (dialogs, cutscenes)
} Local;                 // 0x70

#define INV_COUNTERS    34           // shiny, spatula, sock total, 15 x (socks, level pickups), max health
#define OBJ_RING        32
#define OBJ_DATA        240          // bytes of save data per record (1920 bits)

#define NPC_MAX 128
#define PAY_RING 16
typedef struct {
    u16 index;           // 0x00 index in zScene::base
    u16 flags;           // 0x02 1 valid, 2 boss, 4 hp/round valid; high byte: sender's party id
    u32 id;              // 0x04 asset id (checked before applying)
    f32 pos[3];          // 0x08
    f32 at[3];           // 0x14 rotation: model matrix "at" and "up" rows
    f32 up[3];           // 0x20
    u32 animState;       // 0x2C xAnimState* of the first animation layer (same address in every game)
    f32 animTime;        // 0x30
    s32 goal;            // 0x34 AI goal id (xPsyche::GIDOfActive)
    s16 hp, round;       // 0x38 bosses
    u32 epoch;           // 0x3C bosses: ownership generation
} NpcPose;               // 0x40               // 0x20

typedef struct {
    u32 scene;           // 0x00
    u32 id;              // 0x04 the object's asset id (checked before applying)
    u16 index;           // 0x08 index in zScene::base
    u16 bits;            // 0x0A save-format bits in data
    u8  type;            // 0x0C base type
    u8  pad_[3];
    u8  data[OBJ_DATA];  // 0x10
} ObjRec;                // 0x100

typedef struct {
    u32 magic;           // 0x00 BBOL_MAGIC (static)
    u32 version;         // 0x04
    u32 bridge;          // 0x08 BBOL_BRIDGE once a bridge is attached
    u32 mode;            // 0x0C MODE_*
    u32 bootScene;       // 0x10 sim: scene id to boot straight into (skips the title menu)
    u32 warpScene;       // 0x14 requested scene id
    u32 warpSeq;         // 0x18 bridge bumps to request a warp
    u32 warpAck;         // 0x1C patch copies warpSeq once handled
    u32 padOverride;     // 0x20 1 = replace the local pad with padOn/pax..
    u32 padOn;           // 0x24
    s8  pax, pay, pcx, pcy; // 0x28
    u32 maxRemote;       // 0x2C MAX_REMOTE
    u32 ctxReady;        // 0x30 puppet contexts built for the current scene
    u32 frames;          // 0x34 game frames since boot
    u32 bootWaited;      // 0x38 frames the boot hook waited for the bridge
    u32 flags;           // 0x3C MBF_*
    // scene-load handshake: the patch announces the scene it is about to load and waits (at most
    // loadWait frames) for the bridge to import the party's world into the serializer.
    u32 loadScene;       // 0x40
    u32 loadSeq;         // 0x44 bumped by the patch
    u32 loadAck;         // 0x48 set to loadSeq by the bridge when the import is done
    u32 loadWait;        // 0x4C frames to wait (0 = no handshake)
    u32 ensureCount;     // 0x50 serializer slots the bridge wants to exist (ensureTags)
    u32 ensureSeq;       // 0x54 bridge bumps
    u32 ensureAck;       // 0x58 patch copies ensureSeq once the slots exist
    u32 loadWaited;      // 0x5C frames the last handshake took
    Local local;         // 0x60
    Remote remote[MAX_REMOTE]; // 0xD0
    u32 ensureTags[32];  // 0x300
    // live object sync: rings of object states (the object's own save-format bits)
    u32 outHead;         // 0x380 patch writes (next record to fill)
    u32 outTail;         // 0x384 bridge writes (next record to read)
    u32 inHead;          // 0x388 bridge writes
    u32 inTail;          // 0x38C patch writes
    u32 objScene;        // 0x390 scene the object table belongs to
    u32 objPrimed;       // 0x394 1 once every object's fingerprint is known (changes are reported from then on)
    u32 resetSeq;        // 0x398 bumped when the scene was reset (player died): ask for the party's states again
    u32 objDropped;      // 0x39C states not reported because the out ring was full or they were too big
    u32 objApplied;      // 0x3A0 states applied from the in ring
    u32 objCount;        // 0x3A4 persistent objects in the scene
    // story cutscenes (zCutsceneMgr cinematics) are shared with teammates in the same scene
    u32 csSeq;           // 0x3A8 patch bumps when a cinematic starts here on its own
    u32 csId;            // 0x3AC that cutscene manager's id
    u32 csScene;         // 0x3B0
    u32 csPlayId;        // 0x3B4 bridge: a teammate started this cutscene manager...
    u32 csPlaySeq;       // 0x3B8 ...bumped per request
    u32 csPlayAck;       // 0x3BC patch copies csPlaySeq once handled
    ObjRec out[OBJ_RING];// 0x3C0
    ObjRec in[OBJ_RING];
    // test/debug (0x23C0)
    u32 dbgHashes;       // address of the per-object fingerprint table (u32[4096], by base index)
    u32 dbgCtx;          // address of the puppet contexts (player block first)
    u32 dbgCtxSize;      // bytes per context
    u32 tpSeq;           // bridge bumps: put the local player at tpPos
    u32 tpAck;
    f32 tpPos[3];
    // inventory: the patch publishes every counter between frames (never mid puppet swap) and
    // adds the party's changes itself; order = shared/SerialBits.cs PlyrSlot.LiveCounters
    u32 invCount[INV_COUNTERS];  // 0x43E0
    s32 invAdd[INV_COUNTERS];    // 0x4464
    u32 invSeq;                  // 0x44F0 bridge bumps after filling invAdd
    u32 invAck;                  // 0x44F4
    // test hook: send an event to a scene object (as its trigger would)
    u32 evtId;                   // 0x44F8 test hook: event to object id (0 = game state event, only 159 resume)
    u32 evtCode;                 // 0x44FC
    u32 evtSeq;                  // 0x4500
    u32 evtAck;                  // 0x4504
    // conversations (talk boxes): reported to teammates in the scene, who get a watch-along prompt
    u32 talkSeq;                 // 0x4508 patch bumps when a conversation starts here on its own
    u32 talkId;                  // 0x450C that talk box's id
    u32 talkScene;               // 0x4510
    u32 offerSeq;                // 0x4514 bridge bumps: show the prompt for offerId
    u32 offerAck;                // 0x4518
    u32 offerId;                 // 0x451C the teammate's talk box
    u32 offerResult;             // 0x4520 1 accepted, 2 declined/timed out, 3 busy (not shown)
    char offerText[64];          // 0x4524 the prompt
    u32 dbgRender[40];           // 0x4564 diagnostics: state right after our player was drawn
    u32 heapReserve;             // 0x4604 bridge: bytes to keep free when building puppets (0 = default)
    u32 heapFree[MAX_REMOTE + 1];// 0x4608 free heap gap before the puppets / after each one (high RAM: bytes used there; [0] meaningless)
    u32 heapGap;                 // 0x4620 free bytes between the scene heap and the top heap after the scene loaded
    u32 highRam;                 // 0x4624
    u32 osHeapAdded;             // 0x4628 bytes given to the OS heap from RAM above 24 MB at boot
    u32 highRamMapped;           // 0x462C 1 = DBAT3 maps 0x81800000..0x82000000 (needs 32 MB MEM1)
    u32 objNoisy;                // 0x4630 objects that turned out to tick on their own (no longer shared)
    u32 invSetMask[2];           // 0x4634 bridge: these invAdd entries are absolute values, not deltas
    // enemy movement: one game per scene (the owner) streams every NPC's pose, the others follow it
    u32 npcRank;                 // 0x463C bridge: our party id (0 = enemy sync off); breaks distance ties
    u32 npcOutSeq;               // 0x4640 patch bumps after filling npcOut
    u32 npcOutCount;             // 0x4644
    u32 npcOutScene;             // 0x4648
    u32 npcInSeq;                // 0x464C bridge bumps after filling npcIn
    u32 npcInCount;              // 0x4650
    u32 npcInScene;              // 0x4654
    u32 npcApplied;              // 0x4658 diagnostics: poses applied (total)
    u32 npcRemoteRank[MAX_REMOTE]; // 0x465C bridge: party id of the player in each puppet slot
    NpcPose npcOut[NPC_MAX];     // 0x4670 the NPCs our game drives (nearest to our player)
    NpcPose npcIn[NPC_MAX];
    // paid gates (shiny doors...): what a payment set off (the paying object's links) is replayed
    // in teammates' games, without paying again
    u32 payOutSeq;               // 0x8670 patch bumps after a payment here
    u32 payOutId;                // 0x8674 the object whose links charged the shinies
    u32 payOutEvent;             // 0x8678 the event it was handling
    u32 payOutScene;             // 0x867C
    u32 payInHead;               // 0x8680 bridge bumps after filling payIn[head % PAY_RING]
    u32 payInTail;               // 0x8684 patch
    u32 payIn[PAY_RING][2];      // 0x8688 (object id, event) to replay in the current scene
} Mailbox;

extern volatile Mailbox mb;
void objsync_reset(void);
void objsync_scene_reset(void);
void objsync_frame(void);
void cutscene_frame(void);
void talk_frame(void);
u32 objsync_hashes(void);
void npcsync_frame(void);
void objsync_relay_links(void* obj, u32 event);

// Layout is mirrored in shared/Mailbox.cs; these catch accidental drift.
#define BBOL_OFF(f) __builtin_offsetof(Mailbox, f)
_Static_assert(sizeof(Remote) == 0x70, "Remote");
_Static_assert(sizeof(Local) == 0x70, "Local");
_Static_assert(BBOL_OFF(loadScene) == 0x40, "loadScene");
_Static_assert(BBOL_OFF(local) == 0x60, "local");
_Static_assert(BBOL_OFF(remote) == 0xD0, "remote");
_Static_assert(BBOL_OFF(ensureTags) == 0x300, "ensureTags");
_Static_assert(sizeof(ObjRec) == 0x100, "ObjRec");
_Static_assert(BBOL_OFF(invCount) == 0x43E0, "invCount");
_Static_assert(BBOL_OFF(invSeq) == 0x44F0, "invSeq");
_Static_assert(BBOL_OFF(evtSeq) == 0x4500, "evtSeq");
_Static_assert(BBOL_OFF(offerText) == 0x4524, "offerText");
_Static_assert(BBOL_OFF(heapReserve) == 0x4604, "heapReserve");
_Static_assert(BBOL_OFF(invSetMask) == 0x4634, "invSetMask");
_Static_assert(sizeof(NpcPose) == 0x40, "NpcPose");
_Static_assert(BBOL_OFF(npcRank) == 0x463C, "npcRank");
_Static_assert(BBOL_OFF(npcOut) == 0x4670, "npcOut");
_Static_assert(BBOL_OFF(payOutSeq) == 0x8670, "payOutSeq");
_Static_assert(BBOL_OFF(payIn) == 0x8688, "payIn");
_Static_assert(BBOL_OFF(outHead) == 0x380, "outHead");
_Static_assert(BBOL_OFF(out) == 0x3C0, "out");
_Static_assert(BBOL_OFF(in) == 0x3C0 + OBJ_RING * 0x100, "in");

#endif
