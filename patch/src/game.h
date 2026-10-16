// Game (GQPE78, BFBB USA GameCube) addresses and layouts used by the patch.
// Offsets come from the bfbbdecomp project (config/GQPE78/symbols.txt, src/SB/*).
#ifndef BBOL_GAME_H
#define BBOL_GAME_H

typedef unsigned char u8;
typedef signed char s8;
typedef unsigned short u16;
typedef short s16;
typedef unsigned int u32;
typedef int s32;
typedef float f32;

#define AT(type, addr) (*(volatile type*)(addr))

// ---- data -----------------------------------------------------------------
#define GLOBALS          0x803C0558u   // zGlobals globals (size 0x1FC8)
#define MPAD             0x8038C994u   // _tagxPad mPad[4] (0x148 each)
#define PAD_SIZE         0x148u

// zGlobals offsets
#define G_CAMERA         0x000u        // xCamera (0x330)
#define G_CAMERA_SIZE    0x330u
#define G_CAMERA_MAT     0x014u        // xMat4x3 camera.mat
#define G_SCENESTART     0x638u        // char sceneStart[32]
#define G_MINVSYNC       0x6D4u
#define G_PLAYER         0x6E0u        // zPlayerGlobals player
#define G_PLAYER_END     0x1FB8u       // pickupTable follows
#define G_PLAYER_SIZE    (G_PLAYER_END - G_PLAYER)
#define G_POWERUP        0x9BDu        // player.g.PowerUp[2]: bubble bowl, cruise bubble
#define G_ENT_MODEL      0x704u        // player.ent.model
#define G_ENT_UPDATE     0x714u        // player.ent.update
#define G_ENT_RENDER     0x724u        // player.ent.render
#define G_ENT_FRAME      0x728u        // player.ent.frame
#define G_ANALOGMIN      0x874u
#define G_ANALOGMAX      0x876u
#define G_MODEL_SB       0x16A0u
#define G_MODEL_PAT      0x16A4u
#define G_MODEL_SANDY    0x16A8u
#define G_VISIBLE        0x16ACu
#define G_HEALTH         0x16B0u
#define G_DAMAGETIMER    0x1708u
#define G_CONTROLOFF     0x1788u
#define G_MAXHEALTH      0x1738u
#define G_INV_SHINY      0x1B00u
#define G_INV_SPATULA    0x1B04u
#define G_CMGR           0x1FBCu       // zCutsceneMgr* playing, or NULL
#define G_SCENECUR       0x1FC0u

// player static state in zEntPlayer.cpp / zGame.cpp small-data, swapped per puppet
#define PSTATIC_A        0x803CB5B4u   // gSpongeBall .. mount_type
#define PSTATIC_A_SIZE   (0x803CB6BCu - PSTATIC_A)
#define PSTATIC_B        0x803CAAA0u   // minVelmag .. surf* .. update_dt, lastgCurrentPlayer, player_hit_anim,
#define PSTATIC_B_SIZE   0x58u         // player_dead_anim, sBubbleBowl*, sPlayerNPC_KnockBack* (zEntPlayer .sdata)
#define PSTATIC_C        0x802DE3D0u   // zEntPlayer .bss: stuck/rumble/delayed sounds, lastDeltaPos, motion,
#define PSTATIC_C_SIZE   (0x802DF430u - PSTATIC_C)  // sHitch, model tags, drybob, old_collis (collision results)
#define PSTATIC_D        0x803BF624u   // common .bss: gPlayerAbsMat, rendermat (the matrix the player is drawn
#define PSTATIC_D_SIZE   (0x803BF74Cu - PSTATIC_D)  // with), sCameraLastMat, sDriveVel, floor_* (ground detection)
#define ZCAM_PLAYERVEL   0x803CB4A4u   // zCamera: points at the player's velocity
// player modes with their own state (each skips the normal player render/update while active)
#define BUNGEE_SHARED    0x80297A68u   // bungee_state shared
#define BUNGEE_SIZE      0x19Cu
#define CRUISE_SHARED    0x8028C2A4u   // cruise_bubble shared
#define CRUISE_SIZE      0x18Cu
#define OOB_SHARED       0x80297E48u   // oob_state shared (out of bounds: fade, respawn)
#define OOB_SIZE         0x70u
#define GCURRENTPLAYER   0x803CB660u   // inside A
#define SPLAYERIGNORESND 0x803CB5C4u   // inside A
#define SSHOWMENUONBOOT  0x803CABB4u
#define GXHEAP 0x8038C5E4u   // xMemHeap_tag gxHeap[3], 0xDC each
#define XMODELINSTSTATICALLOC 0x803CB2FCu
#define GGAMEMODE        0x803CB8A8u
#define GGAMESTATE       0x803CAB40u

