// Hazard zones (cannons): a trigger whose EnterPlayer starts something that launches boulders at
// fixed targets (GL01: CANNON_1_TRIG_1 -> CANNON_1_GROUP_TIMER -> timers -> BOULDER_GENERATOR_1
// LaunchBoulderAtWidget). Puppets never touch triggers in our game (they started races), so a cannon
// firing at a teammate stayed quiet here. Each game reports its own player entering/leaving such a
// zone; teammates' games send the same event to their copy. A zone is "occupied" while any player is
// in it: it starts when the first one enters and stops when the last one leaves (our own player's
// events are dropped while a teammate is still in there, and the other way round).
#include "game.h"
#include "mailbox.h"

#define HZ_MAX       48
#define EVT_ENTERPLAYER 5
#define EVT_EXITPLAYER  6
#define EVT_LAUNCH_LO 370   // LaunchBoulderAtWidget, AtPoint, AtPlayer
#define EVT_LAUNCH_HI 372

typedef struct { u32 id; u8* trig; u8 localIn; u8 pad[3]; u32 remoteMask; } Hazard;
static Hazard hz[HZ_MAX];
static u32 hzCount, hzScene;
static int hzRelaying;
extern int inPuppet;

static int launches(u8* b, int depth)
{
    if ((u32)b < 0x80000000u || (u32)b >= 0x82000000u || depth > 4) return 0;
    if (b[4] == 0x11) {   // a group passes its events to its members (xGroup: asset, item[])
        u8* ga = (u8*)AT(u32, b + 0x10);
        u8** items = (u8**)AT(u32, b + 0x14);
        u32 cnt = ((u32)ga >= 0x80000000u) ? AT(u16, ga + 8) : 0;
        for (u32 k = 0; k < cnt && k < 32 && (u32)items >= 0x80000000u; k++)
            if (launches(items[k], depth + 1)) return 1;
    }
    u32 n = b[5];
    u8* l = (u8*)AT(u32, b + 8);
    if (!n || (u32)l < 0x80000000u || (u32)l >= 0x82000000u) return 0;
    for (u32 k = 0; k < n && k < 32; k++) {
        u8* lk = l + k * 0x20;
        u16 ev = AT(u16, lk + 2);
        if (ev >= EVT_LAUNCH_LO && ev <= EVT_LAUNCH_HI) return 1;
        u32 to = AT(u32, lk + 4);
        if (to && to != AT(u32, b) && launches((u8*)zSceneFindObject(to), depth + 1)) return 1;
    }
    return 0;
}

static void hazard_scan(u8* sc)
{
    hzCount = 0;
    hzScene = AT(u32, sc);
    u32 n = AT(u32, sc + 0x74);
    u8** base = (u8**)AT(u32, sc + 0x78);
    for (u32 i = 0; base && i < n && hzCount < HZ_MAX; i++) {
        u8* b = base[i];
        if ((u32)b < 0x80000000u || b[4] != 0x01) continue;   // triggers
        // only what its EnterPlayer links start
        u32 ln = b[5];
        u8* l = (u8*)AT(u32, b + 8);
        int hit = 0;
        for (u32 k = 0; k < ln && k < 32 && (u32)l >= 0x80000000u && !hit; k++) {
            u8* lk = l + k * 0x20;
            if (AT(u16, lk) != EVT_ENTERPLAYER) continue;
            u16 ev = AT(u16, lk + 2);
            if (ev >= EVT_LAUNCH_LO && ev <= EVT_LAUNCH_HI) hit = 1;
            else hit = launches((u8*)zSceneFindObject(AT(u32, lk + 4)), 1);
        }
        if (!hit) continue;
        Hazard* h = &hz[hzCount++];
        h->id = AT(u32, b); h->trig = b; h->localIn = 0; h->remoteMask = 0;
    }
    mb.hzCount = hzCount;
}

static Hazard* find(u8* to)
{
    for (u32 i = 0; i < hzCount; i++) if (hz[i].trig == to) return &hz[i];
    return 0;
}

static void send_event(Hazard* h, u32 ev)
{
    hzRelaying = 1;
    zEntEvent(h->trig, ev);
    hzRelaying = 0;
}

// stubs.S (zEntEvent entry, toEvent 5/6): 0 = drop the event
u32 bbol_hazard_event(u8* to, u32 ev)
{
    if (hzRelaying || inPuppet || !hzCount || mb.bridge != BBOL_BRIDGE) return 1;
    Hazard* h = find(to);
    if (!h) return 1;
    int enter = ev == EVT_ENTERPLAYER;
    if (h->localIn == (u8)enter) return 1;
    h->localIn = (u8)enter;
    u32 head = mb.hzOutHead;
    if (head - mb.hzOutTail < HZ_RING) {
        mb.hzOut[head % HZ_RING][0] = h->id;
        mb.hzOut[head % HZ_RING][1] = ev;
        mb.hzOutHead = head + 1;
    }
    return h->remoteMask ? 0 : 1;   // a teammate is in there: the zone is already running
}

// once a frame: teammates' zone events; teammates who left the scene count as gone
void hazard_frame(void)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    if (!sc || mb.bridge != BBOL_BRIDGE || mb.mode == MODE_SIM) return;
    if (AT(u32, sc) != hzScene) { hazard_scan(sc); mb.hzInTail = mb.hzInHead; }
    while (mb.hzInTail != mb.hzInHead) {
        u32 k = mb.hzInTail % HZ_RING;
        u32 id = mb.hzIn[k][0], ev = mb.hzIn[k][1], rank = mb.hzIn[k][2];
        mb.hzInTail = mb.hzInTail + 1;
        if (rank == 0 || rank > 32) continue;
        u32 bit = 1u << (rank - 1);
        for (u32 i = 0; i < hzCount; i++) {
            Hazard* h = &hz[i];
            if (h->id != id) continue;
            int was = h->remoteMask || h->localIn;
            if (ev == EVT_ENTERPLAYER) h->remoteMask |= bit; else h->remoteMask &= ~bit;
            int now = h->remoteMask || h->localIn;
            if (!was && now) send_event(h, EVT_ENTERPLAYER);
            if (was && !now) send_event(h, EVT_EXITPLAYER);
            mb.hzApplied = mb.hzApplied + 1;
        }
    }
    // a teammate no longer in the scene (no puppet slot with their rank) has left every zone
    u32 present = 0;
    for (int s = 0; s < MAX_REMOTE; s++)
        if (mb.remote[s].active && mb.npcRemoteRank[s] && mb.npcRemoteRank[s] <= 32) present |= 1u << (mb.npcRemoteRank[s] - 1);
    for (u32 i = 0; i < hzCount; i++) {
        Hazard* h = &hz[i];
        if (!(h->remoteMask & ~present)) continue;
        int was = 1;
        h->remoteMask &= present;
        if (was && !h->remoteMask && !h->localIn) send_event(h, EVT_EXITPLAYER);
    }
}
