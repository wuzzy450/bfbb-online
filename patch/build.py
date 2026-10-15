"""Builds the BFBB Online in-game patch.

Outputs (patch/out/):
  bbol_patch.bin   code + data loaded at LOAD_ADDR
  bbol_patch.json  everything the launcher needs to apply it to a player's own Deluxe main.dol
  main.dol         (with --dol BASE) a ready patched DOL for testing

Needs: python -m pip install ziglang
"""
import argparse, base64, hashlib, json, os, re, struct, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "src")
OUT = os.path.join(HERE, "out")

LOAD_ADDR = 0x803D8A80          # just above the original arena start (0x803D8A50)
ARENA_LO_SITE = (0x801D0F38, 0x3C60803E, 0x38638A50)  # OSInit: lis r3,0x803E; addi r3,r3,-0x75B0

# Game functions the C code calls (C name -> address in GQPE78).
GAME_SYMBOLS = {
    "memcpy": 0x80003540,
    "memset": 0x80003458,
    "strcpy": 0x801E7700,
    "OSReport": 0x801D2F04,
    "VIWaitForRetrace": 0x801DB9B8,
    "DCFlushRange": 0x801D2358,
    "xPadNormalizeAnalog": 0x80036478,
    "xPadUpdate": 0x80035FC4,
    "zGameSetupPlayer": 0x800993A8,
    "zEntPlayerReset": 0x8007A120,
    "zSceneSwitch": 0x800B23F8,
    "xShadowManager_Add": 0x80047654,
    "XSER_get_client": 0x80043968,
    "zSceneRender": 0x800B48CC,
    "iEnvEndRenderFX": 0x800C329C,
    "xSerial_setClient": 0x80042EF0,   # xSerial::setClient(U32)
    "zSceneFindObject": 0x800B4A3C,
    "zEntEvent": 0x8001F5F0,           # zEntEvent(xBase*, U32)
    "zEntPlayerControlOn": 0x80066500,
    "zEntPickup_CheckAllPickupsAgainstPlayer": 0x800631EC,
    "ztalkbox_start_talk": 0x80132890,     # ztalkbox::start_talk(const char*, callback*, zNPCCommon*)
    "ztalkbox_stop_talk": 0x80132A90,      # ztalkbox::stop_talk()
    "zEntEvent3": 0x8001F6E0,
    "xModelEval": 0x800346B0,
    "xfont_start_render": 0x800214E4,         # xfont::start_render() const
    "xfont_stop_render": 0x80021508,
    "xfont_irender": 0x8002167C,              # xfont::irender(const char*, float, float) const
    "xEntRender": 0x800194CC,
    "ztextbox_activate": 0x801345A0,       # ztextbox::activate()
    "ztextbox_deactivate": 0x801345EC,     # ztextbox::deactivate()
    "ztextbox_set_text": 0x80134650,       # ztextbox::set_text(const char*)
    "iCameraUpdatePos": 0x800C0530,
    "rwFrameSyncDirty": 0x8023CDFC,
    "zSceneExit": 0x800B2068,
    "DLInsert": 0x801D1D54,                # OSAlloc.c DLInsert(Cell* list, Cell* cell): sorted, merging
    "rwFrameUpdateObjects_cont": 0x80238FC8,  # RwFrameUpdateObjects + 4
    "rwFrameTranslate_cont": 0x802392E4,      # + 4 (after stwu r1,-0x10(r1))
    "rwFrameTransform_cont": 0x80239380,
    "rwFrameRotate_cont": 0x8023941C,
    "rwFrameAddChild_cont": 0x8023907C,
    "rwFrameOrthoNormalize_cont": 0x802394B8,
    "CollTrigger_cont": 0x8007C560,           # zEntPlayerCollTrigger + 4
    "GiveReward_cont": 0x800F1D44,            # zNPCCommon::GiveReward + 4              # zSceneExit(S32 beginReload)        # _rwFrameSyncDirty()        # iCameraUpdatePos(RwCamera*, xMat4x3*)
    "ztaskbox_start_talk": 0x80134AC8,     # ztaskbox::start_talk(zNPCCommon*)
    "ztalkbox_start_talk_id": 0x80132A14,  # ztalkbox::start_talk(U32, callback*, zNPCCommon*)              # zEntEvent(xBase* from, xBase* to, U32 event)
}