// xEntFrame offsets
#define FR_POS           0x30u
#define FR_OLDPOS        0x70u
#define FR_ROTANGLE      0xB8u
#define FR_VEL           0xD4u
// xModelInstance
#define MI_ANIM          0x0Cu
#define MI_MAT           0x4Cu
// RwMatrix pos
#define RWM_POS          0x30u
// _tagxPad
#define PAD_ON           0x2Cu
#define PAD_PRESSED      0x30u
#define PAD_RELEASED     0x34u
#define PAD_ANALOG1      0x38u
#define PAD_ANALOG2      0x3Au

// ---- functions ------------------------------------------------------------
typedef void (*fn_update)(void* ent, void* scene, f32 dt);
typedef void (*fn_render)(void* ent);

void* memcpy(void* d, const void* s, u32 n);
void* memset(void* d, int c, u32 n);
char* strcpy(char* d, const char* s);
void OSReport(const char* fmt, ...);
void VIWaitForRetrace(void);
u32 iSndPrepStream(void* voice);
u32 OSDisableInterrupts(void);
u32 OSRestoreInterrupts(u32 level);
void DCFlushRange(void* p, u32 n);

void xPadNormalizeAnalog(void* pad, s32 inner, s32 outer);
s32  xPadUpdate(s32 idx, f32 dt);
void zGameSetupPlayer(void);
void rwFrameSyncDirty(void);
void zSceneExit(s32 beginReload);
void* DLInsert(void* list, void* cell);
void zEntPlayerReset(void* ent);
void zSceneSwitch(void* portal, s32 forceSameScene);
void xShadowManager_Add(void* ent);
void* XSER_get_client(u32 idtag);
void zSceneRender(void);
void iEnvEndRenderFX(void* env);
void xSerial_setClient(void* xs, u32 idtag);
void* zSceneFindObject(u32 id);
void zEntEvent(void* to, u32 event);
void zGameStateSwitchEvent(s32 event);
void zEntPlayerControlOn(u32 owner);
void zEntPickup_CheckAllPickupsAgainstPlayer(void* scene, f32 dt);
void ztalkbox_start_talk(void* box, const char* text, void* cb, void* npc);
void ztalkbox_stop_talk(void* box);
void zEntEvent3(void* from, void* to, u32 event);
void xModelEval(void* model);
void xEntRender(void* ent);
void ztextbox_activate(void* tb);
void ztextbox_deactivate(void* tb);
void ztextbox_set_text(void* tb, const char* text);
void iCameraUpdatePos(void* rwcam, void* mat);
#define G_CAMERA_LOCAM   0x010u        // xCamera.lo_cam (RwCamera*)
void ztaskbox_start_talk(void* task, void* npc);
void ztalkbox_start_talk_id(void* box, u32 text, void* cb, void* npc);
void xfont_start_render(const void* font);                       // xfont::start_render() const
void xfont_stop_render(const void* font);
void xfont_irender(const void* font, const char* text, f32 x, f32 y);
#define TALK_SHARED      0x8031B320u   // zTalkBox.cpp shared (active talk box at +8)

#endif
