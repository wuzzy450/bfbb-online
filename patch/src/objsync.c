// Live object sync: every persistent scene object (what zSceneSave writes for a save game) is
// serialized with its own Save function into a scratch serializer slot and fingerprinted, a slice
// of objects per frame. A changed fingerprint is reported to the bridge (out ring) with the
// object's save bits; states from the party (in ring) are applied with the object's own Load
// function, exactly as loading a save would.
#include "game.h"
#include "mailbox.h"

#define SCRATCH_TAG  0x42424F53u   // 'BBOS' serializer slot used as scratch (400 bytes)
#define SCRATCH_SIZE 400u
#define OBJ_MAX      4096
#define SCAN_PER_FRAME 96
#define VCALL        1u
#define TBOX_PLRSTATE 0xE0         // _zEntTeleportBox::currPlrState (zEntTeleportBox_Save 0x8012D658)            // NPCs: virtual Save/Load through the vtable at +0x1B8

// zSceneSave / zSceneLoad dispatch (base type -> function), extracted from the game.
static const u32 saveFn[0x46] = {
    [0x01] = 0x80092508, [0x04] = 0x8006451C, [0x05] = 0x80092A24, [0x06] = 0x800A93CC,
    [0x07] = 0x80123694, [0x0B] = 0x80091C00, [0x0D] = 0x800A6D28, [0x0E] = 0x8004D4B4,
    [0x10] = 0x800AC944, [0x11] = 0x8002CC00, [0x12] = 0x800A8934, [0x13] = 0x800441F4,
    [0x16] = 0x80015C78, [0x17] = 0x80062418, [0x18] = 0x8005677C, [0x1A] = 0x800B5C7C,
    [0x1B] = 0x80061064, [0x1C] = 0x8009B294, [0x1D] = 0x801189B8, [0x1E] = 0x8005362C,
    [0x1F] = 0x80052614, [0x20] = 0x800B9FB8, [0x21] = 0x800BC794, [0x24] = 0x8001FE94,
    [0x25] = 0x8009E42C, [0x29] = 0x8005444C, [0x2A] = 0x800B5320, [0x2B] = VCALL,
    [0x31] = 0x8012D610, [0x35] = 0x80134A9C, [0x39] = 0x8017259C, [0x3E] = 0x8017C13C,
    [0x45] = 0x8017F4C8,
};
static const u32 loadFn[0x46] = {
    [0x01] = 0x80092528, [0x04] = 0x80064588, [0x05] = 0x80092A44, [0x06] = 0x800A93EC,
    [0x07] = 0x801236B4, [0x0B] = 0x80091C20, [0x0D] = 0x800A6D48, [0x0E] = 0x8004D504,
    [0x10] = 0x800AC964, [0x11] = 0x8002CC20, [0x12] = 0x800A8954, [0x13] = 0x80044214,
    [0x16] = 0x80015CC8, [0x17] = 0x80062438, [0x18] = 0x80056804, [0x1A] = 0x800B5C9C,
    [0x1B] = 0x800610A8, [0x1C] = 0x8009B2B4, [0x1D] = 0x801189D8, [0x1E] = 0x8005364C,
    [0x1F] = 0x80052634, [0x20] = 0x800B9FD8, [0x21] = 0x800BC7B4, [0x24] = 0x8001FEB4,
    [0x25] = 0x8009E44C, [0x29] = 0x8005446C, [0x2A] = 0x800B5340, [0x2B] = VCALL,
    [0x31] = 0x8012D67C, [0x35] = 0x80134A54, [0x39] = 0x801725BC, [0x3E] = 0x8017C15C,
    [0x45] = 0x8017F4E8,
};

// Types that carry campaign/world state worth sharing live. Left out: things that tick on their
// own every frame (timers, move points, cameras, lights, fog, env, sfx, surfaces, gusts, UI).
static const u8 liveType[0x46] = {
    [0x01] = 1,  // trigger
    [0x04] = 1,  // pickup (shinies, spatulas, socks, underwear...)
    [0x06] = 1,  // platform
    [0x0B] = 1,  // simple object
    [0x10] = 1,  // portal
    [0x11] = 1,  // group
    [0x16] = 1,  // counter
    [0x17] = 1,  // hangable
    [0x18] = 1,  // button / pressure plate
    [0x1B] = 1,  // destructible
    [0x1E] = 1,  // dispatcher
    [0x1F] = 1,  // conditional
    [0x29] = 1,  // enemy generator
    [0x2A] = 1,  // script
    [0x2B] = 1,  // NPC / enemy
    [0x31] = 1,  // teleport box
    [0x35] = 1,  // task box
    [0x39] = 1,  // taxi
};