BCTRL = 0x4E800421

# (site, original instruction, our function). "bl" replaces the instruction with bl func.
HOOKS = [
    (0x800978FC, ("bl", 0x800993A8), "bbol_setup_player"),   # zGameSetup: zGameSetupPlayer()
    (0x80097EA0, BCTRL, "bbol_player_update"),               # zGameLoop: player.ent.update
    (0x800B45A8, BCTRL, "bbol_player_render"),               # zSceneRenderPreFX: player.ent.render
    (0x800B3078, ("bl", 0x8007A120), "bbol_player_reset"),   # zSceneReset: zEntPlayerReset
    (0x80097D38, ("bl", 0x80035FC4), "bbol_pad_update"),     # zGameLoop: xPadUpdate
    (0x800A6584, ("bl", 0x80035FC4), "bbol_pad_update"),     # zMenuLoop: xPadUpdate x4
    (0x800A659C, ("bl", 0x80035FC4), "bbol_pad_update"),
    (0x800A65A8, ("bl", 0x80035FC4), "bbol_pad_update"),
    (0x800A65B4, ("bl", 0x80035FC4), "bbol_pad_update"),
    (0x800A5138, 0x800D82B4, "bbol_boot_stub"),              # zMainLoop: lwz r0,sShowMenuOnBoot(r13)
    (0x80098064, ("bl", 0x800B48CC), "bbol_scene_render"),   # zGameLoop: zSceneRender()
    (0x80098088, ("bl", 0x800C329C), "bbol_env_end_fx"),     # zGameLoop: iEnvEndRenderFX(env)
    (0x800702D0, ("bl", 0x800631EC), "bbol_pickup_check"),   # zEntPlayer_Update: pickups vs player
    (0x80134E08, ("bl", 0x8001F6E0), "bbol_taskbox_complete_event"),  # ztaskbox::complete: OnComplete
    # conversations a teammate's puppet would start in our game (bumping a villager, out of bounds hints)
    (0x80137580, ("bl", 0x80134AC8), "bbol_task_start_talk"),   # zNPCGoalTalk::Enter: ztaskbox::start_talk
    (0x80134BA0, ("bl", 0x80132890), "bbol_talk_start_talk"),   # ztaskbox::start_talk: ztalkbox::start_talk
    (0x80132A44, ("bl", 0x80132890), "bbol_talk_start_talk"),   # ztalkbox::start_talk(id)
    (0x80132A70, ("bl", 0x80132890), "bbol_talk_start_talk"),
    (0x8011C43C, ("bl", 0x80132A14), "bbol_talk_start_talk_id"),  # oob_state: tutorial talk
    (0x8009787C, ("bl", 0x800B2068), "bbol_scene_exit"),     # zGameExit: zSceneExit(1)
    (0x80097888, ("bl", 0x800B2068), "bbol_scene_exit"),     # zGameExit: zSceneExit(0)
    (0x800A60B4, ("bl", 0x800B2068), "bbol_scene_exit"),     # zMainLoop/menu: zSceneExit
]

# Plain instruction patches: (site, original, replacement, what).
RAW = [
]

