// Story cutscenes: when a cinematic (a zCutsceneMgr) starts in this game, report it; when a
// teammate in the same scene started one, start the same cutscene manager here with the event
// its own trigger would have sent (0xBE, "play"). Conversations and other non-cinematic scenes
// are not handled here (teammates get a watch-along prompt for those).
#include "game.h"
#include "mailbox.h"

#define EVT_CUTSCENE_PLAY 0xBEu
#define BASE_CUTSCENEMGR  0x28u   // en_ZBASETYPE eBaseTypeCutsceneMgr
#define CONTROL_OWNER_CUTSCENE 0x8000u
#define WATCHED_MAX 32

static u32 lastCmgr;
static u32 startedFor;    // cutscene manager we started for a teammate (not reported back)
static u32 pendingCheck;  // frames left to see whether our request really started
static u32 watched[WATCHED_MAX], nWatched, watchedScene;
// A teammate's cutscene we watch along may end by moving the player to where it is staged (the
// teammate's spot: a teleport box's cinematic took the watcher along). Put ours back afterwards.
static f32 watchPos[3];
static int watchingFor;   // 1 while a cutscene we started for a teammate plays

static int seen(u32 id)
{
    for (u32 i = 0; i < nWatched; i++) if (watched[i] == id) return 1;
    return 0;
}

static void remember(u32 id)
{
    if (!seen(id) && nWatched < WATCHED_MAX) watched[nWatched++] = id;
}

void cutscene_frame(void)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    u32 scene = sc ? AT(u32, sc) : 0;
    if (scene != watchedScene) { watchedScene = scene; nWatched = 0; startedFor = 0; pendingCheck = 0; lastCmgr = 0; watchingFor = 0; }

    u32 cm = AT(u32, GLOBALS + G_CMGR);
    if (cm && cm != lastCmgr) {
        u32 id = AT(u32, cm);
        remember(id);
        watchingFor = id == startedFor && startedFor;
        if (id != startedFor) {
            mb.csId = id;
            mb.csScene = scene;
            mb.csSeq = mb.csSeq + 1;
        }
        startedFor = 0;
        pendingCheck = 0;
    }
    if (!cm && lastCmgr && watchingFor) {
        // the teammate's cutscene ended here: back to where we stood when it started
        watchingFor = 0;
        mb.tpPos[0] = watchPos[0];
        mb.tpPos[1] = watchPos[1];
        mb.tpPos[2] = watchPos[2];
        mb.tpSeq = mb.tpSeq + 1;
    }
    lastCmgr = cm;

    // the cutscene manager takes control away before it checks it can play: if our request
    // didn't start anything, give control back
    if (pendingCheck && --pendingCheck == 0 && !cm) {
        zEntPlayerControlOn(CONTROL_OWNER_CUTSCENE);
        startedFor = 0;
    }

    if (mb.csPlaySeq == mb.csPlayAck) return;
    mb.csPlayAck = mb.csPlaySeq;
    if (cm) return;                                   // already watching something
    if (mb.mode == MODE_SIM) return;                  // sims don't watch movies
    if (seen(mb.csPlayId)) return;                    // we already watched this one here
    u8* mgr = (u8*)zSceneFindObject(mb.csPlayId);
    if (!mgr || mgr[4] != BASE_CUTSCENEMGR) return;
    u8* fr = (u8*)AT(u32, GLOBALS + G_ENT_FRAME);
    if (!fr) return;
    for (int k = 0; k < 3; k++) watchPos[k] = AT(f32, fr + FR_POS + 4 * k);
    startedFor = mb.csPlayId;
    remember(mb.csPlayId);
    pendingCheck = 30;
    zEntEvent(mgr, EVT_CUTSCENE_PLAY);
}

// ---- conversations --------------------------------------------------------------------
// A conversation (talk box) that starts here is reported; a teammate's conversation in our scene
// is offered with a prompt shown in that same talk box: D-pad Up watches along (the talk box is
// started here too), Down or 8 seconds declines.
#define BASE_TALKBOX      0x34u
#define BASE_TASKBOX      0x35u
#define EVT_START_CONVERSATION 335u
#define CONTROL_OWNER_TALK 0x10u
#define BTN_UP   0x10u
#define BTN_DOWN 0x40u

static u32 lastTalk, startedTalk, promptFrames, promptFor, teammateTalk, teammateTalkFrames;
static int promptBox;   // the watch-along question is on screen (drawn by talk_prompt_text)

// A conversation with someone (an NPC next to the player), not a sign or a tutorial hint: only
// those are offered to teammates.
static int npc_near(u8* sc, f32 r)
{
    u8* fr = (u8*)AT(u32, GLOBALS + G_ENT_FRAME);
    if ((u32)fr < 0x80000000u) return 0;
    f32 px = AT(f32, fr + 0x30), py = AT(f32, fr + 0x34), pz = AT(f32, fr + 0x38);
    u32 n = AT(u32, sc + 0x74);
    u8** base = (u8**)AT(u32, sc + 0x78);
    for (u32 i = 0; base && i < n; i++) {
        u8* b = base[i];
        if ((u32)b < 0x80000000u || b[4] != 0x2B) continue;
        u8* mdl = (u8*)AT(u32, b + 0x24);
        u8* m = ((u32)mdl >= 0x80000000u && (u32)mdl < 0x82000000u) ? (u8*)AT(u32, mdl + 0x4C) : 0;
        if ((u32)m < 0x80000000u || (u32)m >= 0x82000000u) continue;
        f32 dx = AT(f32, m + 0x30) - px, dy = AT(f32, m + 0x34) - py, dz = AT(f32, m + 0x38) - pz;
        if (dx * dx + dy * dy + dz * dz < r * r) return 1;
    }
    return 0;
}

