// Moving platforms: every game runs a platform's motion on its own clock from the moment it loaded
// the scene, so the same platform sits at a different point of its path in each game (a teammate
// riding it seemed to stand on air). One game per scene (the owner: the bridge picks the lowest
// party id there) publishes the motion state of its moving platforms; the others take it over when
// their copy has drifted. Scene memory has the same layout in every game, so the motion block
// (xEntMotion, its pointers included) is copied as is.
// Motions that run by themselves: extend/retract, orbit, move points, pendulum, and mechanisms while
// they move (tippy cylinders bob forever; a door/gate only moves after an event, which objsync shares,
// and then both games move it the same way anyway).
#include "game.h"
#include "mailbox.h"

#define BASE_PLATFORM 0x06
#define PL_MOTION     0xD8u   // zPlatform::motion (xEntMotion)
#define PL_STATE_END  0x150u  // .. state, plat_flags, tmr, ctr
#define MO_TYPE       0x04u   // xEntMotion::type
#define MO_FLAGS      0x06u
#define MO_STOPPED    4u
#define ENT_MODEL     0x24u
#define ENT_FRAME     0x48u
#define PLAT_EVERY    30      // frames between owner snapshots
#define PLAT_DRIFT    0.5f    // units: a copy this far off takes the owner's state

static u32 platFrame, lastInSeq;

static int self_moving(u8* b)
{
    if ((u32)b < 0x80000000u || b[4] != BASE_PLATFORM) return 0;
    u8 t = b[PL_MOTION + MO_TYPE];
    if (t > 5 || t == 2) return 0;   // spline motion: unused
    if ((u32)AT(u32, b + PL_MOTION) < 0x80000000u) return 0;   // no motion asset
    u8* mdl = (u8*)AT(u32, b + ENT_MODEL);
    return (u32)mdl >= 0x80000000u && (u32)mdl < 0x82000000u && (u32)AT(u32, mdl + 0x4C) >= 0x80000000u;
}

static f32* mat_of(u8* b) { return (f32*)AT(u32, (u8*)AT(u32, b + ENT_MODEL) + 0x4C); }

void platsync_frame(void)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    if (!sc || mb.bridge != BBOL_BRIDGE || mb.mode == MODE_SIM) return;
    u32 scene = AT(u32, sc);
    u32 n = AT(u32, sc + 0x74);
    u8** base = (u8**)AT(u32, sc + 0x78);
    if (!base) return;

    // owner: snapshot every moving platform
    if (mb.platOwner && ++platFrame >= PLAT_EVERY) {
        platFrame = 0;
        u32 k = 0;
        for (u32 i = 0; i < n && k < PLAT_MAX; i++) {
            u8* b = base[i];
            if (!self_moving(b) || (AT(u16, b + PL_MOTION + MO_FLAGS) & MO_STOPPED)) continue;
            volatile PlatState* p = &mb.platOut[k++];
            p->index = (u16)i;
            p->flags = 1;
            p->id = AT(u32, b);
            for (u32 j = 0; j < PL_STATE_END - PL_MOTION; j++) p->st[j] = b[PL_MOTION + j];
            f32* m = mat_of(b);
            for (int j = 0; j < 16; j++) p->mat[j] = m[j];
        }
        mb.platOutScene = scene;
        mb.platOutCount = k;
        mb.platOutSeq = mb.platOutSeq + 1;
    }

    // everyone else: take over platforms that drifted
    if (mb.platOwner || mb.platInSeq == lastInSeq) return;
    lastInSeq = mb.platInSeq;
    if (mb.platInScene != scene) return;
    u32 cnt = mb.platInCount;
    if (cnt > PLAT_MAX) cnt = PLAT_MAX;
    for (u32 k = 0; k < cnt; k++) {
        volatile PlatState* p = &mb.platIn[k];
        if (p->index >= n) continue;
        u8* b = base[p->index];
        if (!self_moving(b) || AT(u32, b) != p->id) continue;
        f32* m = mat_of(b);
        f32 dx = m[12] - p->mat[12], dy = m[13] - p->mat[13], dz = m[14] - p->mat[14];
        if (dx * dx + dy * dy + dz * dz < PLAT_DRIFT * PLAT_DRIFT) continue;
        for (u32 j = 0; j < PL_STATE_END - PL_MOTION; j++) b[PL_MOTION + j] = p->st[j];
        for (int j = 0; j < 16; j++) m[j] = p->mat[j];
        u8* fr = (u8*)AT(u32, b + ENT_FRAME);
        if ((u32)fr >= 0x80000000u && (u32)fr < 0x82000000u)
            for (int j = 0; j < 16; j++) AT(f32, fr + 4 * j) = p->mat[j];
        mb.platApplied = mb.platApplied + 1;
    }
}
