// BFBB Online in-game patch.
//
// Remote players are "puppets": full extra player contexts (like the bfbbmulti split-screen
// mod), each built with the game's own zGameSetupPlayer. Every frame each puppet is swapped
// into `globals`, fed the remote player's pad and camera so the game animates it naturally,
// updated, then pulled toward the remote player's real position. Puppets are invulnerable,
// silent and invisible to pickups/triggers/AI (those only ever test the live context during
// zSceneUpdate), so a puppet never changes this game's world on its own.
//
// The launcher/server talks to the patch through `mb` (see mailbox.h).
#include "game.h"
#include "mailbox.h"

__attribute__((section(".mailbox"), used)) volatile Mailbox mb = {
    .magic = BBOL_MAGIC,
    .version = BBOL_VERSION,
    .maxRemote = MAX_REMOTE,
};

#define SW_PLAYER  1u
#define SW_CAMERA  2u
#define SW_STATICS 4u
#define SW_PAD     8u
#define SW_ALL     (SW_PLAYER | SW_CAMERA | SW_STATICS | SW_PAD)

typedef struct {
    u8 player[G_PLAYER_SIZE];
    u8 camera[G_CAMERA_SIZE];
    u8 sa[PSTATIC_A_SIZE];
    u8 sb[PSTATIC_B_SIZE];
    u8 sc[PSTATIC_C_SIZE];
    u8 sd[PSTATIC_D_SIZE];
    u8 bungee[BUNGEE_SIZE];
    u8 cruise[CRUISE_SIZE];
    u8 oob[OOB_SIZE];
    u32 camvel;
    u8 pad[PAD_SIZE];
} Ctx;

typedef struct {
    u32 valid;      // built for the current scene
    u32 lastSeq;
    u32 seqFrame;   // mb.frames when lastSeq arrived
    u32 prevOn;
    u32 placed;     // has been snapped to the remote once
    u32 tilted;     // frames the puppet has been tipped over (a death or reset gone wrong)
} CtxInfo;

static Ctx ctx[MAX_REMOTE];
static CtxInfo info[MAX_REMOTE];
static Ctx tmp;  // the live (local) context while a puppet is swapped in
static u32 overridePrevOn;

static void store(Ctx* c, u32 m)
{
    if (m & SW_PLAYER)  memcpy(c->player, (void*)(GLOBALS + G_PLAYER), G_PLAYER_SIZE);
    if (m & SW_CAMERA)  memcpy(c->camera, (void*)(GLOBALS + G_CAMERA), G_CAMERA_SIZE);
    if (m & SW_STATICS) {
        memcpy(c->sa, (void*)PSTATIC_A, PSTATIC_A_SIZE);
        memcpy(c->sb, (void*)PSTATIC_B, PSTATIC_B_SIZE);
        memcpy(c->sc, (void*)PSTATIC_C, PSTATIC_C_SIZE);
        memcpy(c->sd, (void*)PSTATIC_D, PSTATIC_D_SIZE);
        memcpy(c->bungee, (void*)BUNGEE_SHARED, BUNGEE_SIZE);
        memcpy(c->cruise, (void*)CRUISE_SHARED, CRUISE_SIZE);
        memcpy(c->oob, (void*)OOB_SHARED, OOB_SIZE);
        c->camvel = AT(u32, ZCAM_PLAYERVEL);
    }
    if (m & SW_PAD)     memcpy(c->pad, (void*)MPAD, PAD_SIZE);
}

static void load(const Ctx* c, u32 m)
{
    if (m & SW_PLAYER)  memcpy((void*)(GLOBALS + G_PLAYER), c->player, G_PLAYER_SIZE);
    if (m & SW_CAMERA)  memcpy((void*)(GLOBALS + G_CAMERA), c->camera, G_CAMERA_SIZE);
    if (m & SW_STATICS) {
        memcpy((void*)PSTATIC_A, c->sa, PSTATIC_A_SIZE);
        memcpy((void*)PSTATIC_B, c->sb, PSTATIC_B_SIZE);
        memcpy((void*)PSTATIC_C, c->sc, PSTATIC_C_SIZE);
        memcpy((void*)PSTATIC_D, c->sd, PSTATIC_D_SIZE);
        memcpy((void*)BUNGEE_SHARED, c->bungee, BUNGEE_SIZE);
        memcpy((void*)CRUISE_SHARED, c->cruise, CRUISE_SIZE);
        memcpy((void*)OOB_SHARED, c->oob, OOB_SIZE);
        AT(u32, ZCAM_PLAYERVEL) = c->camvel;
    }
    if (m & SW_PAD)     memcpy((void*)MPAD, c->pad, PAD_SIZE);
}

static int bridged(void) { return mb.bridge == BBOL_BRIDGE; }

// --- small math (no libm in the game) --------------------------------------------------
static f32 wrap_pi(f32 a)
{
    const f32 PI = 3.14159265f, TAU = 6.2831853f;
    while (a > PI) a -= TAU;
    while (a < -PI) a += TAU;
    return a;
}

static f32 fsin(f32 x)
{
    x = wrap_pi(x);
    f32 x2 = x * x;
    return x * (1.0f - x2 * (1.0f / 6.0f - x2 * (1.0f / 120.0f - x2 * (1.0f / 5040.0f - x2 / 362880.0f))));
}

static f32 fcos(f32 x) { return fsin(x + 1.5707963f); }

static u8* frame_of_live(void) { return (u8*)AT(u32, GLOBALS + G_ENT_FRAME); }
static u8* model_of_live(void) { return (u8*)AT(u32, GLOBALS + G_ENT_MODEL); }

// --- inventory --------------------------------------------------------------------------
static u32 inv_addr(int i)
{
    if (i == 0) return GLOBALS + 0x1B00;           // shinies
    if (i == 1) return GLOBALS + 0x1B04;           // spatulas
    if (i == 2) return GLOBALS + 0x1BC4;           // socks total
    if (i == 33) return GLOBALS + G_MAXHEALTH;     // golden underwear
    int l = (i - 3) >> 1;
    return ((i - 3) & 1) ? GLOBALS + 0x1B84 + 4 * l : GLOBALS + 0x1B08 + 4 * l;
}

static void inventory_frame(void)
{
    if (mb.invSeq != mb.invAck) {
        for (int i = 0; i < INV_COUNTERS; i++) {
            s32 d = mb.invAdd[i];
            int absolute = (mb.invSetMask[i >> 5] >> (i & 31)) & 1;
            if (!d && !absolute) continue;
            s32 v = absolute ? d : (s32)AT(u32, inv_addr(i)) + d;
            if (absolute) d = v - (s32)AT(u32, inv_addr(i));
            if (i == 33) {
                // max health is 3..6; a teammate's golden underwear also fills ours up
                if (v < 3) v = 3;
                if (v > 6) v = 6;
                if (d > 0 && AT(u32, GLOBALS + G_HEALTH)) AT(u32, GLOBALS + G_HEALTH) = (u32)v;
            }
            AT(u32, inv_addr(i)) = v < 0 ? 0 : (u32)v;
        }
        mb.invSetMask[0] = 0; mb.invSetMask[1] = 0;
        mb.invAck = mb.invSeq;
    }
    for (int i = 0; i < INV_COUNTERS; i++) mb.invCount[i] = AT(u32, inv_addr(i));
}

// zEntPlayer_Update: bl zEntPickup_CheckAllPickupsAgainstPlayer. A teammate's puppet must never
// pick things up in this game (the teammate's own game does, and the result is synced).
int inPuppet;   // also read by stubs.S (bbol_colltrigger_stub)
void bbol_pickup_check(void* scene, f32 dt)
{
    if (inPuppet) return;
    zEntPickup_CheckAllPickupsAgainstPlayer(scene, dt);
}