typedef struct {
    u32 idtag;
    s32 baseoff;
    u32* ctxt;     // st_SERIAL_CLIENTINFO*: idtag, membuf, trueoff, actsize
    s32 warned;
    s32 curele;
    s32 bitidx;
    s32 bittally;
} xSerial;

typedef void (*fn_sl)(void* obj, xSerial* s);

static xSerial xs;
static int muteTaskEvents;

// ztaskbox::complete: zEntEvent(this, this, OnComplete). A teammate's task state (a conversation
// they finished) is applied without its OnComplete links: what those do (rewards, gates, the next
// task) reaches us as those objects' own states, and here they would start the follow-up dialog.
void bbol_taskbox_complete_event(void* from, void* to, u32 event)
{
    if (muteTaskEvents) return;
    zEntEvent3(from, to, event);
}
// zEntEvent(from, fromEvent, to, toEvent, param, widget, force): the game's event dispatch.
typedef void (*fn_event)(void* from, u32 fromEvent, void* to, u32 toEvent, const f32* param, void* widget, s32 force);
#define ZENTEVENT_FULL ((fn_event)0x8001F758)
#define EVT_GIVESHINY 53
extern int inPuppet;
static int relaying;

// stubs.S bbol_event_stub: every GiveShinyObjects event before it is handled. A payment (negative
// amount, e.g. a shiny door) is reported so teammates replay what it set off; 0 = drop the event
// (a replay: the shared inventory already has the party's shinies).
u32 bbol_shiny_event(u8* from, u32 fromEvent, u8* to, u32 ev, const f32* param)
{
    if (relaying) return 0;
    if (from && param && param[0] < 0 && !inPuppet && mb.bridge == BBOL_BRIDGE && AT(u32, GLOBALS + G_SCENECUR)) {
        mb.payOutId = AT(u32, from);
        mb.payOutEvent = fromEvent;
        mb.payOutScene = AT(u32, (u8*)AT(u32, GLOBALS + G_SCENECUR));
        mb.payOutSeq = mb.payOutSeq + 1;
    }
    return 1;
}

// Sends obj's links for `event` (what the game does after obj handled it) without obj's own
// handler running again. Shinies given/charged along the way are dropped; collected pickups stay collected.
#define PICKUP_STATE 0x16C
#define PICKUP_GRABBING 0x4u   // flying to the player; collectPickup() then gives the item
static int pickup_busy(u8* b);
static u8* scene_base(u32 i, u32* count);
static u8 personal[OBJ_MAX];

// A pickup a trigger chain hands out (race reward, Mr. Krabs' bought spatula: race_scan, personal 4)
// belongs to the player who earned it; a teammate's replay mustn't put it in this game too.
static int reward_pickup(u8* p)
{
    u32 count;
    scene_base(0, &count);
    for (u32 i = 0; i < count && i < OBJ_MAX; i++)
        if (scene_base(i, &count) == p) return personal[i] == 4;
    return 0;
}
void objsync_relay_links(void* obj, u32 event)
{
    u8* o = obj;
    u8* l = (u8*)AT(u32, o + 8);
    if (!l) return;
    relaying++;
    for (u32 i = 0; i < o[5]; i++, l += 0x20) {
        if (AT(u16, l) != event) continue;
        void* dst = zSceneFindObject(AT(u32, l + 4));
        // a pickup already collected here (the payer took Mr. Krabs' golden spatula and that synced
        // first): showing it again would let this game collect it a second time
        if (!dst || (((u8*)dst)[4] == 0x04 && ((AT(u32, (u8*)dst + PICKUP_STATE) & 0x0C) || pickup_busy(dst) || reward_pickup(dst)))) continue;
        void* w = AT(u32, l + 0x18) ? zSceneFindObject(AT(u32, l + 0x18)) : 0;
        ZENTEVENT_FULL(o, event, dst, AT(u16, l + 2), (const f32*)(l + 8), w, 0);
    }
    relaying--;
}

