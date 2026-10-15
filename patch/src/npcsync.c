// Enemy and boss sync. Every game runs its own AI, and an NPC's AI only ever sees that game's own
// player. So each NPC is driven by one game (its "owner"), which streams the NPC's pose, animation
// and (bosses) AI goal and hit points (npcOut); every other game in the scene puts its copy in the
// same state each frame (npcIn). The local AI keeps running in the following games but starts every
// frame from the owner's state.
//  - Enemies (robots, tikis, duplotrons, jellyfish): owned by the game whose player is nearest, so
//    everyone sees an enemy chase and attack whoever is actually closest to it.
//  - Bosses and sub-bosses: owned by one game for the whole fight (the lowest party id at first).
//    A hit lands in the hitter's game (its copy is where everyone sees it); that game then takes
//    the boss over (a newer "epoch"), so its hit, the boss's reaction and the lost hit point reach
//    everyone. The boss's health is one shared value.
// Scene memory is laid out identically in every game (same object, animation table and goal
// addresses), so animation states and goals are passed as the owner's pointers / ids and checked
// against our own tables before use.
#include "game.h"
#include "mailbox.h"

#define OBJ_MAX     4096
#define NPC_TYPE    0xF4             // xNPCBasic::myNPCType ('NTR0' ...)
#define ENT_MODEL   0x24
#define ENT_FRAME   0x48
#define NPC_VTABLE  0x1B8
#define NPC_ISALIVE 0x7C
#define NPC_PSYCHE  0x228            // zNPCCommon::psy_instinct
#define OWN_MARGIN  2.0f             // hysteresis (units) before an enemy moves to a nearer player
#define SNAP_DIST2  (12.0f * 12.0f)  // further than this from the owner's pose: jump there
#define BOSS_ORPHAN 90               // frames without the owner's poses before a follower takes over

#define PF_VALID    1u
#define PF_BOSS     2u
#define PF_HP       4u

typedef s32 (*fn_alive)(void* npc);
typedef s32 (*fn_gid)(void* psyche);
typedef s32 (*fn_goalset)(void* psyche, s32 gid, s32 unk);
typedef void (*fn_setstate)(void* single, void* state, f32 time);
#define GIDOfActive  ((fn_gid)0x8010D5C4)
#define GIDOfPending ((fn_gid)0x8010D604)
#define GoalSet      ((fn_goalset)0x8010D730)
#define xAnimPlaySetState ((fn_setstate)0x80007F1C)

static u8 own[OBJ_MAX];              // 1 = our game drives this NPC
static u16 epoch[OBJ_MAX];           // bosses: ownership generation
static s16 lastHp[OBJ_MAX];          // bosses: hit points after we last applied / sent them
static u16 seenFrame[OBJ_MAX];       // bosses: low 16 bits of mb.frames when the owner's pose last came
static s32 lastGoal[OBJ_MAX];        // goal we last asked for (followers)
static u32 sceneOf;
static u32 sceneFrames;              // frames since we entered this scene
static u32 inSeqSeen;
static u32 sendTick;

static int valid_ptr(const void* p) { return (u32)p >= 0x80000000u && (u32)p < 0x82000000u; }

static int is_boss_type(u32 t) { return (t >> 16) == 0x4E54 && (((t >> 8) & 0xFF) == 'B' || ((t >> 8) & 0xFF) == 'H'); }

static int synced_kind(u8* b)
{
    u32 t = AT(u32, b + NPC_TYPE);
    if ((t >> 16) != 0x4E54) return 0;         // 'NT..'
    u8 c = (u8)(t >> 8);
    // robots, tikis, duplotrons, jellyfish; bosses and sub-bosses
    return c == 'R' || c == 'T' || c == 'D' || c == 'A' || c == 'B' || c == 'H';
}

// Bosses' hit points and round (decomp offsets, checked in the Deluxe DOL); hp 0 = not known
// (ownership by party id only), round 0 = none. Robo-Plankton has no hit points: his fight
// advances active_territory (0x698) as fuses blow, so his "hp" counts down from HP_INV_BASE.
#define HP_INV_BASE 100
static void hp_fields(u32 t, u32* hp, u32* round, int* inv)
{
    *hp = *round = 0; *inv = 0;
    switch (t) {
    case 0x4E544230: *hp = 0x2D4; *round = 0x2D8; break;   // NTB0 Robo-Sandy
    case 0x4E544231: *hp = 0x2B8; *round = 0x2BC; break;   // NTB1 Robo-Patrick
    case 0x4E544233: *hp = 0x2C4; *round = 0x2C8; break;   // NTB3 Robo-SpongeBob (zNPCB_SB2::check_life)
    case 0x4E544234: *hp = 0x698; *inv = 1; break;         // NTB4 Robo-Plankton (next_territory)
    case 0x4E544830: *hp = 0x2C4; *round = 0x2BC; break;   // NTH0 King Jellyfish
    case 0x4E544831: *hp = 0x2C4; *round = 0x2C8; break;   // NTH1 Flying Dutchman
    case 0x4E544832: *hp = 0x2B8; *round = 0x2BC; break;   // NTH2 Prawn
    }
}