// Conversations never start while a teammate's puppet is being updated: its game runs the
// conversation (and we get a watch-along prompt). Stopping one afterwards can hang the game.
void bbol_task_start_talk(void* task, void* npc)
{
    if (inPuppet) { mb.dbgRender[0]++; mb.dbgRender[1] = AT(u32, task); return; }
    ztaskbox_start_talk(task, npc);
}

void bbol_talk_start_talk(void* box, const char* text, void* cb, void* npc)
{
    if (inPuppet) { mb.dbgRender[0]++; mb.dbgRender[1] = AT(u32, box); return; }
    ztalkbox_start_talk(box, text, cb, npc);
}

void bbol_talk_start_talk_id(void* box, u32 text, void* cb, void* npc)
{
    if (inPuppet) { mb.dbgRender[0]++; mb.dbgRender[1] = AT(u32, box); return; }
    ztalkbox_start_talk_id(box, text, cb, npc);
}

// --- local state out --------------------------------------------------------------------
static void publish_local(void)
{
    volatile Local* L = &mb.local;
    u32 s = L->seq;
    L->seq = s + 1;

    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    u8* fr = frame_of_live();
    u8* pad = (u8*)MPAD;
    const f32* cam = (const f32*)(GLOBALS + G_CAMERA_MAT);

    L->frame = mb.frames;
    L->sceneId = sc ? AT(u32, sc) : 0;
    L->inGame = (sc && fr) ? 1 : 0;
    L->character = AT(u32, GCURRENTPLAYER);
    L->on = AT(u32, pad + PAD_ON);
    L->ax = AT(s8, pad + PAD_ANALOG1);
    L->ay = AT(s8, pad + PAD_ANALOG1 + 1);
    L->cx = AT(s8, pad + PAD_ANALOG2);
    L->cy = AT(s8, pad + PAD_ANALOG2 + 1);
    if (fr) {
        for (int k = 0; k < 3; k++) {
            L->pos[k] = AT(f32, fr + FR_POS + 4 * k);
            L->vel[k] = AT(f32, fr + FR_VEL + 4 * k);
        }
        L->yaw = AT(f32, fr + FR_ROTANGLE);
    }
    for (int k = 0; k < 3; k++) {
        L->cam[k] = cam[k];          // right
        L->cam[3 + k] = cam[4 + k];  // up
        L->cam[6 + k] = cam[8 + k];  // at
    }
    L->health = AT(u32, GLOBALS + G_HEALTH);
    L->shiny = AT(u32, GLOBALS + G_INV_SHINY);
    L->spatula = AT(u32, GLOBALS + G_INV_SPATULA);
    L->controlOff = AT(u32, GLOBALS + G_CONTROLOFF);

    L->seqEnd = s + 2;
    L->seq = s + 2;
}

// --- warps ------------------------------------------------------------------------------
static u32 warpAsset[6];   // xPortalAsset: id, type/links/flags, cam, marker, ang, sceneID
static u32 warpPortal[5];  // _zPortal: xBase (16 bytes) + passet

static void handle_warp(void)
{
    if (mb.warpSeq == mb.warpAck || !mb.warpScene) return;
    mb.warpAck = mb.warpSeq;
    if (!AT(u32, GLOBALS + G_SCENECUR)) return;

    union { f32 f; u32 u; } ang = { .f = -1.0e9f };  // keep the scene's own start angle
    warpAsset[0] = 0x42424F57u;  // 'BBOW'
    warpAsset[1] = 0;
    warpAsset[2] = 0;
    warpAsset[3] = 0;            // no marker: the scene's default player start
    warpAsset[4] = ang.u;
    warpAsset[5] = mb.warpScene;
    warpPortal[0] = warpAsset[0];
    warpPortal[1] = 0;
    warpPortal[2] = 0;
    warpPortal[3] = 0;
    warpPortal[4] = (u32)warpAsset;
    zSceneSwitch(warpPortal, (mb.flags & MBF_WARP_FORCE) ? 1 : 0);
}

// --- puppets ----------------------------------------------------------------------------
static void place(u8* fr, u8* mdl, const f32* p)
{
    for (int k = 0; k < 3; k++) {
        AT(f32, fr + FR_POS + 4 * k) = p[k];
        AT(f32, fr + FR_OLDPOS + 4 * k) = p[k];
    }
    if (mdl) {
        u8* m = (u8*)AT(u32, mdl + MI_MAT);
        if (m) for (int k = 0; k < 3; k++) AT(f32, m + RWM_POS + 4 * k) = p[k];
    }
}

static void face(u8* fr, u8* mdl, f32 yaw)
{
    // Same basis xMat3x3Euler(yaw, 0, 0) builds: right (c,0,-s), up (0,1,0), at (s,0,c).
    f32 s = fsin(yaw), c = fcos(yaw);
    f32 b[12] = { c, 0, -s, 0,  0, 1, 0, 0,  s, 0, c, 0 };
    AT(f32, fr + FR_ROTANGLE) = yaw;
    for (int k = 0; k < 12; k++) {
        if ((k & 3) == 3) continue;
        AT(f32, fr + 4 * k) = b[k];
    }
    if (mdl) {
        u8* m = (u8*)AT(u32, mdl + MI_MAT);
        if (m) for (int k = 0; k < 12; k++) {
            if ((k & 3) == 3) continue;
            AT(f32, m + 4 * k) = b[k];
        }
    }
}

// --- PvP (host setting): checked on the victim's side, each game hurts only its own player --
#define PVP_ATTACK  0x3Cu   // zEntPlayer_MoveInfo: 4 melee, 8 bash, 0x10 bounce/slam, 0x20 spin/wand
typedef struct { u32 attack; u32 boom; f32 pos[3]; f32 boomAt[3]; } PvpSample;
static PvpSample pvp[MAX_REMOTE];
static u32 pvpLatch[MAX_REMOTE];   // this puppet's current attack already hit us (one hit per swing)
static int ballHit;                // the rolling ball already hit us
static u32 ballThrown;    // mb.frames when a puppet threw it
static int ballOwner;     // 1 + slot of the puppet that last threw the scene's bowling ball, 0 = not a puppet's
static f32 ballLast[3];

static u8* ball_ent(void) { return (u8*)AT(u32, GLOBALS + G_BUBBLEBOWL); }
static int ball_pos(f32* out)
{
    u8* e = ball_ent();
    if (!e) return 0;
    u8* mdl = (u8*)AT(u32, e + 0x24);
    u8* m = mdl ? (u8*)AT(u32, mdl + MI_MAT) : 0;
    if (!m) return 0;
    for (int k = 0; k < 3; k++) out[k] = AT(f32, m + 0x30 + 4 * k);
    return (AT(u8, e + 0x18) & 1) != 0;   // visible = rolling
}
static f32 dist2(const f32* a, const f32* b) { f32 d = 0; for (int k = 0; k < 3; k++) d += (a[k] - b[k]) * (a[k] - b[k]); return d; }

static int name_is(const char* a, const char* b) { while (*a && *a == *b) { a++; b++; } return *a == *b; }
static int name_starts(const char* a, const char* p) { while (*p && *a == *p) { a++; p++; } return *p == 0; }
static int pvp_attack_name(const char* n)
{
    return name_is(n, "Melee01") || name_starts(n, "Karate") || name_starts(n, "Chop") || name_starts(n, "Smash")
        || name_is(n, "LassoThrow") || name_is(n, "StunLand");   // Patrick's belly flop (X in the air) landing
}

