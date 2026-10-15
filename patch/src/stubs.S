# Replaces `lwz r0, sShowMenuOnBoot(r13)` in zMainLoop: the next instruction tests r0.
# zMainLoop already holds the start scene id in r30 at this point; a sim replaces it.
    .text
    .globl bbol_boot_stub
bbol_boot_stub:
    stwu 1, -32(1)
    mflr 0
    stw 0, 36(1)
    stw 30, 16(1)
    bl bbol_boot
    stw 3, 20(1)
    bl bbol_boot_scene
    lwz 30, 16(1)
    cmpwi 3, 0
    beq 1f
    mr 30, 3
1:
    lwz 3, 36(1)
    mtlr 3
    lwz 0, 20(1)
    addi 1, 1, 32
    blr

# RwFrameUpdateObjects entry (was `lwz r5, 0xA0(r3)`: frame->root). A frame with no root (or no
# frame) would put address 8 on RenderWare's dirty-frame list and crash the next camera update.
# Skip those; remember the caller in mb.dbgRender[16..18] (LR, count, frame).
    .globl bbol_rwfuo_stub
bbol_rwfuo_stub:
    cmpwi 3, 0
    beq 1f
    lwz 5, 0xA0(3)
    cmpwi 5, 0
    beq 1f
    b rwFrameUpdateObjects_cont
1:
    lis 5, mb@ha
    addi 5, 5, mb@l
    mflr 0
    stw 0, 0x4564 + 64(5)
    lwz 0, 0x4564 + 68(5)
    addi 0, 0, 1
    stw 0, 0x4564 + 68(5)
    stw 3, 0x4564 + 72(5)
    blr

# The other RenderWare frame functions that put frame->root on the dirty list. Same guard; slot k
# records the caller in mb.dbgRender[19 + 2k] (LR) and [20 + 2k] (count).
.macro rwguard name, cont, slot
    .globl \name
\name:
    cmpwi 3, 0
    beq 1f
    lwz 12, 0xA0(3)
    cmpwi 12, 0
    beq 1f
    stwu 1, -0x10(1)
    b \cont
1:
    lis 12, mb@ha
    addi 12, 12, mb@l
    mflr 0
    stw 0, 0x4564 + 4 * (19 + 2 * \slot)(12)
    lwz 0, 0x4564 + 4 * (20 + 2 * \slot)(12)
    addi 0, 0, 1
    stw 0, 0x4564 + 4 * (20 + 2 * \slot)(12)
    blr
.endm
    rwguard bbol_rwg_translate, rwFrameTranslate_cont, 0
    rwguard bbol_rwg_transform, rwFrameTransform_cont, 1
    rwguard bbol_rwg_rotate, rwFrameRotate_cont, 2
    rwguard bbol_rwg_addchild, rwFrameAddChild_cont, 3
    rwguard bbol_rwg_ortho, rwFrameOrthoNormalize_cont, 4

# zNPCCommon::GiveReward entry (was `stwu r1, -0x30(r1)`): enemies killed here only because a
# teammate killed them (objsync) give no loot; the teammate's game gave it.
    .globl bbol_givereward_stub
bbol_givereward_stub:
    stwu 1, -16(1)
    mflr 0
    stw 0, 20(1)
    stw 3, 8(1)
    bl bbol_reward_allowed
    cmpwi 3, 0
    lwz 3, 8(1)
    lwz 0, 20(1)
    mtlr 0
    addi 1, 1, 16
    beqlr
    stwu 1, -0x30(1)
    b GiveReward_cont

# zEntPlayerCollTrigger(xEnt* player, xScene*) entry (was `stwu r1, -0x120(r1)`): the player
# entering trigger volumes. A teammate's puppet must not walk into triggers in this game (a race's
# finish line ended the race for everyone in the scene); the teammate's own game fires them.
    .globl bbol_colltrigger_stub
bbol_colltrigger_stub:
    lis 12, inPuppet@ha
    lwz 12, inPuppet@l(12)
    cmpwi 12, 0
    bnelr
    stwu 1, -0x120(1)
    b CollTrigger_cont