static s32 hp_get(u8* b, u32 hpo, int inv)
{
    s32 v = AT(s32, b + hpo);
    return inv ? HP_INV_BASE - v : v;
}

static void hp_put(u8* b, u32 hpo, int inv, s32 v)
{
    AT(s32, b + hpo) = inv ? HP_INV_BASE - v : v;
}

static int alive(u8* b)
{
    u8* vt = (u8*)AT(u32, b + NPC_VTABLE);
    if (!valid_ptr(vt)) return 0;
    u32 f = AT(u32, vt + NPC_ISALIVE);
    if (f < 0x80003000u || f >= 0x80400000u) return 0;
    return ((fn_alive)f)(b) != 0;
}

static u8* npc_at(u32 i, u32 id)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    if (!sc || i >= AT(u32, sc + 0x74)) return 0;
    u8** base = (u8**)AT(u32, sc + 0x78);
    u8* b = base ? base[i] : 0;
    if (!b || b[4] != 0x2B || AT(u32, b) != id || !synced_kind(b)) return 0;
    return b;
}

static f32 inv_sqrt(f32 x)
{
    union { f32 f; u32 u; } v = { x };
    v.u = 0x5F3759DFu - (v.u >> 1);
    f32 y = v.f;
    y = y * (1.5f - 0.5f * x * y * y);
    y = y * (1.5f - 0.5f * x * y * y);
    return y;
}

static f32 dist2(const volatile f32* a, const volatile f32* b)
{
    f32 dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
    return dx * dx + dy * dy + dz * dz;
}

static f32 dist(f32 d2) { return d2 * inv_sqrt(d2 > 1e-6f ? d2 : 1e-6f); }

static u8* frame_of(u8* b) { u8* fr = (u8*)AT(u32, b + ENT_FRAME); return valid_ptr(fr) ? fr : 0; }

// first animation layer of the NPC's model (xModelInstance.Anim -> xAnimPlay.Single)
static u8* anim_single(u8* b, u8** table)
{
    u8* mdl = (u8*)AT(u32, b + ENT_MODEL);
    if (!valid_ptr(mdl)) return 0;
    u8* play = (u8*)AT(u32, mdl + MI_ANIM);
    if (!valid_ptr(play) || !AT(u16, play + 4)) return 0;
    u8* single = (u8*)AT(u32, play + 8);
    if (!valid_ptr(single)) return 0;
    if (table) *table = (u8*)AT(u32, play + 0x10);
    return single;
}

static int state_in_table(u8* table, u32 st)
{
    if (!valid_ptr(table)) return 0;
    u8* s = (u8*)AT(u32, table + 0x0C);
    for (int n = 0; n < 400 && valid_ptr(s); n++, s = (u8*)AT(u32, s))
        if ((u32)s == st) return 1;
    return 0;
}

static u8* psyche_of(u8* b)
{
    u8* p = (u8*)AT(u32, b + NPC_PSYCHE);
    return valid_ptr(p) ? p : 0;
}

static u32 lowest_rank(void)
{
    u32 r = mb.npcRank;
    for (int k = 0; k < MAX_REMOTE; k++)
        if (mb.remote[k].active && mb.npcRemoteRank[k] && mb.npcRemoteRank[k] < r) r = mb.npcRemoteRank[k];
    return r;
}

// Enemies: us if our player is nearest (with hysteresis; ties go to the lower party id).
static int decide_enemy(u32 i, u8* b, const f32* me)
{
    u8* fr = frame_of(b);
    if (!fr) return 0;
    const f32* p = (const f32*)(fr + FR_POS);
    f32 best = 1e30f;
    u32 bestRank = 0xFFFFFFFFu;
    for (int k = 0; k < MAX_REMOTE; k++) {
        if (!mb.remote[k].active || (mb.remote[k].flags & RF_HIDDEN)) continue;
        f32 d = dist2(p, mb.remote[k].pos);
        if (d < best || (d == best && mb.npcRemoteRank[k] < bestRank)) { best = d; bestRank = mb.npcRemoteRank[k]; }
    }
    if (best >= 1e30f) return 1;               // alone in the scene
    f32 a = dist(dist2(p, me)), o = dist(best);
    if (own[i]) return a <= o + OWN_MARGIN;
    if (a + OWN_MARGIN < o) return 1;
    return a <= o && mb.npcRank < bestRank;
}

