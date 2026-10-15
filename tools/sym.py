"""sym.py symbols.txt ADDR... : resolve GQPE78 addresses to function names (bfbbdecomp symbols)."""
import re, sys
syms = []
for l in open(sys.argv[1]):
    m = re.match(r"(\S+) = \.text:0x([0-9A-F]+); // type:function size:0x([0-9A-F]+)", l)
    if m: syms.append((int(m.group(2), 16), int(m.group(3), 16), m.group(1)))
for a in sys.argv[2:]:
    x = int(a, 16)
    print(a, next((f"{n}+0x{x - s:X}" for s, z, n in syms if s <= x < s + z), "?"))