static void apply_pays(void)
{
    while (mb.payInTail != mb.payInHead) {
        u32 k = mb.payInTail % PAY_RING;
        void* o = zSceneFindObject(mb.payIn[k][0]);
        if (o) objsync_relay_links(o, mb.payIn[k][1]);
        mb.payInTail = mb.payInTail + 1;
    }
}

static u32 hashes[OBJ_MAX];
static u32 deadHash, aliveHash;   // the 1-bit enemy states' fingerprints
static u32 reviveUntil;
static u8 partyDead[OBJ_MAX];      // enemies a teammate killed in this scene visit

// Enemies we killed only because a teammate did: their death gives no loot here (the teammate's game
// gave it; the shinies reach us through the shared inventory).
#define NOLOOT_MAX 64
static u32 noLoot[NOLOOT_MAX];
static u32 noLootNext;
static void kill_for_party(u8* b)
{
    noLoot[noLootNext++ % NOLOOT_MAX] = (u32)b;
    zEntEvent(b, 441);   // NPCKillQuietly
}

// stubs.S: zNPCCommon::GiveReward(this)
u32 bbol_reward_allowed(u8* npc)
{
    for (u32 i = 0; i < NOLOOT_MAX; i++)
        if (noLoot[i] == (u32)npc) { noLoot[i] = 0; return 0; }
    return 1;
}           // mb.frames: until then a dead enemy coming back is the scene reset's doing

void objsync_scene_reset(void) { reviveUntil = mb.frames + 120; }
static u32 scanCursor;

static u32* scratch_begin(void)
{
    xSerial_setClient(&xs, SCRATCH_TAG);
    u32* mem = (u32*)xs.ctxt[1];
    memset(mem, 0, SCRATCH_SIZE);
    return mem;
}

static fn_sl resolve(const u32* tbl, u8* b, u32 vtoff)
{
    u8 t = b[4];
    if (t >= 0x46 || !tbl[t]) return 0;
    if (tbl[t] != VCALL) return (fn_sl)tbl[t];
    u8* vt = (u8*)AT(u32, b + 0x1B8);
    if ((u32)vt < 0x80000000u || (u32)vt >= 0x81800000u) return 0;
    return (fn_sl)AT(u32, vt + vtoff);
}

static u32 fnv(const u32* w, u32 bits)
{
    u32 h = 2166136261u ^ bits, n = (bits + 31) >> 5;
    for (u32 i = 0; i < n; i++) {
        u32 v = w[i];
        if (i == n - 1 && (bits & 31)) v &= (1u << (bits & 31)) - 1u;
        h = (h ^ v) * 16777619u;
    }
    return h ? h : 1;
}

static u8* scene_base(u32 i, u32* count)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    if (!sc) { *count = 0; return 0; }
    *count = AT(u32, sc + 0x74);
    if (i >= *count) return 0;
    u8** base = (u8**)AT(u32, sc + 0x78);
    return base ? base[i] : 0;
}


// A pickup being collected right now: report it once it has settled (gone), and never overwrite
// it, or the receiving game would finish the collection and give the item to its own player.
static int pickup_busy(u8* b) { return b[4] == 0x04 && (AT(u32, b + PICKUP_STATE) & PICKUP_GRABBING); }

#define PICKUP_ASSET 0x170
#define PICKUP_KEY   13            // Deluxe's hub/level keys (zAssetPickup type 1, index 13)
#define EVT_PICKUP   35
#define EVT_RESET    10
#define EVT_DESTROY  22
#define DOBJ_STATE   0xD8          // zEntDestructObj::state
#define DOBJ_DESTROYED 2
#define EVT_NPC_KILL_QUIETLY 441
#define NPC_VTABLE   0x1B8
#define NPC_ISALIVE  0x7C

static int is_key(u8* b)
{
    if (b[4] != 0x04) return 0;
    u8* p = (u8*)AT(u32, b + PICKUP_ASSET);
    return (u32)p >= 0x80000000u && (u32)p < 0x81800000u && p[4] == 1 && p[5] == PICKUP_KEY;
}