// inside puppet i's context, right after its update
static void pvp_sample(int i, u8* fr)
{
    PvpSample* s = &pvp[i];
    s->attack = zEntPlayer_MoveInfo() & PVP_ATTACK;
    // Patrick's/Sandy's attacks aren't in MoveInfo (their UserFlags are the same as a jump's): by name
    u8* mdl = (u8*)AT(u32, GLOBALS + G_ENT_MODEL);
    u8* an = mdl ? (u8*)AT(u32, mdl + MI_ANIM) : 0;
    u8* sg = an ? (u8*)AT(u32, an + 8) : 0;
    u8* st = sg ? (u8*)AT(u32, sg + 4) : 0;
    const char* nm = st ? (const char*)AT(u32, st + 4) : 0;
    if (nm && AT(u32, GCURRENTPLAYER) != 0 && pvp_attack_name(nm)) s->attack |= 4;
    for (int k = 0; k < 3; k++) s->pos[k] = AT(f32, fr + FR_POS + 4 * k);
    s->boom = (AT(u32, CRUISE_SHARED) & 0x40u) != 0;   // cruise_bubble shared.flags: missile exploding
    for (int k = 0; k < 3; k++) s->boomAt[k] = AT(f32, CRUISE_SHARED + 0x50 + 4 * k);   // shared.hit_loc
    // the ball jumped to this puppet's hand: this puppet bowled it
    f32 b[3];
    ball_pos(b);
    if (ball_ent() && dist2(b, ballLast) > 4.0f && dist2(b, s->pos) < 9.0f) { ballOwner = i + 1; ballThrown = mb.frames; ballHit = 0; }
    for (int k = 0; k < 3; k++) ballLast[k] = b[k];
}

// back in our own context, after every puppet ran
static void pvp_apply(void)
{
    f32 b[3];
    int rolling = ball_pos(b);
    // a puppet's ball: while it rolls, or just after it was thrown (hitting us kills it at once)
    int live = ballOwner && (rolling || mb.frames - ballThrown < 20);
    if (!live) ballOwner = 0;
    else if (dist2(b, ballLast) > 9.0f) ballOwner = 0;   // jumped again outside a puppet's update: we bowled
    for (int k = 0; k < 3; k++) ballLast[k] = b[k];
    for (int i = 0; i < MAX_REMOTE; i++) if (!pvp[i].attack && !pvp[i].boom) pvpLatch[i] = 0;
    mb.dbgRender[16] = (u32)ballOwner | ((u32)rolling << 8) | ((u32)ballHit << 9);
    if (!(mb.flags & MBF_PVP) || inPuppet) return;
    u8* fr = frame_of_live();
    if (!fr || AT(u32, GLOBALS + G_CONTROLOFF)) return;
    f32 me[3] = { AT(f32, fr + FR_POS), AT(f32, fr + FR_POS + 4) + 0.5f, AT(f32, fr + FR_POS + 8) };
    for (int i = 0; i < MAX_REMOTE; i++) {
        if (!info[i].valid || !mb.remote[i].active) continue;
        PvpSample* s = &pvp[i];
        f32 c[3] = { s->pos[0], s->pos[1] + 0.5f, s->pos[2] };
        if (pvpLatch[i]) continue;
        if (s->attack && dist2(c, me) < 1.6f * 1.6f) { pvpLatch[i] = 1; mb.dbgRender[17]++; zEntPlayer_DamageNPCKnockBack((void*)(GLOBALS + G_PLAYER), 1, s->pos); return; }
        if (s->boom && dist2(s->boomAt, me) < 2.0f * 2.0f) { pvpLatch[i] = 1; mb.dbgRender[17]++; zEntPlayer_DamageNPCKnockBack((void*)(GLOBALS + G_PLAYER), 1, s->boomAt); return; }
    }
    if (ballOwner && !ballHit && dist2(b, me) < 2.5f * 2.5f) { ballHit = 1; mb.dbgRender[17]++; zEntPlayer_DamageNPCKnockBack((void*)(GLOBALS + G_PLAYER), 1, b); }
}

static void puppet_update(int i, void* scene, f32 dt)
{
    volatile Remote* r = &mb.remote[i];
    CtxInfo* in = &info[i];
    fn_update up = AT(fn_update, GLOBALS + G_ENT_UPDATE);

    if (r->seq != in->lastSeq) {
        in->lastSeq = r->seq;
        in->seqFrame = mb.frames;
    }

    store(&tmp, SW_ALL);
    load(&ctx[i], SW_ALL);
    if (mb.flags & 32u) { store(&ctx[i], SW_ALL); load(&tmp, SW_ALL); return; }   // diagnostics: swap only

    // abilities (bubble bowl, cruise bubble): a puppet is built with the starting set only; the
    // unlocks are party progress, so it has what our player has
    AT(u8, GLOBALS + G_POWERUP) = tmp.player[G_POWERUP - G_PLAYER];
    AT(u8, GLOBALS + G_POWERUP + 1) = tmp.player[G_POWERUP + 1 - G_PLAYER];
    // character: gCurrentPlayer is per context (PSTATIC_A); the game swaps model/moves on its own
    // when it changes (as boss fights do), if this scene has that character's model
    if (r->character < 3 && AT(u32, GLOBALS + G_MODEL_SB + 4 * r->character))
        AT(u32, GCURRENTPLAYER) = r->character;

    // pad: the remote's buttons/sticks this frame; edges derived here so none are lost
    u8* pad = (u8*)MPAD;
    int noInput = (r->flags & RF_NOINPUT) != 0;
    u32 on = noInput ? 0 : r->on;
    AT(u32, pad + PAD_PRESSED) = on & ~in->prevOn;
    AT(u32, pad + PAD_RELEASED) = ~on & in->prevOn;
    AT(u32, pad + PAD_ON) = on;
    in->prevOn = on;
    AT(s8, pad + PAD_ANALOG1) = noInput ? 0 : r->ax;
    AT(s8, pad + PAD_ANALOG1 + 1) = noInput ? 0 : r->ay;
    AT(s8, pad + PAD_ANALOG2) = noInput ? 0 : r->cx;
    AT(s8, pad + PAD_ANALOG2 + 1) = noInput ? 0 : r->cy;
    xPadNormalizeAnalog(pad, AT(u16, GLOBALS + G_ANALOGMIN), AT(u16, GLOBALS + G_ANALOGMAX));

    // camera basis: stick directions are camera relative
    f32* cam = (f32*)(GLOBALS + G_CAMERA_MAT);
    for (int k = 0; k < 3; k++) {
        cam[k] = r->cam[k];
        cam[4 + k] = r->cam[3 + k];
        cam[8 + k] = r->cam[6 + k];
    }

    // never hurt, never die, never make sound in this game
    AT(f32, GLOBALS + G_DAMAGETIMER) = 1.0f;
    AT(u32, GLOBALS + G_HEALTH) = r->health ? r->health : 3;
    AT(u32, SPLAYERIGNORESND) = 1;
    AT(u32, GLOBALS + G_VISIBLE) = (r->flags & RF_HIDDEN) ? 0 : 1;

    u8* fr = frame_of_live();
    u8* mdl = model_of_live();
    int cheap = (r->flags & RF_CHEAP) != 0 || (mb.flags & MBF_ALL_CHEAP);

    if (!in->placed) {
        f32 p[3] = { r->pos[0], r->pos[1], r->pos[2] };
        place(fr, mdl, p);
        face(fr, mdl, r->yaw);
        in->placed = 1;
    }

    inPuppet = 1;
    if (!cheap) up((void*)(GLOBALS + G_PLAYER), scene, dt);
    inPuppet = 0;
    if (fr) pvp_sample(i, fr);

    // pull toward where the remote player is now (last sample + velocity, capped)
    f32 age = (f32)(mb.frames - in->seqFrame) * (1.0f / 60.0f);
    if (age > 0.25f) age = 0.25f;
    f32 tgt[3], cur[3], err[3], d2 = 0;
    for (int k = 0; k < 3; k++) {
        tgt[k] = r->pos[k] + r->vel[k] * age;
        cur[k] = AT(f32, fr + FR_POS + 4 * k);
        err[k] = tgt[k] - cur[k];
        d2 += err[k] * err[k];
    }
    if (cheap || d2 > 6.0f * 6.0f) {
        place(fr, mdl, tgt);
    } else {
        for (int k = 0; k < 3; k++) cur[k] += err[k] * 0.25f;
        place(fr, mdl, cur);
    }
    if (cheap) face(fr, mdl, r->yaw);

    // the player never tips over in this game: a puppet tilted or upside down for a second is
    // stuck (a death sequence that never finished here): reset it, the next frame puts it back
    // upright where the teammate is
    if (fr && AT(f32, fr + 0x14) < 0.5f) {
        if (++in->tilted > 60) {
            zEntPlayerReset((void*)(GLOBALS + G_PLAYER));
            AT(u8, GLOBALS + G_PLAYER + 0x18) |= 1;
            in->placed = 0;
            in->tilted = 0;
        }
    } else in->tilted = 0;

    store(&ctx[i], SW_ALL);
    load(&tmp, SW_ALL);
}