static void set_pose(u8* b, volatile NpcPose* q)
{
    u8* fr = frame_of(b);
    u8* mdl = (u8*)AT(u32, b + ENT_MODEL);
    if (!fr) return;
    f32* fp = (f32*)(fr + FR_POS);
    f32 np[3];
    f32 t = dist2(q->pos, fp) > SNAP_DIST2 ? 1.0f : 0.35f;
    for (int k = 0; k < 3; k++) np[k] = fp[k] + (q->pos[k] - fp[k]) * t;
    // rotation: at and up as sent (orthonormal in the owner's game), right = up x at
    f32 ax = q->at[0], ay = q->at[1], az = q->at[2], ux = q->up[0], uy = q->up[1], uz = q->up[2];
    f32 m[9] = { uy * az - uz * ay, uz * ax - ux * az, ux * ay - uy * ax, ux, uy, uz, ax, ay, az };
    int rot = (ax * ax + ay * ay + az * az) > 0.5f && (ux * ux + uy * uy + uz * uz) > 0.5f;
    for (int k = 0; k < 3; k++) {
        AT(f32, fr + FR_POS + 4 * k) = np[k];
        AT(f32, fr + FR_OLDPOS + 4 * k) = np[k];
        if (rot) {
            AT(f32, fr + 0x00 + 4 * k) = m[k];
            AT(f32, fr + 0x10 + 4 * k) = m[3 + k];
            AT(f32, fr + 0x20 + 4 * k) = m[6 + k];
        }
    }
    if (valid_ptr(mdl)) {
        u8* mat = (u8*)AT(u32, mdl + MI_MAT);
        if (valid_ptr(mat))
            for (int k = 0; k < 3; k++) {
                AT(f32, mat + RWM_POS + 4 * k) = np[k];
                if (rot) {
                    AT(f32, mat + 0x00 + 4 * k) = m[k];
                    AT(f32, mat + 0x10 + 4 * k) = m[3 + k];
                    AT(f32, mat + 0x20 + 4 * k) = m[6 + k];
                }
            }
    }
}

static void set_anim(u8* b, volatile NpcPose* q, int fresh)
{
    u8* table;
    u8* single = anim_single(b, &table);
    u32 st = q->animState;
    if (!single || !st) return;
    if (AT(u32, single + 4) != st) {
        if (state_in_table(table, st)) xAnimPlaySetState(single, (void*)st, q->animTime);
    } else if (fresh) {
        f32 d = AT(f32, single + 8) - q->animTime;
        if (d > 0.25f || d < -0.25f) AT(f32, single + 8) = q->animTime;
    }
}

static void set_goal(u32 i, u8* b, volatile NpcPose* q)
{
    u8* psy = psyche_of(b);
    s32 g = q->goal;
    if (!psy || !g) return;
    if (GIDOfActive(psy) == g || GIDOfPending(psy) == g) { lastGoal[i] = g; return; }
    // ask once per goal the owner enters (the goal's own entry code runs), not every frame
    if (lastGoal[i] == g) return;
    lastGoal[i] = g;
    GoalSet(psy, g, 0);
}

static void fill_pose(volatile NpcPose* q, u32 i, u8* b, u32 boss)
{
    u8* fr = frame_of(b);
    q->index = (u16)i;
    q->flags = (u16)(PF_VALID | boss | ((mb.npcRank & 0xFF) << 8));
    q->id = AT(u32, b);
    for (int k = 0; k < 3; k++) {
        q->pos[k] = AT(f32, fr + FR_POS + 4 * k);
        q->at[k] = AT(f32, fr + 0x20 + 4 * k);
        q->up[k] = AT(f32, fr + 0x10 + 4 * k);
    }
    u8* single = anim_single(b, 0);
    q->animState = single ? AT(u32, single + 4) : 0;
    q->animTime = single ? AT(f32, single + 8) : 0;
    u8* psy = psyche_of(b);
    q->goal = psy ? GIDOfActive(psy) : 0;
    q->hp = 0; q->round = 0; q->epoch = 0;
    if (boss) {
        u32 hpo, ro; int inv;
        hp_fields(AT(u32, b + NPC_TYPE), &hpo, &ro, &inv);
        if (hpo) { q->hp = (s16)hp_get(b, hpo, inv); q->round = ro ? (s16)AT(s32, b + ro) : 0; q->flags |= PF_HP; }
        q->epoch = epoch[i];
    }
}