typedef s32 (*fn_alive)(void* npc);
static fn_alive npc_alive_fn(u8* b)
{
    u8* vt = (u8*)AT(u32, b + NPC_VTABLE);
    if ((u32)vt < 0x80000000u || (u32)vt >= 0x81800000u) return 0;
    u32 f = AT(u32, vt + NPC_ISALIVE);
    return (f >= 0x80003000u && f < 0x80400000u) ? (fn_alive)f : 0;
}

// Objects the game doesn't save but teammates still have to agree on while they play the scene:
// keys (their pickup event counts the chest down), breakables, enemy generators, and enemies
// (only whether they are alive: each game runs its own AI).
static int transient(u8* b)
{
    u8 t = b[4];
    return t == 0x1B || t == 0x29 || t == 0x2B || is_key(b);
}

// An object that starts a cinematic (links to a cutscene manager) stays personal: the cinematic is
// played once per player, so a teammate who missed it still gets it from their own trigger.
static u8 personal[OBJ_MAX];   // (declared above) 0 unknown, 1 shared, 2 personal, 3 ticks on its own (a clock),
                               // 4 a race's reward (reported for counting only), 5 a race's end chain
static u16 noisyFrame[OBJ_MAX]; // low 16 bits of mb.frames when the current 2 s window started
static u8 noisyCount[OBJ_MAX];

static int starts_cinematic(u8* b)
{
    u32 n = b[5];
    u8* l = (u8*)AT(u32, b + 8);
    if (!n || (u32)l < 0x80000000u) return 0;
    for (u32 k = 0; k < n; k++) {
        u8* d = (u8*)zSceneFindObject(AT(u32, l + k * 0x20 + 4));
        if (d && d[4] == 0x28) return 1;
    }
    return 0;
}

// Races and other timed challenges: a trigger (the finish line) gives a pickup (the reward) with
// Pickup(35), and that pickup's links run the level end. Each player races on their own clock: a
// teammate winning must not end the level for us. So the finish trigger, the reward and everything
// they set off (a few links deep) are per player; the reward is still reported so the server counts
// the first collection, but never applied to a teammate's game.
#define RACE_MAX   128
#define RACE_DEPTH 4
static u8 raceDone;
static u8* raceObj[RACE_MAX];
static u8 raceN;

static int race_has(u8* b)
{
    for (u32 k = 0; k < raceN; k++) if (raceObj[k] == b) return 1;
    return 0;
}

static void race_add(u8* b, u32 depth)
{
    if (!b || race_has(b) || raceN >= RACE_MAX) return;
    raceObj[raceN++] = b;
    if (depth >= RACE_DEPTH) return;
    u32 n = b[5];
    u8* l = (u8*)AT(u32, b + 8);
    if (!n || (u32)l < 0x80000000u || (u32)l >= 0x81800000u) return;
    for (u32 k = 0; k < n; k++) {
        u8* d = (u8*)zSceneFindObject(AT(u32, l + k * 0x20 + 4));
        // stop at the player, portals (leaving is the player's own choice) and cinematics
        if (d && d[4] != 0x10 && d[4] != 0x28 && d[4] != 0x2C) race_add(d, depth + 1);
    }
}

static void race_scan(void)
{
    raceDone = 1;
    raceN = 0;
    u32 count;
    scene_base(0, &count);
    if (count > OBJ_MAX) count = OBJ_MAX;
    for (u32 i = 0; i < count; i++) {
        u8* b = scene_base(i, &count);
        if (!b) continue;
        u32 n = b[5];
        u8* l = (u8*)AT(u32, b + 8);
        if (!n || (u32)l < 0x80000000u || (u32)l >= 0x81800000u || b[4] == 0x04) continue;
        for (u32 k = 0; k < n; k++) {
            if (AT(u16, l + k * 0x20 + 2) != EVT_PICKUP) continue;
            u8* d = (u8*)zSceneFindObject(AT(u32, l + k * 0x20 + 4));
            if (d && d[4] == 0x04) { race_add(b, 0); break; }
        }
    }
    for (u32 i = 0; i < count; i++) {
        u8* b = scene_base(i, &count);
        if (b && race_has(b)) personal[i] = b[4] == 0x04 ? 4 : 5;
    }
    mb.dbgRender[29] = raceN;
}