// --- hooks ------------------------------------------------------------------------------

// --- world import (scene-load handshake) -------------------------------------------------
static void service_ensure(void)
{
    if (mb.ensureSeq == mb.ensureAck) return;
    u32 n = mb.ensureCount;
    if (n > 32) n = 32;
    for (u32 k = 0; k < n; k++)
        if (mb.ensureTags[k]) XSER_get_client(mb.ensureTags[k]);
    mb.ensureAck = mb.ensureSeq;
}

// Runs right before zGameSetup calls zSceneLoad: give the bridge a chance to put the party's
// world (PLYR, CNTR and this scene's slot) into the serializer so the scene loads with it.
static void load_handshake(void)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    mb.loadScene = sc ? AT(u32, sc) : 0;
    mb.loadSeq = mb.loadSeq + 1;
    u32 n = mb.loadWait, i;
    for (i = 0; i < n && mb.loadAck != mb.loadSeq; i++) {
        service_ensure();
        VIWaitForRetrace();
    }
    service_ensure();
    mb.loadWaited = i;
}

// Free bytes between the scene heap and the top heap.
static u32 heap_gap(void)
{
    u32 lo = AT(u32, GXHEAP + 0x10 + 16 * AT(u16, GXHEAP + 0xD0));
    u32 hi = AT(u32, GXHEAP + 0xDC + 0x10 + 16 * AT(u16, GXHEAP + 0xDC + 0xD0));
    return hi > lo ? hi - lo : 0;
}

// zGameExit (and the menu): bl zSceneExit. RenderWare only syncs its dirty-frame list at the next
// camera update; a frame still on it when the scene's memory goes away made the next scene's first
// camera update jump to NULL (BB04 -> BB05 after a long session, with or without puppets).
// diagnostics: RenderWare's dirty-frame list head at each stage of a scene change (dbgRender[10..15])
#define RW_ENGINE 0x803CC87Cu
static u32 rw_dirty_head(void) { u32 e = AT(u32, RW_ENGINE); return e ? AT(u32, e + 0xBC) : 1; }


// --- RenderWare resource arena check (diagnostics) ----------------------------------------
// The arena (instanced geometry) is a block heap: {heap, next, prev, size, used} 32-byte headers,
// blocks back to back (_rwResHeapInit/_rwResHeapAlloc). Something rarely overwrites a header and
// the game crashes much later in _rwResHeapAlloc. Walked at each of our steps of a frame: the first
// broken walk names the step (or the stretch of the game's own code since the previous check).
// mb.dbgRender[32] = stage << 24 | frame, [33] bad block, [34..38] its header, [39] = previous
// stage << 24 | blocks walked. Stops after the first hit.
#define RW_RESMOD 0x803CC848u
static u32 arenaPrevStage, arenaHit;
// InterruptHandlerTable, HeapArray, resourcesModule (offset), RwEngineInstance, the arena heap
#define SENTINELS 5
static u32 sentinelAddr[SENTINELS] = { 0x803CC570u, 0x803CC548u, 0x803CC848u, 0x803CC87Cu, 0 };
static u32 sentinelVal[SENTINELS];
static int in_ram(u32 a) { return a >= 0x80000000u && a < 0x82000000u; }
void arena_check(u32 stage)
{
    if (arenaHit || (mb.flags & 0x200000u)) return;   // 0x200000: diagnostics, no arena check
    // every step of the frame only with 0x400000 (diagnostics); normally once a frame and around
    // scene changes, which is cheap and still logs that it happened
    if (!(mb.flags & 0x400000u) && stage != 1 && stage < 21) return;
    u32 e = AT(u32, RW_ENGINE);
    if (!in_ram(e)) return;
    sentinelAddr[4] = e + AT(u32, RW_RESMOD) + 0xC;
    u32 heap = AT(u32, e + AT(u32, RW_RESMOD) + 0xC);
    if (!heap) return;
    // globals that never change once the game runs (the first crash had the OS interrupt handler
    // table pointer overwritten with a float): changed -> block = the global's address,
    // header = old value, new value
    for (int k = 0; k < SENTINELS; k++) {
        u32 v = AT(u32, sentinelAddr[k]);
        if (!sentinelVal[k]) { sentinelVal[k] = v; continue; }
        if (v != sentinelVal[k]) {
            arenaHit = 1;
            mb.dbgRender[32] = (stage << 24) | (mb.frames & 0xFFFFFFu);
            mb.dbgRender[33] = sentinelAddr[k];
            mb.dbgRender[34] = sentinelVal[k]; mb.dbgRender[35] = v;
            mb.dbgRender[36] = 0x53454E54u; mb.dbgRender[37] = 0; mb.dbgRender[38] = 0;   // 'SENT'
            mb.dbgRender[39] = arenaPrevStage << 24;
            return;
        }
    }
    // the main camera's world (RenderWare reads it as the current world while drawing; a garbage one
    // crashed in _rwGCLightsGlobalEnable on the first frame of SM05): 'CWLD', camera, world
    {
        u32 cam = AT(u32, GLOBALS + G_CAMERA + G_CAMERA_LOCAM);
        if (in_ram(cam)) {
            u32 w = AT(u32, cam + AT(u32, 0x803CC7E8u) + 0xC);
            u32 cw = AT(u32, e + 4), cc = AT(u32, e);
            if (cw && !in_ram(cw)) w = cw;
            if (cc && !in_ram(cc)) w = cc;
            if (w && !in_ram(w)) {
                arenaHit = 1;
                mb.dbgRender[32] = (stage << 24) | (mb.frames & 0xFFFFFFu);
                mb.dbgRender[33] = cam;
                mb.dbgRender[34] = w; mb.dbgRender[35] = AT(u32, e + 4);
                mb.dbgRender[36] = 0x43574C44u; mb.dbgRender[37] = AT(u32, cam + 4); mb.dbgRender[38] = AT(u32, 0x803CC7E8u);
                mb.dbgRender[39] = arenaPrevStage << 24;
                return;
            }
        }
    }
    u32 bad = 0, b = 0, prev = 0, n = 0;
    if (!in_ram(heap)) bad = heap;
    else {
        u32 ff = AT(u32, heap + 4);
        if (ff && (!in_ram(ff) || (ff & 31))) bad = heap;
        b = AT(u32, heap);
        while (!bad && b) {
            if (!in_ram(b) || (b & 31)) { bad = b ? b : 1; break; }
            u32 nx = AT(u32, b + 4), sz = AT(u32, b + 0xC);
            if (AT(u32, b) != heap || AT(u32, b + 8) != prev || AT(u32, b + 0x10) > 1 || (sz & 31)
                || (nx && nx != b + 0x20 + sz) || ++n > 200000) { bad = b; break; }
            prev = b;
            b = nx;
        }
    }
    if (bad) {
        arenaHit = 1;
        mb.dbgRender[32] = (stage << 24) | (mb.frames & 0xFFFFFFu);
        mb.dbgRender[33] = bad;
        for (int k = 0; k < 5; k++) mb.dbgRender[34 + k] = in_ram(bad) ? AT(u32, (bad & ~3u) + 4 * k) : 0;
        mb.dbgRender[39] = (arenaPrevStage << 24) | (prev & 0xFFFFFFu);
    }
    arenaPrevStage = stage;
}