// Starts the teammate's conversation here: the task box that talks through that talk box (its
// current stage, synced from the party), or the talk box itself.
static void watch_along(u32 talkId)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    u32 n = sc ? AT(u32, sc + 0x74) : 0;
    u8** base = sc ? (u8**)AT(u32, sc + 0x78) : 0;
    for (u32 i = 0; base && i < n; i++) {
        u8* b = base[i];
        if (!b || b[4] != BASE_TASKBOX) continue;
        u8* a = (u8*)AT(u32, b + 0x14);
        if ((u32)a >= 0x80000000u && AT(u32, a + 0x14) == talkId && b[0x10]) {
            ztaskbox_start_talk(b, 0);
            return;
        }
    }
    u8* box = (u8*)zSceneFindObject(talkId);
    if (box) zEntEvent(box, EVT_START_CONVERSATION);
}
static char promptText[64];

static u32 talkScene;

void talk_frame(void)
{
    u8* sc = (u8*)AT(u32, GLOBALS + G_SCENECUR);
    // a new scene: everything below points into the old one (the prompt's text box is gone; using
    // it wrecked the new scene's memory when a prompt was up during a warp)
    u32 scene = sc ? AT(u32, sc) : 0;
    if (scene != talkScene) {
        talkScene = scene;
        if (promptBox) mb.offerResult = 2;
        promptBox = 0;
        lastTalk = startedTalk = promptFor = teammateTalk = teammateTalkFrames = promptFrames = 0;
    }
    u8* active = (u8*)AT(u32, TALK_SHARED + 8);
    u32 id = ((u32)active >= 0x80000000u) ? AT(u32, active) : 0;

    if (promptBox) {
        // the D-pad answers the question
        u32 pressed = AT(u32, MPAD + PAD_PRESSED);
        if (active || AT(u32, GLOBALS + G_CMGR)) {
            promptBox = 0;
            mb.offerResult = 2;
        } else if (pressed & BTN_UP) {
            promptBox = 0;
            startedTalk = promptFor;
            watch_along(promptFor);
            mb.offerResult = 1;
        } else if ((pressed & BTN_DOWN) || --promptFrames == 0) {
            promptBox = 0;
            mb.offerResult = 2;
        }
        active = (u8*)AT(u32, TALK_SHARED + 8);
        id = ((u32)active >= 0x80000000u) ? AT(u32, active) : 0;
    }

    // a teammate's conversation that started here too (their puppet bumped the villager in our game):
    // it's theirs, close it here (we get the watch-along prompt instead)
    if (teammateTalkFrames) teammateTalkFrames--;
    if (id && id != lastTalk && id == teammateTalk && teammateTalkFrames && id != startedTalk) {
        ztalkbox_stop_talk(active);
        zEntPlayerControlOn(CONTROL_OWNER_TALK);
        active = 0;
        id = 0;
    }

    if (id != lastTalk) {
        // only conversations that hold the player are offered (not signs and hints); the hold may
        // come a frame after the talk box opens, so keep looking until it ends
        int holds = (AT(u32, GLOBALS + G_CONTROLOFF) & CONTROL_OWNER_TALK) != 0;
        if (id && id != startedTalk && sc && holds) {
            // reported even for signs: a teammate's game closes it if our puppet started it there
            mb.talkNpc = npc_near(sc, 6.0f);
            mb.talkId = id;
            mb.talkScene = AT(u32, sc);
            mb.talkSeq = mb.talkSeq + 1;
        }
        if (!id || id == startedTalk || holds) {
            if (id != startedTalk) startedTalk = 0;
            lastTalk = id;
        }
    }

    if (mb.offerSeq == mb.offerAck) return;
    mb.offerAck = mb.offerSeq;
    teammateTalk = mb.offerId;
    teammateTalkFrames = 10 * 60;
    if (id && id == mb.offerId && id != startedTalk) {
        ztalkbox_stop_talk(active);              // it leaked in before the teammate's report did
        zEntPlayerControlOn(CONTROL_OWNER_TALK);
        active = 0;
        id = 0;
        lastTalk = 0;
    }
    if (mb.mode == MODE_SIM || promptBox || active || AT(u32, GLOBALS + G_CMGR) || AT(u32, GLOBALS + G_CONTROLOFF)
        || !zSceneFindObject(mb.offerId)) {
        mb.offerResult = 3;
        return;
    }
    // no text: the teammate's talk isn't one to watch (a sign, or watch-along is off); it was only
    // passed on so the copy their puppet started here gets closed (above)
    if (!mb.offerText[0]) { mb.offerResult = 3; return; }
    for (int k = 0; k < 63; k++) promptText[k] = mb.offerText[k];
    promptText[63] = 0;
    promptBox = 1;
    promptFor = mb.offerId;
    promptFrames = 8 * 60;
    mb.offerResult = 0;
}

// the watch-along question while it is up (drawn small at the bottom of the screen), else 0
const char* talk_prompt_text(void) { return promptBox ? promptText : 0; }