static int tracked(u8* b, u32 i)
{
    if (!b) return 0;
    u8 t = b[4];
    if (t >= 0x46 || !saveFn[t]) return 0;
    if (i < OBJ_MAX) {
        if (!personal[i]) personal[i] = starts_cinematic(b) ? 2 : 1;
        if (personal[i] >= 2 && personal[i] != 4) return 0;
    }
    if (t == 0x2B) return !(mb.flags & 256u) && npc_alive_fn(b) != 0;
    return (liveType[t] && (AT(u16, b + 6) & 2)) || transient(b);
}

// Serializes object b into the scratch slot; returns its bit count (0 = nothing).
static u32 save_obj(u8* b)
{
    if (b[4] == 0x1B) {
        // breakables: one bit, broken. Their state number also cycles through init/reset values
        // nobody can see (and a scene reset sets the other one), and Load alone can't break one.
        u32* mem = scratch_begin();
        mem[0] = AT(u32, b + DOBJ_STATE) == DOBJ_DESTROYED ? 1u : 0u;
        return 1;
    }
    if (b[4] == 0x2B) {
        // enemies: one bit, alive
        fn_alive a = npc_alive_fn(b);
        if (!a) return 0;
        u32* mem = scratch_begin();
        mem[0] = a(b) ? 1u : 0u;
        return 1;
    }
    fn_sl f = resolve(saveFn, b, 0x34);
    if (!f) return 0;
    scratch_begin();
    if (b[4] == 0x31) {
        // teleport boxes: only open/closed is shared. Their save also holds where THIS game's player
        // is in the box (inside / teleporting...); a teammate's copy made our box move our player.
        u32 keep = AT(u32, b + TBOX_PLRSTATE);
        AT(u32, b + TBOX_PLRSTATE) = 0;   // PLAYER_STATE_OUTSIDE
        f(b, &xs);
        AT(u32, b + TBOX_PLRSTATE) = keep;
    } else
        f(b, &xs);
    return xs.warned ? 0 : (u32)xs.bittally;
}

u32 objsync_hashes(void) { return (u32)hashes; }

void objsync_reset(void)
{
    { u32 z = 0, o = 1; deadHash = fnv(&z, 1); aliveHash = fnv(&o, 1); }
    scanCursor = 0;
    mb.objPrimed = 0;
    raceDone = 0;
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    mb.objScene = sc ? AT(u32, sc) : 0;
    for (u32 i = 0; i < NOLOOT_MAX; i++) noLoot[i] = 0;
    for (u32 i = 0; i < OBJ_MAX; i++) { hashes[i] = 0; personal[i] = 0; noisyCount[i] = 0; partyDead[i] = 0; }
}

static void report(u32 i, u8* b, u32 bits)
{
    u32 head = mb.outHead;
    if (head - mb.outTail >= OBJ_RING || bits > OBJ_DATA * 8) { mb.objDropped++; return; }
    volatile ObjRec* r = &mb.out[head % OBJ_RING];
    r->scene = mb.objScene;
    r->id = AT(u32, b);
    r->index = (u16)i;
    r->bits = (u16)bits;
    r->type = b[4];
    r->pad_[0] = r->pad_[1] = 0;
    r->pad_[2] = (i < OBJ_MAX && personal[i] == 4) ? 1 : 0;   // count it, don't share it
    if (b[4] == 0x04) {
        // what kind of pickup (the server counts each spatula once, whoever's game collected it)
        u8* pa = (u8*)AT(u32, b + PICKUP_ASSET);
        if ((u32)pa >= 0x80000000u && (u32)pa < 0x81800000u) { r->pad_[0] = pa[4]; r->pad_[1] = pa[5]; }
    }
    memcpy((void*)r->data, (void*)xs.ctxt[1], ((bits + 31) >> 5) * 4);
    mb.outHead = head + 1;
}

// teammates' pickup states that arrived while our copy was flying to our player: applied once it
// settles, unless it ended up collected here too (a grab cut short by a death or warp would
// otherwise leave the pickup up here and gone for the teammate)
#define DEFER_MAX 8
static ObjRec deferred[DEFER_MAX];
static u32 deferredCount;