void bbol_scene_exit(s32 beginReload)
{
    arena_check(21);
    mb.dbgRender[10] = rw_dirty_head();
    if (!(mb.flags & 0x100000u)) rwFrameSyncDirty();   // 0x100000: diagnostics, no sync
    zSceneExit(beginReload);
    arena_check(22);
    mb.dbgRender[11] = rw_dirty_head();
}

// zGameSetup: bl zGameSetupPlayer
void bbol_setup_player(void)
{
    // The last scene's puppets are still intact in high RAM, and some of their RenderWare frames may
    // still be on RW's dirty list. Rebuilding zeroes that memory and breaks the list (crash in
    // _rwFrameSyncDirty at the next camera update): sync it while they're still valid.
    mb.dbgRender[12] = rw_dirty_head();
    if (mb.highRam && mb.ctxReady) rwFrameSyncDirty();
    zGameSetupPlayer();
    mb.dbgRender[13] = rw_dirty_head();
    mb.heapGap = heap_gap();   // what the scene itself leaves free
    for (int i = 0; i < MAX_REMOTE; i++) {
        info[i].valid = 0;
        info[i].placed = 0;
        info[i].prevOn = 0;
    }
    mb.ctxReady = 0;
    if (!bridged()) return;
    if (mb.flags & MBF_NO_PUPPETS) { load_handshake(); return; }

    // The model pools only hold enough instances for one player; zSceneInit builds scene
    // objects with static (scene heap) allocation, so do the same for the puppets.
    u32 oldStatic = AT(u32, XMODELINSTSTATICALLOC);
    AT(u32, XMODELINSTSTATICALLOC) = 1;
    store(&tmp, SW_ALL);
    // The scene heap (0, grows up) and the top heap (1, grows down) share one 3.5 MB block and
    // xMemAlloc never checks that they meet; big levels leave < 80 KB between them (JF02, RB01),
    // and the OS heap has no room either. With Dolphin's 32 MB MEM1 (the launcher asks for it)
    // nothing uses RAM above 24 MB: build the puppets there by pointing the scene heap's cursor
    // at it for the duration. Without it, build only as many as leave a safe reserve.
    u8* h0 = (u8*)GXHEAP;
    u8* sp = h0 + 0x10 + 16 * AT(u16, h0 + 0xD0);
    int high = mb.highRamMapped != 0;
    u32 saved[4], savedLast = AT(u32, h0 + 0xD8);
    memcpy(saved, sp, 16);
    // xHeapState {curr, blk_ct, used, wasted}: xMemAlloc refuses (NULL) once used + size passes the
    // heap's size, and the puppets don't use the scene's block: count from 0 while building them
    // (both restored after). Big scenes (the slide) left too little and player setup crashed.
    if (high) { AT(u32, sp) = EXTRA_RAM; AT(u32, sp + 8) = 0; }
    u32 reserve = mb.heapReserve ? mb.heapReserve : HEAP_RESERVE_DEFAULT;
    // until the first one is measured, assume a puppet costs PUPPET_COST_GUESS: the first build
    // must not run a tight scene (JF02) out of memory (NULL allocations crashed xEntInitForType)
    u32 cost = PUPPET_COST_GUESS;
    int measured = 0;
    int nctx = (mb.flags & 512u) ? 1 : MAX_REMOTE;   // 512: diagnostics, one context
    mb.heapFree[0] = heap_gap();
    for (int i = 0; i < nctx; i++) {
        u32 gap = heap_gap();
        if (!high && gap < reserve + cost + (cost >> 2)) break;
        zGameSetupPlayer();
        AT(u32, SPLAYERIGNORESND) = 1;
        store(&ctx[i], SW_ALL);
        // the bowling ball is a scene object found at scene init, not by player setup: without the
        // pointer a puppet's bowl winds up and never throws (xEntBoulder_BubbleBowl returns early)
        AT(u32, (u32)ctx[i].player + (G_BUBBLEBOWL - G_PLAYER)) = AT(u32, (u32)tmp.player + (G_BUBBLEBOWL - G_PLAYER));
        load(&tmp, SW_CAMERA | SW_STATICS | SW_PAD);
        info[i].valid = 1;
        mb.ctxReady = i + 1;
        u32 after = high ? AT(u32, sp) - EXTRA_RAM : heap_gap();
        mb.heapFree[i + 1] = after;
        if (!high && (!measured || gap - after > cost)) { cost = gap - after; measured = 1; }
    }
    if (high) { memcpy(sp, saved, 16); AT(u32, h0 + 0xD8) = savedLast; }
    mb.highRam = high;
    load(&tmp, SW_ALL);
    AT(u32, XMODELINSTSTATICALLOC) = oldStatic;
    arena_check(23);
    load_handshake();
}

// The sim's own player: parked far above the map, never updated, so it can't collect,
// trigger or be chased by anything. Party members are puppets.
static void park_local(void)
{
    u8* fr = frame_of_live();
    if (!fr) return;
    f32 p[3] = { 0.0f, 50000.0f, 0.0f };
    place(fr, model_of_live(), p);
    AT(u32, GLOBALS + G_VISIBLE) = 0;
    AT(f32, GLOBALS + G_DAMAGETIMER) = 1.0f;
}

// zGameLoop: bctrl -> globals.player.ent.update(ent, scene, dt)
void bbol_player_update(void* ent, void* scene, f32 dt)
{
    fn_update up = AT(fn_update, GLOBALS + G_ENT_UPDATE);
    int sim = bridged() && mb.mode == MODE_SIM;
    arena_check(1);
    if (sim) park_local();
    else up(ent, scene, dt);
    arena_check(2);
    mb.frames++;
    mb.dbgRender[14] = rw_dirty_head();
    if (bridged()) inventory_frame();
    publish_local();
    if (!bridged()) return;
    service_ensure();
    mb.dbgHashes = objsync_hashes();
    mb.dbgCtx = (u32)ctx;
    mb.dbgCtxSize = sizeof(Ctx);
    if (mb.tpSeq != mb.tpAck) {
        mb.tpAck = mb.tpSeq;
        f32 p[3] = { mb.tpPos[0], mb.tpPos[1], mb.tpPos[2] };
        if (frame_of_live()) place(frame_of_live(), model_of_live(), p);
    }
    if (!(mb.flags & 1024u)) objsync_frame();   // 1024: diagnostics, no object sync
    arena_check(3);
    npcsync_frame();
    arena_check(4);
    handle_warp();
    arena_check(5);
    int updated = 0;
    for (int i = 0; i < MAX_REMOTE; i++) {
        if (info[i].valid && mb.remote[i].active) {
            puppet_update(i, scene, dt);
            arena_check(6 + i);
            updated = 1;
        } else
            info[i].placed = 0;
    }
    pvp_apply();
    // a puppet's update moves the render camera (RenderWare's, shared) to its own view: put it back,
    // or our own player is culled against the wrong frustum and only his shadow shows
    if (updated && !(mb.flags & 128u)) {
        void* rw = (void*)AT(u32, GLOBALS + G_CAMERA + G_CAMERA_LOCAM);
        if (rw) iCameraUpdatePos(rw, (void*)(GLOBALS + G_CAMERA_MAT));
    }
    // Puppet updates put the puppets' RenderWare frames on RW's dirty list, which is only synced at
    // the next camera update. If the scene switches first, their memory is gone (the game never
    // destroys puppets) and that sync jumped to a NULL callback. Sync now, while they're valid.
    if (updated) rwFrameSyncDirty();
    arena_check(11);
}

// --- name tags ---------------------------------------------------------------------------
// A teammate's name over their head, drawn with the game's own font (xfont id 1, the size the
// memory-card messages use). Projected with the camera's matrix and field of view.
typedef struct { u32 id; f32 width, height, space; u8 r, g, b, a; f32 cx, cy, cw, ch; } XFont;
#define CAM_FOV 0x138u   // xCamera::fov (degrees)