void npcsync_frame(void)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    u8* lf = (u8*)AT(u32, GLOBALS + G_ENT_FRAME);
    if (!sc || !lf || !mb.npcRank || (mb.flags & 0x10000u)) return;   // 65536: diagnostics, off
    u32 sid = AT(u32, sc);
    if (sid != sceneOf) {
        sceneOf = sid;
        for (u32 i = 0; i < OBJ_MAX; i++) { own[i] = 0; epoch[i] = 0; lastHp[i] = -1; seenFrame[i] = 0; lastGoal[i] = 0; }
        mb.npcOutCount = 0;
        sceneFrames = 0;
    }
    sceneFrames++;
    // a sim's player is parked far away: it never drives anything
    const f32* me = mb.mode == MODE_SIM ? 0 : (const f32*)(lf + FR_POS);
    u16 now = (u16)mb.frames;

    // bosses: a hit our player landed on a boss we follow (its hit points went down in our game
    // since we last set them): the boss is ours now
    u32 count = AT(u32, sc + 0x74);
    u8** base = (u8**)AT(u32, sc + 0x78);
    if (!base) return;
    if (count > OBJ_MAX) count = OBJ_MAX;

    // follow the states other games sent for the NPCs they drive
    int fresh = mb.npcInSeq != inSeqSeen;
    inSeqSeen = mb.npcInSeq;
    if (mb.npcInScene == sid) {
        u32 n = mb.npcInCount > NPC_MAX ? NPC_MAX : mb.npcInCount;
        for (u32 k = 0; k < n; k++) {
            volatile NpcPose* q = &mb.npcIn[k];
            if (!(q->flags & PF_VALID) || q->index >= OBJ_MAX) continue;
            u32 i = q->index;
            u8* b = npc_at(i, q->id);
            if (!b || !alive(b)) continue;
            if (q->flags & PF_BOSS) {
                u32 hpo, ro; int inv;
                hp_fields(AT(u32, b + NPC_TYPE), &hpo, &ro, &inv);
                u32 theirRank = q->flags >> 8;
                // a newer owner (they hit it), or the same generation and a lower party id: theirs
                if (fresh && (q->epoch > epoch[i] || (q->epoch == epoch[i] && own[i] && theirRank < (mb.npcRank & 0xFF)))) {
                    epoch[i] = (u16)q->epoch;
                    own[i] = 0;
                }
                if (own[i]) continue;
                seenFrame[i] = now ? now : 1;
                if (hpo && (q->flags & PF_HP)) {
                    s32 hp = hp_get(b, hpo, inv);
                    if (lastHp[i] >= 0 && hp < lastHp[i] && q->hp >= lastHp[i]) {
                        // our player hit it since we last synced: take it over, keep our hit
                        own[i] = 1;
                        epoch[i] = (u16)(epoch[i] + 1);
                        lastHp[i] = (s16)hp;
                        continue;
                    }
                    hp_put(b, hpo, inv, q->hp);
                    if (ro) AT(s32, b + ro) = q->round;
                    lastHp[i] = q->hp;
                }
                set_goal(i, b, q);
            } else if (own[i]) continue;
            set_pose(b, q);
            set_anim(b, q, fresh);
            if (fresh) mb.npcApplied++;
        }
    }

    // decide which NPCs are ours and publish their states (every 3rd frame, ~20 Hz)
    if (++sendTick % 3) return;
    u32 out = 0;
    u32 low = lowest_rank();
    for (u32 i = 0; i < count; i++) {
        u8* b = base[i];
        if (!b || b[4] != 0x2B || !synced_kind(b)) continue;
        if (!alive(b)) { own[i] = 0; continue; }
        u32 boss = is_boss_type(AT(u32, b + NPC_TYPE)) ? PF_BOSS : 0;
        if (boss) {
            u32 hpo, ro; int inv;
            hp_fields(AT(u32, b + NPC_TYPE), &hpo, &ro, &inv);
            // nobody has driven it yet: the lowest party id in the scene; the owner left (or never
            // showed up): we take over
            if (!own[i] && me) {
                if (epoch[i] == 0 && seenFrame[i] == 0 && low == mb.npcRank) own[i] = 1;
                else if (seenFrame[i] ? (u16)(now - seenFrame[i]) > BOSS_ORPHAN : sceneFrames > 300) {
                    own[i] = 1;
                    epoch[i] = (u16)(epoch[i] + 1);
                }
            }
            if (own[i] && hpo) lastHp[i] = (s16)hp_get(b, hpo, inv);
        } else {
            own[i] = me ? (u8)decide_enemy(i, b, me) : 0;
        }
        if (!own[i] || out >= NPC_MAX || !frame_of(b)) continue;
        fill_pose(&mb.npcOut[out++], i, b, boss);
    }
    mb.npcOutCount = out;
    mb.npcOutScene = sid;
    mb.npcOutSeq = mb.npcOutSeq + 1;
}