static void apply_one(volatile ObjRec* r);

static void apply_deferred(void)
{
    u32 n = deferredCount, keep = 0;
    deferredCount = 0;
    for (u32 k = 0; k < n; k++) {
        u32 count;
        u8* b = scene_base(deferred[k].index, &count);
        if (deferred[k].scene != mb.objScene || !b || AT(u32, b) != deferred[k].id) continue;
        if (pickup_busy(b)) { if (keep != k) deferred[keep] = deferred[k]; keep++; continue; }
        if (!(AT(u32, b + PICKUP_STATE) & 0x0C)) { apply_one(&deferred[k]); mb.dbgRender[8]++; }   // [8] held states applied
    }
    deferredCount += keep;
}

static void apply_in(void)
{
    if (deferredCount) apply_deferred();
    while (mb.inTail != mb.inHead) {
        volatile ObjRec* r = &mb.in[mb.inTail % OBJ_RING];
        // our own conversation still running: its task box changes when it ends, not now
        u32 c0;
        u8* b0 = scene_base(r->index, &c0);
        if (b0 && r->scene == mb.objScene && b0[4] == 0x35 && r->type == 0x35 && AT(u32, TALK_SHARED + 8)) break;
        if (b0 && r->scene == mb.objScene && AT(u32, b0) == r->id && pickup_busy(b0) && deferredCount < DEFER_MAX) {
            u32 n = (r->bits + 7) / 8 + 0x10;
            memcpy(&deferred[deferredCount++], (void*)r, n > sizeof(ObjRec) ? sizeof(ObjRec) : n);
            mb.dbgRender[4]++;   // [4] teammates' pickup states held while ours was flying
            mb.inTail = mb.inTail + 1;
            continue;
        }
        apply_one(r);
        mb.inTail = mb.inTail + 1;
    }
}

static void apply_one(volatile ObjRec* r)
{
    u32 count;
    {
        u8* b = scene_base(r->index, &count);
        if (r->scene == mb.objScene && b && AT(u32, b) == r->id && b[4] == r->type
            && r->bits && r->bits <= OBJ_DATA * 8 && !(r->index < OBJ_MAX && personal[r->index] >= 3)) {
            if (b[4] == 0x1B) {
                // a teammate broke this (or it came back): what its Destroy / Reset events do
                int broken = AT(u32, b + DOBJ_STATE) == DOBJ_DESTROYED, want = r->data[3] & 1;
                if (mb.flags & 8192u) ;
                else if (want && !broken) zEntEvent(b, EVT_DESTROY);
                else if (!want && broken) zEntEvent(b, EVT_RESET);
                u32* mem = scratch_begin();
                mem[0] = AT(u32, b + DOBJ_STATE) == DOBJ_DESTROYED ? 1u : 0u;
                if (r->index < OBJ_MAX) hashes[r->index] = fnv(mem, 1);
                mb.objApplied++;
                return;
            }
            if (b[4] == 0x2B) {
                // a teammate's game killed this enemy: kill ours too (quietly: no loot twice)
                fn_alive a = npc_alive_fn(b);
                if (!(r->data[3] & 1) && r->index < OBJ_MAX) partyDead[r->index] = 1;
                if (a && !(r->data[3] & 1) && a(b)) {
                    kill_for_party(b);
                    u32* mem = scratch_begin();
                    mem[0] = 0;
                    if (r->index < OBJ_MAX) hashes[r->index] = fnv(mem, 1);
                    mb.objApplied++;
                }
                return;
            }
            // a key someone else collected: what its pickup event does (count the chest down)
            // has to happen here too, once, when it goes from collectable to collected
            u32 st = AT(u32, b + PICKUP_STATE);
            int keyWasUp = is_key(b) && (AT(u16, b + 6) & 1) && (st & 0x40) && !(st & 0x3C);
            int pickWasUp = b[4] == 0x04 && !is_key(b) && (st & 0x40) && !(st & 0x3C);
            if (b[4] == 0x04 && (st & 0x0C) && r->index < OBJ_MAX && !pickup_busy(b)) {
                // we collected it too, in the same instant, and the scan hasn't reported it yet: report
                // ours first (the server counts a sock grabbed in two games once), keep our state
                u32 bits = save_obj(b);
                u32 h = bits ? fnv((u32*)xs.ctxt[1], bits) : 0;
                if (bits && h != hashes[r->index]) {
                    hashes[r->index] = h;
                    report(r->index, b, bits);
                    return;
                }
            }
            fn_sl f = pickup_busy(b) ? 0 : resolve(loadFn, b, 0x38);
            if (f) {
                u32* mem = scratch_begin();
                memcpy(mem, (void*)r->data, ((r->bits + 31) >> 5) * 4);
                xSerial_setClient(&xs, SCRATCH_TAG);
                muteTaskEvents = b[4] == 0x35;
                u32 talk0 = AT(u32, TALK_SHARED + 8);
                u32 plr = b[4] == 0x31 ? AT(u32, b + TBOX_PLRSTATE) : 0;
                f(b, &xs);
                if (b[4] == 0x31) AT(u32, b + TBOX_PLRSTATE) = plr;   // our player's place in the box stays ours
                muteTaskEvents = 0;
                if (AT(u32, TALK_SHARED + 8) != talk0) { mb.dbgRender[2]++; mb.dbgRender[3] = AT(u32, b); }
                // remember the applied state so it isn't reported back as our own change
                u32 bits = save_obj(b);
                if (bits && r->index < OBJ_MAX) hashes[r->index] = fnv((u32*)xs.ctxt[1], bits);
                mb.objApplied++;
                st = AT(u32, b + PICKUP_STATE);
                if (keyWasUp && (st & 0x0C)) zEntEvent(b, EVT_PICKUP);
                // any other pickup a teammate took: what its pickup sets off here too (a chest's
                // spatula hides the chest...), without collecting it again
                else if (pickWasUp && (st & 0x0C)) objsync_relay_links(b, EVT_PICKUP);
            }
        }
    }
}