static void name_tag(const f32* head, volatile Remote* r)
{
    const f32* cm = (const f32*)(GLOBALS + G_CAMERA_MAT);
    f32 d[3] = { head[0] - cm[12], head[1] - cm[13], head[2] - cm[14] };
    f32 z = d[0] * cm[8] + d[1] * cm[9] + d[2] * cm[10];
    if (z < 1.0f || z > 80.0f) return;
    f32 x = d[0] * cm[0] + d[1] * cm[1] + d[2] * cm[2];
    f32 y = d[0] * cm[4] + d[1] * cm[5] + d[2] * cm[6];
    f32 fov = AT(f32, GLOBALS + G_CAMERA + CAM_FOV);
    if (!(fov > 10.0f && fov < 170.0f)) fov = 75.0f;
    f32 half = fov * (3.14159265f / 360.0f);
    f32 t = fsin(half) / fcos(half);          // tan(fov / 2): horizontal; the screen is 4:3
    f32 sx = 0.5f - 0.5f * x / (z * t);       // the camera's "right" row points to screen left
    f32 sy = 0.5f - 0.5f * y / (z * t * 0.75f);
    if (sx < -0.1f || sx > 1.1f || sy < -0.1f || sy > 1.1f) return;
    char name[17];
    int n = 0;
    for (; n < 16 && r->name[n]; n++) name[n] = r->name[n];
    name[n] = 0;
    if (!n) return;
    // closer = bigger, within limits
    f32 scale = 18.0f / z;
    if (scale > 1.0f) scale = 1.0f;
    if (scale < 0.5f) scale = 0.5f;
    XFont f;
    f.id = 1;
    f.width = scale * 16.0f / 640.0f;
    f.height = scale * 19.0f / 480.0f;
    f.space = 0;
    u32 col = r->color;
    f.r = (u8)(col >> 24); f.g = (u8)(col >> 16); f.b = (u8)(col >> 8); f.a = 255;
    f.cx = 0; f.cy = 0; f.cw = 1; f.ch = 1;
    xfont_start_render(&f);
    xfont_irender(&f, name, sx - n * f.width * 0.35f, sy - f.height);
    xfont_stop_render(&f);
}

// the watch-along question: one small line at the bottom left, not a sign's text box (those
// covered half the screen in some levels)
const char* talk_prompt_text(void);
static void talk_prompt_render(void)
{
    const char* t = talk_prompt_text();
    if (!t || !t[0]) return;
    XFont f;
    f.id = 1;
    f.width = 0.55f * 16.0f / 640.0f;
    f.height = 0.55f * 19.0f / 480.0f;
    f.space = 0;
    f.r = 255; f.g = 240; f.b = 120; f.a = 230;
    f.cx = 0; f.cy = 0; f.cw = 1; f.ch = 1;
    xfont_start_render(&f);
    xfont_irender(&f, t, 0.06f, 0.86f);
    xfont_stop_render(&f);
}

// zSceneRenderPreFX: bctrl -> globals.player.ent.render(ent)
void bbol_player_render(void* ent)
{
    fn_render rf = AT(fn_render, GLOBALS + G_ENT_RENDER);
    if (mb.flags & 64u) xModelEval(model_of_live());   // diagnostics
    arena_check(12);
    rf(ent);
    arena_check(13);
    if (!bridged()) return;
    // a cinematic's cameras are staged for one SpongeBob: show only ours
    if (AT(u32, GLOBALS + G_CMGR)) return;
    if (mb.flags & MBF_NO_PUPPET_RENDER) return;
    const f32* campos = (const f32*)(GLOBALS + G_CAMERA_MAT + 0x30);
    for (int i = 0; i < MAX_REMOTE; i++) {
        if (!info[i].valid || !info[i].placed || !mb.remote[i].active) continue;
        if (mb.remote[i].flags & RF_HIDDEN) continue;
        // don't let a teammate fill the screen (dialog close-ups, tight corners)
        {
            u8* fr = (u8*)AT(u32, (u32)ctx[i].player + (G_ENT_FRAME - G_PLAYER));
            if (fr) {
                f32 d2 = 0;
                for (int k = 0; k < 3; k++) {
                    f32 dk = AT(f32, fr + FR_POS + 4 * k) - campos[k];
                    d2 += dk * dk;
                }
                if (d2 < 2.2f * 2.2f) continue;
            }
        }
        store(&tmp, SW_PLAYER | SW_STATICS);
        load(&ctx[i], SW_PLAYER | SW_STATICS);
        // a death (the exploding one) or a scene reset can leave the puppet's entity hidden
        // (xEntHide): a teammate is always drawn unless their own game hides them (RF_HIDDEN)
        AT(u8, GLOBALS + G_PLAYER + 0x18) |= 1;
        rf((void*)(GLOBALS + G_PLAYER));
        load(&tmp, SW_PLAYER | SW_STATICS);
        rwFrameSyncDirty();   // see bbol_player_update
        arena_check(14 + i);
    }
    arena_check(19);
}

// Text over the finished scene (inside the camera's update, after zSceneRender): the watch-along
// question and teammates' names. Drawn mid-scene they were hidden by scenery drawn later.
static void draw_overlays(void)
{
    if (!bridged() || mb.mode == MODE_SIM || AT(u32, GLOBALS + G_CMGR)) return;
    talk_prompt_render();
    if (mb.flags & (MBF_NO_NAMETAGS | MBF_NO_PUPPET_RENDER)) return;
    for (int i = 0; i < MAX_REMOTE; i++) {
        if (!info[i].valid || !info[i].placed || !mb.remote[i].active || (mb.remote[i].flags & RF_HIDDEN)) continue;
        u8* fr = (u8*)AT(u32, (u32)ctx[i].player + (G_ENT_FRAME - G_PLAYER));
        if (!fr) continue;
        f32 head[3] = { AT(f32, fr + FR_POS), AT(f32, fr + FR_POS + 4) + 1.3f, AT(f32, fr + FR_POS + 8) };
        name_tag(head, &mb.remote[i]);
    }
}

// zSceneReset: bl zEntPlayerReset(&globals.player.ent)
void bbol_player_reset(void* ent)
{
    zEntPlayerReset(ent);
    // zSceneReset saves every object, resets them and loads that save back: the world stays as it
    // was, apart from what its reset events change (counters, puzzles...). Those are reported to the
    // party like any other change (objsync keeps its fingerprints), so nobody's world drifts apart;
    // enemies the party killed stay dead (objsync kills them again).
    objsync_scene_reset();
    mb.resetSeq = mb.resetSeq + 1;
    if (!bridged()) return;
    for (int i = 0; i < MAX_REMOTE; i++) {
        if (!info[i].valid) continue;
        store(&tmp, SW_ALL);
        load(&ctx[i], SW_ALL);
        zEntPlayerReset((void*)(GLOBALS + G_PLAYER));
        store(&ctx[i], SW_ALL);
        load(&tmp, SW_ALL);
        info[i].placed = 0;
    }
}

// Control taken away with nothing on screen to give it back (seen after a bungee spatula, with
// teammates' puppets in the scene): after 15 s with no cinematic or conversation running, give
// it back. mb.dbgRender[30] = the owners that were released, [31] = how many times.
static u32 heldFrames;
static void control_watchdog(void)
{
    u32 off = AT(u32, GLOBALS + G_CONTROLOFF);
    if (!off || AT(u32, GLOBALS + G_CMGR) || AT(u32, TALK_SHARED + 8) || mb.mode == MODE_SIM) { heldFrames = 0; return; }
    if (++heldFrames < 15 * 60) return;
    heldFrames = 0;
    mb.dbgRender[30] = off;
    mb.dbgRender[31]++;
    zEntPlayerControlOn(off);
}