# Function entries replaced with a plain branch to our code: (site, original, our function).
BRANCHES = [
    (0x80238FC4, 0x80A300A0, "bbol_rwfuo_stub"),   # RwFrameUpdateObjects: lwz r5,0xA0(r3)
    (0x802392E0, 0x9421FFF0, "bbol_rwg_translate"), # RwFrameTranslate: stwu r1,-0x10(r1)
    (0x8023937C, 0x9421FFF0, "bbol_rwg_transform"),
    (0x80239418, 0x9421FFF0, "bbol_rwg_rotate"),
    (0x80239078, 0x9421FFF0, "bbol_rwg_addchild"),
    (0x802394B4, 0x9421FFF0, "bbol_rwg_ortho"),
    (0x800F1D40, 0x9421FFD0, "bbol_givereward_stub"),  # zNPCCommon::GiveReward: stwu r1,-0x30(r1)
    (0x8007C55C, 0x9421FEE0, "bbol_colltrigger_stub"), # zEntPlayerCollTrigger: stwu r1,-0x120(r1)
]

DELUXE_DOL_SHA1 = "c70e94750896d16255308610f364a8998d67bdbe"
STOCK_DOL_SHA1 = "2ba9746ca162426c675dd48d869639f7b3923c33"


def bl(site, target):
    off = target - site
    assert -0x2000000 <= off < 0x2000000 and off % 4 == 0
    return 0x48000001 | (off & 0x03FFFFFC)


def zig(*args):
    cmd = [sys.executable, "-m", "ziglang", *args]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode:
        sys.stderr.write(r.stdout + r.stderr)
        raise SystemExit(f"failed: {' '.join(args[:2])}")
    return r


def build_blob():
    os.makedirs(OUT, exist_ok=True)
    cflags = ["-target", "powerpc-freestanding-eabihf", "-mcpu=750", "-O2", "-ffreestanding",
              "-fno-builtin", "-fno-pic", "-fno-stack-protector", "-fno-exceptions",
              "-fno-asynchronous-unwind-tables", "-Wall", "-Werror=implicit-function-declaration"]
    objs = []
    if os.environ.get("MINIMAL"):
        # test build: only the boot-scene hook, booting straight into $MINIMAL (e.g. B301)
        cflags.append("-DMINIMAL_BOOT=0x" + os.environ["MINIMAL"].encode().hex())
    for f in ("bbol.c", "objsync.c", "cutscene.c", "npcsync.c", "stubs.S"):
        o = os.path.join(OUT, f + ".o")
        zig("cc", *cflags, "-c", os.path.join(SRC, f), "-o", o)
        objs.append(o)

    ld = os.path.join(OUT, "link.ld")
    with open(ld, "w") as fh:
        fh.write("ENTRY(bbol_player_update)\nSECTIONS {\n")
        fh.write(f"  . = 0x{LOAD_ADDR:08X};\n")
        fh.write("  .bbol : { *(.mailbox) *(.text*) *(.rodata*) *(.sdata2*) *(.data*) *(.sdata*)"
                 " *(.sbss*) *(.bss*) *(COMMON) . = ALIGN(32); }\n")
        fh.write("  /DISCARD/ : { *(.comment) *(.eh_frame*) *(.note*) }\n}\n")
        for k, v in GAME_SYMBOLS.items():
            fh.write(f"{k} = 0x{v:08X};\n")
    elf = os.path.join(OUT, "bbol.elf")
    mapf = os.path.join(OUT, "bbol.map")
    zig("ld.lld", "-T", ld, *objs, "-o", elf, "-Map", mapf, "--no-undefined")
    blob = os.path.join(OUT, "bbol_patch.bin")
    zig("objcopy", "-O", "binary", "-j", ".bbol", elf, blob)

    syms = {}
    for line in open(mapf):
        m = re.match(r"\s*([0-9a-fA-F]{8})\s+[0-9a-fA-F]{8}\s+\w+\s+\d+\s+(\w+)\s*$", line)
        if m:
            syms[m.group(2)] = int(m.group(1), 16)
    data = open(blob, "rb").read()
    return data, syms


def check_no_sda(elf_text_dis):
    pass