// Once per game frame (scene running).
void objsync_frame(void)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    if (!sc) return;
    if (AT(u32, sc) != mb.objScene) objsync_reset();
    if (!raceDone) race_scan();
    if (mb.flags & 32768u) mb.inTail = mb.inHead;   // diagnostics: drop the party's states
    else apply_in();
    apply_pays();

    u32 count;
    scene_base(0, &count);
    if (count > OBJ_MAX) count = OBJ_MAX;
    mb.objCount = count;
    for (u32 n = 0; n < SCAN_PER_FRAME && count; n++) {
        u32 i = scanCursor;
        if (++scanCursor >= count) { scanCursor = 0; mb.objPrimed = 1; }
        u8* b = scene_base(i, &count);
        if (!tracked(b, i) || pickup_busy(b)) continue;
        u32 bits = save_obj(b);
        if (!bits) continue;
        u32 h = fnv((u32*)xs.ctxt[1], bits);
        if (h == hashes[i]) continue;
        if (b[4] == 0x2B && h == aliveHash && !(mb.flags & 4096u) && (partyDead[i] || (hashes[i] == deadHash && mb.frames < reviveUntil))) {
            // an enemy the party killed that only now appears in our game (it hadn't spawned when the
            // kill arrived), or
            // an enemy that was dead came back because the scene was reset: it stays dead (enemies that
            // respawn on their own later still do)
            kill_for_party(b);
            continue;
        }
        hashes[i] = h;
        // something that changes all the time on its own (a race clock counting up every frame) is
        // per player, like timers: after 4 changes in 2 seconds stop sharing it
        if (mb.objPrimed && b[4] != 0x04 && b[4] != 0x2B && !(mb.flags & 2048u)) {
            u16 now = (u16)mb.frames;
            if ((u16)(now - noisyFrame[i]) > 120) { noisyFrame[i] = now; noisyCount[i] = 0; }
            if (++noisyCount[i] > 4 && personal[i] == 1) { personal[i] = 3; mb.objNoisy++; continue; }
        }
        // once primed, also report objects seen for the first time: some only become tracked during
        // play (e.g. a counter whose save flag gets set) and their first state may already differ
        if (mb.objPrimed) report(i, b, bits);
    }
}