// zPlatform_Update entry. A teeter-totter only tilts under globals.player.drv (our own player);
// one a teammate's puppet stands on (and we don't) is updated with that puppet's drive state.
#define G_DRV          0x1BE8u    // player.drv (xEntDrive)
#define DRV_OS         0x0Cu
#define DRV_S          0x18u
#define DRV_ODRIVER    0x1Cu
#define DRV_DRIVER     0x20u
#define PLAT_TEETER    11
void zPlatform_Update_orig(void* plat, void* sc, f32 dt);
void bbol_platform_update(u8* plat, void* sc, f32 dt)
{
    u8* drv = (u8*)(GLOBALS + G_DRV);
    if (!bridged() || plat[0x1A] != PLAT_TEETER || inPuppet
        || AT(u32, drv + DRV_DRIVER) == (u32)plat || AT(u32, drv + DRV_ODRIVER) == (u32)plat) {
        zPlatform_Update_orig(plat, sc, dt);
        return;
    }
    for (int i = 0; i < MAX_REMOTE; i++) {
        if (!info[i].valid || !info[i].placed || !mb.remote[i].active) continue;
        u8* pd = ctx[i].player + (G_DRV - G_PLAYER);
        if (AT(u32, pd + DRV_DRIVER) != (u32)plat && AT(u32, pd + DRV_ODRIVER) != (u32)plat) continue;
        u32 keep[5] = { AT(u32, drv + DRV_OS), AT(u32, drv + DRV_S), AT(u32, drv + DRV_ODRIVER),
                        AT(u32, drv + DRV_DRIVER), AT(u32, GLOBALS + G_ENT_FRAME) };
        AT(u32, drv + DRV_OS) = AT(u32, pd + DRV_OS);
        AT(u32, drv + DRV_S) = AT(u32, pd + DRV_S);
        AT(u32, drv + DRV_ODRIVER) = AT(u32, pd + DRV_ODRIVER);
        AT(u32, drv + DRV_DRIVER) = AT(u32, pd + DRV_DRIVER);
        AT(u32, GLOBALS + G_ENT_FRAME) = AT(u32, ctx[i].player + (G_ENT_FRAME - G_PLAYER));
        zPlatform_Update_orig(plat, sc, dt);
        AT(u32, drv + DRV_OS) = keep[0];
        AT(u32, drv + DRV_S) = keep[1];
        AT(u32, drv + DRV_ODRIVER) = keep[2];
        AT(u32, drv + DRV_DRIVER) = keep[3];
        AT(u32, GLOBALS + G_ENT_FRAME) = keep[4];
        return;
    }
    zPlatform_Update_orig(plat, sc, dt);
}

// zGameLoop / zMenuLoop: bl xPadUpdate
s32 bbol_pad_update(s32 idx, f32 dt)
{
    s32 r = xPadUpdate(idx, dt);
    if (idx == 0) mb.dbgRender[5]++;   // diagnostics: game loop frames (also while paused)
    // pause menu (gGameMode 7): the player doesn't update, but cheats change counters here; publish
    // them so the launcher reports them before the player leaves through the menu (map, quit)
    if (idx == 0 && bridged() && AT(u32, GGAMEMODE) == 7)
        for (int i = 0; i < INV_COUNTERS; i++) mb.invCount[i] = AT(u32, inv_addr(i));
    if (bridged() && (mb.flags & MBF_WARP_ANYTIME) && AT(u32, GGAMEMODE) == 12) handle_warp();
    if (bridged() && idx == 0 && mb.evtSeq != mb.evtAck && AT(u32, GLOBALS + G_SCENECUR)) {
        mb.evtAck = mb.evtSeq;
        if (mb.evtId == 0) {
            // id 0: a game state event, as a dispatcher would run it (159 SetGameState_Play = the
            // pause menu's resume; only those, so a launcher can't switch to anything else)
            if (mb.evtCode == 159 && AT(u32, GGAMEMODE) == 7) zGameStateSwitchEvent(mb.evtCode);
        } else {
            void* o = zSceneFindObject(mb.evtId);
            if (o) zEntEvent(o, mb.evtCode);
        }
    }
    if (idx == 0 && bridged() && mb.padOverride) {
        u8* pad = (u8*)MPAD;
        u32 on = mb.padOn;
        AT(u32, pad + PAD_PRESSED) = on & ~overridePrevOn;
        AT(u32, pad + PAD_RELEASED) = ~on & overridePrevOn;
        AT(u32, pad + PAD_ON) = on;
        overridePrevOn = on;
        AT(s8, pad + PAD_ANALOG1) = mb.pax;
        AT(s8, pad + PAD_ANALOG1 + 1) = mb.pay;
        AT(s8, pad + PAD_ANALOG2) = mb.pcx;
        AT(s8, pad + PAD_ANALOG2 + 1) = mb.pcy;
    }
    // runs every game frame, also while gameplay is frozen for a cutscene (after the pad is final)
    if (bridged() && idx == AT(u8, GLOBALS + 0x6D1) && AT(u32, GLOBALS + G_SCENECUR) && !(mb.flags & (1024u | 16384u))) { arena_check(20); cutscene_frame(); talk_frame(); control_watchdog(); arena_check(24); }
    return r;
}


// Streamed sounds (voice lines, Deluxe's music in mnu5.HIP) play on 6 channels, each with a DVDFileInfo
// at +0x98 whose command block reads 0x4000-byte chunks into the channel's slice of one 0x18100-byte
// buffer. The game reuses that block while the DVD driver still owns it: the audio frame callback
// (fcb) stops a stream with DVDCancelAsync and may restart the channel in the same callback, and it
// also asks for the next chunk on its own flags. Requeuing or overwriting a block that is queued or
// being transferred breaks the driver's state; it then went on with a 0x80000-byte DMA from the
// buffer onward that wrote a whole sound over RenderWare's engine and resource arena (crash in
// _rwResHeapAlloc / _rwGCLightsGlobalEnable, mostly on the first frame of a level). Fix: a read on a
// stream block that is still in flight is held and issued from that block's completion callback,
// and iSndPrepStream's copy of a fresh DVDFileInfo leaves the live command block alone.
#define SND_CHANNELS 0x803C9140u   // stream channels, 0x10C each; DVDFileInfo at +0x98
#define DVDCB_STATE    0x0Cu
#define DVDCB_CALLBACK 0x28u
#define DVDCB_SIZE     0x30u
typedef void (*fn_dvdcb)(s32 result, void* block);
s32 DVDReadAbsAsyncPrio_orig(void* block, void* addr, s32 len, s32 offset, fn_dvdcb cb, s32 prio);
typedef struct { u32 pending; void* addr; s32 len, offset; fn_dvdcb cb; s32 prio; fn_dvdcb inflightCb; } HeldRead;
static HeldRead held[6];

static int stream_channel(u32 blk)
{
    u32 d = blk - (SND_CHANNELS + 0x98u);
    if (blk < SND_CHANNELS + 0x98u || d % 0x10Cu) return -1;
    d /= 0x10Cu;
    return d < 6 ? (int)d : -1;
}

static int dvd_in_flight(u32 blk) { s32 st = AT(s32, blk + DVDCB_STATE); return st == 1 || st == 2; }   // BUSY / WAITING

// the in-flight block's completion: its own callback first, then the held read
static void held_read_cb(s32 result, void* block)
{
    int i = stream_channel((u32)block);
    if (i < 0) return;
    HeldRead* h = &held[i];
    AT(u32, (u32)block + DVDCB_CALLBACK) = (u32)h->inflightCb;
    if (h->inflightCb) h->inflightCb(result, block);
    if (h->pending && !dvd_in_flight((u32)block)) {
        h->pending = 0;
        DVDReadAbsAsyncPrio_orig(block, h->addr, h->len, h->offset, h->cb, h->prio);
    }
}

