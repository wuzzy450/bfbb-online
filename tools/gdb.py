"""Tiny GDB RSP client for Dolphin's stub. gdb.py go  (continue at boot)  |  gdb.py sample N  (N PC/LR samples)"""
import socket, sys, time

def connect():
    for _ in range(600):
        try:
            return socket.create_connection(("127.0.0.1", 2345), timeout=10)
        except OSError:
            time.sleep(0.5)
    raise SystemExit("no stub")

def send(s, pkt):
    cs = sum(pkt.encode()) & 0xFF
    s.sendall(f"${pkt}#{cs:02x}".encode())

def recv(s):
    buf = b""
    while True:
        c = s.recv(1)
        if not c: raise SystemExit("closed")
        if c == b"$":
            buf = b""
            while True:
                c = s.recv(1)
                if c == b"#": break
                buf += c
            s.recv(2); s.sendall(b"+")
            return buf.decode()

def regs(s):
    def p(n):
        send(s, f"p{n:x}"); return int(recv(s)[:8] or "0", 16)
    g = [p(i) for i in range(32)]
    return g, p(64), p(67)

def mem(s, addr, n):
    send(s, f"m{addr:x},{n:x}")
    for _ in range(4):
        r = recv(s)
        if len(r) == 2 * n:
            try: return bytes.fromhex(r)
            except ValueError: pass
        print("  (stray", r[:40], ")")
    raise IOError("no mem reply")

EXTRA = [0x803CB878, 0x803CB8A8, 0x803CAB40, 0x803CB7B0, 0x803D8AB4, 0x803CB7D0]
s = connect()
if sys.argv[1] == "go":
    s.sendall(b"+"); send(s, "?"); print(recv(s)); send(s, "c"); print("continued", flush=True)
    # then wait until the log says the hang happened and sample in this same connection
    import os
    log, pat = sys.argv[2], sys.argv[3]
    while not (os.path.exists(log) and pat in open(log, errors="ignore").read()): time.sleep(1)
    sys.argv = [sys.argv[0], "sample", "6"]
def stack(s, sp):
    st = []
    for _ in range(14):  # back chain: [sp]=caller sp, [caller sp+4]=return addr
        try:
            sp = int.from_bytes(mem(s, sp, 4), 'big')
            ret = int.from_bytes(mem(s, sp + 4, 4), 'big') if sp else 0
        except Exception: break
        if not sp: break
        st.append(f"{ret:08X}")
    return " ".join(st)

if sys.argv[1] == "watch":
    # gdb.py watch <log> <pattern> <ptr> <off> [want]: once <pattern> is in the log, watch writes to
    # [*ptr + off] (4 bytes) and report each write that leaves the value == want (default 0).
    import os
    log, pat, ptr, off = sys.argv[2], sys.argv[3], int(sys.argv[4], 16), int(sys.argv[5], 16)
    want = int(sys.argv[6], 16) if len(sys.argv) > 6 else 0
    s.sendall(b"+"); send(s, "?"); print(recv(s)); send(s, "c"); print("continued", flush=True)
    while not (os.path.exists(log) and pat in open(log, errors="ignore").read()): time.sleep(0.5)
    s.sendall(b""); print(recv(s))
    addr = int.from_bytes(mem(s, ptr, 4), 'big') + off
    send(s, f"Z2,{addr:x},4"); print("Z2", hex(addr), recv(s), flush=True)
    send(s, "c")
    s.settimeout(None)
    hits, t0 = 0, time.time()
    while time.time() - t0 < 900:
        t = recv(s)
        if not t.startswith("T"): continue
        kv = dict(x.split(':', 1) for x in t[3:].split(';') if ':' in x)
        v = int.from_bytes(mem(s, addr, 4), 'big')
        hits += 1
        if v == want:
            pc = int(kv.get('40', '0'), 16); sp = int(kv.get('01', '0'), 16)
            print(f"HIT {hits}: [{addr:08X}]={v:08X} PC {pc:08X} stack {stack(s, sp)}", flush=True)
        send(s, "c")
    sys.exit(0)
if True:
    for k in range(int(sys.argv[2])):
        s.sendall(b"\x03"); recv_last = recv(s); print(recv_last)
        t = recv_last; kv = dict(x.split(':') for x in t[3:].split(';') if x)
        pc = int(kv['40'],16); sp = int(kv['01'],16); g=[0]*32; lr=0
        st = []
        for _ in range(14):  # walk back chain: [sp]=caller sp, [caller sp+4]=return addr
            try:
                sp = int.from_bytes(mem(s, sp, 4), 'big')
                ret = int.from_bytes(mem(s, sp + 4, 4), 'big') if sp else 0
            except Exception: break
            if not sp: break
            st.append(f"{ret:08X}")
        try:
            sc = int.from_bytes(mem(s, 0x803C0558 + 0x1FC0, 4), 'big')
            sid = mem(s, sc, 4) if sc else b"----"
            print("sceneCur", hex(sc), sid, "startScene", mem(s, 0x803C0558 + 0x1FC4, 8) if False else "", "extra", [hex(int.from_bytes(mem(s, a, 4), 'big')) for a in EXTRA])
        except Exception as e: print("rd fail", e)
        print(f"PC {pc:08X} LR {lr:08X} r3 {g[3]:08X} r4 {g[4]:08X} stack {' '.join(st)}")
        send(s, "c"); time.sleep(2)