def make_spec(blob, syms):
    end = (LOAD_ADDR + len(blob) + 31) & ~31
    hooks = []
    for site, orig, fn in (HOOKS if not os.environ.get("MINIMAL") else [h for h in HOOKS if h[2] == "bbol_boot_stub"]):
        if fn not in syms:
            raise SystemExit(f"missing symbol {fn}")
        want = bl(site, orig[1]) if isinstance(orig, tuple) else orig
        hooks.append({"addr": site, "expect": want, "write": bl(site, syms[fn]), "fn": fn})
    for site, e, fn in (BRANCHES if not os.environ.get('MINIMAL') else []):
        off = syms[fn] - site
        assert -0x2000000 <= off < 0x2000000
        hooks.append({"addr": site, "expect": e, "write": 0x48000000 | (off & 0x03FFFFFC), "fn": fn})
    for site, e, w, what in RAW:
        hooks.append({"addr": site, "expect": e, "write": w, "fn": what})
    hi, lo = end >> 16, end & 0xFFFF
    site, e1, e2 = ARENA_LO_SITE
    hooks.append({"addr": site, "expect": e1, "write": 0x3C600000 | hi, "fn": "arenaLo.hi"})
    hooks.append({"addr": site + 4, "expect": e2, "write": 0x60630000 | lo, "fn": "arenaLo.lo"})
    return {
        "name": "BFBB Online",
        "version": 1,
        "loadAddr": LOAD_ADDR,
        "size": len(blob),
        "arenaLo": end,
        "mailbox": syms["mb"],
        "sha1": hashlib.sha1(blob).hexdigest(),
        "baseDolSha1": [DELUXE_DOL_SHA1],
        "hooks": hooks,
        "blob": base64.b64encode(blob).decode(),
    }


# ---- DOL helpers (the launcher has a C# copy of this) ----------------------------------
def dol_apply(dol, spec):
    b = bytearray(dol)
    off = list(struct.unpack(">18I", b[0:72]))
    addr = list(struct.unpack(">18I", b[72:144]))
    size = list(struct.unpack(">18I", b[144:216]))

    def file_off(a):
        for i in range(18):
            if size[i] and addr[i] <= a < addr[i] + size[i]:
                return off[i] + a - addr[i]
        raise ValueError(hex(a))

    for h in spec["hooks"]:
        fo = file_off(h["addr"])
        cur = struct.unpack(">I", b[fo:fo + 4])[0]
        if cur != h["expect"]:
            raise SystemExit(f"{h['fn']}: {h['addr']:08X} is {cur:08X}, expected {h['expect']:08X}")
        b[fo:fo + 4] = struct.pack(">I", h["write"])
    slot = next(i for i in range(7) if size[i] == 0)
    blob = base64.b64decode(spec["blob"])
    while len(b) % 32:
        b.append(0)
    off[slot], addr[slot], size[slot] = len(b), spec["loadAddr"], len(blob)
    b += blob
    while len(b) % 32:
        b.append(0)
    b[0:72] = struct.pack(">18I", *off)
    b[72:144] = struct.pack(">18I", *addr)
    b[144:216] = struct.pack(">18I", *size)
    return bytes(b)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dol", help="Deluxe main.dol to patch into out/main.dol")
    a = ap.parse_args()
    blob, syms = build_blob()
    spec = make_spec(blob, syms)
    with open(os.path.join(OUT, "bbol_patch.json"), "w") as fh:
        json.dump(spec, fh, indent=1)
    print(f"blob {len(blob)} bytes at {LOAD_ADDR:08X}..{spec['arenaLo']:08X}, mailbox {spec['mailbox']:08X}")
    for k in sorted(syms, key=syms.get):
        if k.startswith("bbol_") or k in ("mb", "ctx", "tmp"):
            print(f"  {syms[k]:08X} {k}")
    if a.dol:
        dol = open(a.dol, "rb").read()
        sha = hashlib.sha1(dol).hexdigest()
        if sha not in spec["baseDolSha1"]:
            print(f"warning: base DOL sha1 {sha} is not the known Deluxe 1.1 DOL")
        out = dol_apply(dol, spec)
        with open(os.path.join(OUT, "main.dol"), "wb") as fh:
            fh.write(out)
        print("wrote out/main.dol", len(out))


if __name__ == "__main__":
    main()