// DVDReadAbsAsyncPrio entry (stubs.S bbol_dvdread_stub): 1 = go ahead, 0 = held (the caller gets TRUE)
s32 bbol_dvd_guard(void* block, void* addr, s32 len, s32 offset, fn_dvdcb cb, s32 prio)
{
    int i = stream_channel((u32)block);
    if (i < 0) return 1;
    u32 lvl = OSDisableInterrupts();
    if (!dvd_in_flight((u32)block)) { OSRestoreInterrupts(lvl); return 1; }
    HeldRead* h = &held[i];
    if (AT(u32, (u32)block + DVDCB_CALLBACK) != (u32)held_read_cb) {
        h->inflightCb = (fn_dvdcb)AT(u32, (u32)block + DVDCB_CALLBACK);
        AT(u32, (u32)block + DVDCB_CALLBACK) = (u32)held_read_cb;
    }
    h->pending = 1; h->addr = addr; h->len = len; h->offset = offset; h->cb = cb; h->prio = prio;   // newest wins
    mb.dbgRender[7]++;   // diagnostics: stream reads held back
    OSRestoreInterrupts(lvl);
    return 0;
}

// iSndPlay: bl iSndPrepStream(voice)
u32 bbol_snd_prep_stream(void* voice)
{
    u32 idx = ((u32)voice - (0x803BD5B4u + 0x1Cu)) / 0x64u;   // xSndVoiceInfo index, as iSndPlay computes it
    if (idx >= 6) return iSndPrepStream(voice);
    u32 blk = SND_CHANNELS + idx * 0x10Cu + 0x98u;
    u32 lvl = OSDisableInterrupts();
    if (!dvd_in_flight(blk)) { OSRestoreInterrupts(lvl); return iSndPrepStream(voice); }
    u8 keep[DVDCB_SIZE];
    memcpy(keep, (void*)blk, DVDCB_SIZE);
    u32 r = iSndPrepStream(voice);
    memcpy((void*)blk, keep, DVDCB_SIZE);
    mb.dbgRender[6]++;   // diagnostics: stream set up over a busy block
    OSRestoreInterrupts(lvl);
    return r;
}

// zGameLoop: bl zSceneRender. Sims draw nothing (most of a frame's CPU time).
void bbol_scene_render(void)
{
    if (bridged() && mb.mode == MODE_SIM) return;
    arena_check(25);
    zSceneRender();
    draw_overlays();
    arena_check(26);
}

// zGameLoop: bl iEnvEndRenderFX(env). Needs state iEnvRender sets inside zSceneRender.
void bbol_env_end_fx(void* env)
{
    if (bridged() && mb.mode == MODE_SIM) return;
    iEnvEndRenderFX(env);
}

// zMainLoop boot stub: scene id that replaces the start scene (0 = keep).
u32 bbol_boot_scene(void)
{
#ifdef MINIMAL_BOOT
    return MINIMAL_BOOT;
#endif
    if (bridged() && mb.bootScene) return mb.bootScene;
    return 0;
}

// With Dolphin's 32 MB MEM1 (the launcher asks for it) the game still only uses 24 MB. Its OS heap (RenderWare, sound
// buffers) fragments over a long session until a sound bank can't be allocated (NULL -> DVD read to
// address 0 -> hang, seen in DB02 after ~60 scenes). Give the OS heap the spare RAM: what
// OSAddToHeap does, which this game doesn't link.
#define OS_HEAP_ARRAY 0x803CC548u   // HeapDesc* HeapArray: {size, Cell* free, Cell* allocated}
#define OS_CURR_HEAP  0x803CAF40u
static u32 dbat3u(void) { u32 v; __asm__ volatile("mfspr %0, 542" : "=r"(v)); return v; }
static void set_dbat3(u32 u, u32 l)
{
    __asm__ volatile("isync; mtspr 543, %1; mtspr 542, %0; isync" : : "r"(u), "r"(l) : "memory");
}

#define TEST_NO_OSHEAP 0   // tests: 1 = map the extra RAM but give the OS heap none of it
static void grow_os_heap(void)
{
    if (mb.osHeapAdded || AT(u32, 0x80000028) < 0x2000000u) return;
    // The OS maps exactly 24 MB (DBAT0 16 MB at 0x80000000, DBAT2 8 MB at 0x81000000; DBAT1 is the
    // uncached view, DBAT3 unused). Map the extra 8 MB with DBAT3: 0x81800000 -> physical 0x01800000,
    // 8 MB, supervisor+user valid, cached, read/write.
    if (dbat3u() != 0) return;
    set_dbat3(0x818000FFu, 0x01800002u);
    mb.highRamMapped = 1;
    if (TEST_NO_OSHEAP) return;
    u32 arr = AT(u32, OS_HEAP_ARRAY), h = AT(u32, OS_CURR_HEAP);
    if (arr < 0x80000000u || h > 8) return;
    u32* cell = (u32*)EXTRA_OSHEAP_LO;   // Cell {prev, next, size}
    cell[0] = 0; cell[1] = 0; cell[2] = EXTRA_OSHEAP_HI - EXTRA_OSHEAP_LO;
    u8* hd = (u8*)(arr + 0xC * h);
    AT(u32, hd + 4) = (u32)DLInsert((void*)AT(u32, hd + 4), cell);
    AT(s32, hd + 0) += (s32)cell[2];
    mb.osHeapAdded = cell[2];
    mb.highRamMapped = 1;
}

// zMainLoop, before the "show the title menu?" test. Returns sShowMenuOnBoot.
u32 bbol_boot(void)
{
#ifdef MINIMAL_BOOT
    AT(u32, SSHOWMENUONBOOT) = 0;
    return 0;
#endif
    grow_os_heap();
    u32 i;
    for (i = 0; i < 30 && !bridged(); i++) VIWaitForRetrace();
    mb.bootWaited = i;
    if (bridged() && mb.bootScene) {
        AT(u32, SSHOWMENUONBOOT) = 0;
        volatile u8* s = (volatile u8*)(GLOBALS + G_SCENESTART);
        u32 id = mb.bootScene;
        s[0] = id >> 24; s[1] = id >> 16; s[2] = id >> 8; s[3] = id; s[4] = 0;
        if (mb.mode == MODE_SIM) AT(u32, GLOBALS + G_MINVSYNC) = 2;  // sims run at 30 fps
    }
    return AT(u32, SSHOWMENUONBOOT);
}


#ifdef BBOL_XFER_PROBE
// diagnostics (see stubs.S): a DMA into main RAM that starts in the sound streaming/loading buffer and
// runs past its end, or that touches RenderWare's engine block. dbgRender[21] = count << 8 | kind |
// 0x10 engine | 0x20 overrun, [22] dst, [23] len, [24] caller, [25] DVD offset / ARQ type
#define SND_BUF 0x803CBBCCu
void bbol_xfer_check(u32 kind, u32 dst, u32 len, u32 caller, u32 x)
{
    mb.dbgRender[kind == 1 ? 19 : 20]++;      // calls seen (proof the hooks run)
    if (kind == 1 && x != 1) return;          // ARQ: only ARAM -> main RAM
    // compare physical addresses: ARQ takes them physical, DMA buffers may be uncached (0xC...)
    u32 b = AT(u32, SND_BUF) & 0x01FFFFFFu, e = AT(u32, RW_ENGINE) & 0x01FFFFFFu;
    dst &= 0x01FFFFFFu;
    u32 end = dst + len;
    int over = b && dst >= b && dst < b + 0x18100u && end > b + 0x18100u;
    int eng = e && dst < e + 0x800u && end > e;
    if (!over && !eng) return;
    u32 n = (mb.dbgRender[21] >> 8) + 1;
    if (n == 1) {
        mb.dbgRender[22] = dst; mb.dbgRender[23] = len; mb.dbgRender[24] = caller; mb.dbgRender[25] = x;
        // DVDLowRead: the request block behind it (DVDCommandBlock: offset 0x10, length 0x14, addr 0x18,
        // transferred 0x20) -> [26..28]
        if (kind == 2 && x >= 0x80000000u && x < 0x82000000u) {
            mb.dbgRender[26] = AT(u32, x + 0x14); mb.dbgRender[27] = AT(u32, x + 0x18); mb.dbgRender[28] = AT(u32, x + 0x10);
        }
    }
    mb.dbgRender[21] = (n << 8) | kind | (eng ? 0x10u : 0) | (over ? 0x20u : 0);
}
#endif
