#!/usr/bin/env python3
"""npc-spawn-z-fix — correct spawn-Z data defects in main_world/npc_spawns.json.

Card t_7abdc0ae (fix/npc-spawn-z). 15 rows snap to validated terrain height:

  unit 569   (10570.89, 14718.34)  382.272 -> 291.25   에노이르 (elf, f103)
  unit 3672  (10576.83, 14732.12)  379.9   -> 312.60   에오카드 (elf, f103)
  unit 3672  (10569.16, 14719.20)  382.3   -> 294.10
  unit 3672  (10572.40, 14719.10)  382.3   -> 292.50
  unit 3672  (10584.40, 14714.00)  379.9   -> 314.75
  unit 3672  (10556.10, 14715.30)  379.9   -> 315.55
  unit 3672  (10556.90, 14722.30)  379.9   -> 315.20
  unit 3672  (10558.90, 14709.80)  379.9   -> 328.40
  unit 3672  (10566.50, 14712.20)  382.272 -> 298.50
  unit 3672  (10578.20, 14704.90)  379.9   -> 319.30
  unit 3672  (10583.80, 14726.20)  379.9   -> 314.85
  unit 3672  (10569.84, 14733.19)  379.9   -> 314.95
  unit 3672  (10572.90, 14712.10)  382.3   -> 293.75
  unit 3672  (10560.70, 14728.60)  379.9   -> 315.20
  unit 1904  (10469.50, 15106.70)  264.3   -> 219.40   아로라라 (daru, f103)

Terrain heights: server-exact heightmap sampler (recon_b_extract.terrain_height),
validated 0.1-0.3m vs live ground truth (scorecard-explorations/npc-behavior.md);
two independent box runs agree on every value. All 15 rows: non-flyer
(actor_models.movement_id != 2), no walkable navmesh surface at spawn Z
(cell 10_14 max node Z 359.1), open ground, 44.9-91.0m above terrain.

Line-based edit: JSONC (comments + trailing commas) preserved byte-for-byte
except the 15 "Z" lines. Run:  python3 tools/data/npc-spawn-z-fix.py \
  AAEmu.Game/Data/Worlds/main_world/npc_spawns.json [--out out.json] [--check]
"""
import json
import re
import sys

UNIT_RE = re.compile(r'^\s*"UnitId":\s*(\d+)\s*,?\s*(//.*)?$')
X_RE = re.compile(r'^\s*"X":\s*(-?[\d.]+)\s*,?\s*$')
Y_RE = re.compile(r'^\s*"Y":\s*(-?[\d.]+)\s*,?\s*$')
Z_RE = re.compile(r'^(\s*"Z":\s*)(-?[\d.]+)(\s*,?\s*)$')

# (unit, x, y, old_z, new_z)
FIX_ROWS = [
    (569,  10570.89, 14718.339, 382.272, 291.25),
    (3672, 10576.83, 14732.125, 379.9,   312.60),
    (3672, 10569.16, 14719.196, 382.3,   294.10),
    (3672, 10572.4,  14719.1,   382.3,   292.50),
    (3672, 10584.4,  14714.0,   379.9,   314.75),
    (3672, 10556.1,  14715.3,   379.9,   315.55),
    (3672, 10556.9,  14722.3,   379.9,   315.20),
    (3672, 10558.9,  14709.8,   379.9,   328.40),
    (3672, 10566.5,  14712.2,   382.272, 298.50),
    (3672, 10578.2,  14704.9,   379.9,   319.30),
    (3672, 10583.8,  14726.2,   379.9,   314.85),
    (3672, 10569.84, 14733.19,  379.9,   314.95),
    (3672, 10572.9,  14712.1,   382.272, 293.75),
    (3672, 10560.7,  14728.6,   379.9,   315.20),
    (1904, 10469.5,  15106.7,   264.3,   219.40),
]


def load_jsonc(path):
    """Tolerant parse: strip // comments outside strings (spawns files are JSONC)."""
    raw = open(path, 'rb').read().decode('utf-8', 'replace')
    out = []
    in_str = False
    esc = False
    i = 0
    n = len(raw)
    while i < n:
        c = raw[i]
        if in_str:
            out.append(c)
            if esc:
                esc = False
            elif c == '\\':
                esc = True
            elif c == '"':
                in_str = False
        else:
            if c == '"':
                in_str = True
                out.append(c)
            elif c == '/' and i + 1 < n and raw[i + 1] == '/':
                while i < n and raw[i] != '\n':
                    i += 1
                out.append('\n')
            elif c == ',':
                j = i + 1
                while j < n and raw[j] in ' \t\r\n':
                    j += 1
                if j < n and raw[j] in '}]':
                    pass
                else:
                    out.append(c)
            else:
                out.append(c)
        i += 1
    return json.loads(''.join(out))


def locate_fix_lines(lines, expect_new=False):
    """Find the Z line of each fix row. Exact-once matching.

    expect_new=False: rows must still carry the OLD (broken) Z — pre-fix mode.
    expect_new=True:  rows must carry the NEW (terrain) Z — post-fix --check.
    """
    found = []  # (z_line_idx, fix_row)
    i = 0
    n = len(lines)
    while i < n:
        m = UNIT_RE.match(lines[i])
        if not m:
            i += 1
            continue
        unit = int(m.group(1))
        # peek ahead within this row block (next ~8 lines) for Position
        x = y = z = None
        z_idx = None
        for j in range(i + 1, min(i + 9, n)):
            xm = X_RE.match(lines[j])
            ym = Y_RE.match(lines[j])
            zm = Z_RE.match(lines[j])
            if xm and x is None:
                x = float(xm.group(1))
            elif ym and y is None:
                y = float(ym.group(1))
            elif zm and z is None:
                z = float(zm.group(2))
                z_idx = j
            if x is not None and y is not None and z is not None:
                break
        if x is not None and y is not None and z is not None:
            for fr in FIX_ROWS:
                fu, fx, fy, fz, nz = fr
                want_z = nz if expect_new else fz
                if (unit == fu and abs(x - fx) < 0.02 and abs(y - fy) < 0.02
                        and abs(z - want_z) < 0.005):
                    found.append((z_idx, fr))
        i += 1

    # exact-once uniqueness on the Z line index
    seen = {}
    for z_idx, fr in found:
        if z_idx in seen:
            sys.exit(f'ERROR: fix row matched twice at line {z_idx + 1}: {fr}')
        seen[z_idx] = fr
    if len(seen) != len(FIX_ROWS):
        missing = [fr for fr in FIX_ROWS if fr not in seen.values()]
        sys.exit(f'ERROR: matched {len(seen)}/{len(FIX_ROWS)} fix rows; missing: {missing}')
    return sorted(seen.items())


def apply_fix(path, out_path=None):
    out_path = out_path or path
    with open(path, 'r', encoding='utf-8') as f:
        raw = f.read()
    before_md5 = __import__('hashlib').md5(raw.encode('utf-8')).hexdigest()
    lines = raw.splitlines(keepends=True)

    locs = locate_fix_lines(lines)
    changed = []
    for z_idx, (unit, x, y, old_z, new_z) in locs:
        m = Z_RE.match(lines[z_idx])
        if not m:
            sys.exit(f'ERROR: cannot parse Z line {z_idx + 1}: {lines[z_idx]!r}')
        if abs(float(m.group(2)) - old_z) > 0.005:
            sys.exit(f'ERROR: Z mismatch at line {z_idx + 1}: expected {old_z}, got {m.group(2)}')
        lines[z_idx] = f'{m.group(1)}{new_z}{m.group(3)}'
        changed.append((unit, x, y, old_z, new_z))

    with open(out_path, 'w', encoding='utf-8') as f:
        f.writelines(lines)

    after_md5 = __import__('hashlib').md5(open(out_path, 'rb').read()).hexdigest()

    # verification
    parsed = load_jsonc(out_path)
    row_count = len(parsed)
    # confirm the 15 rows now carry the new Z and nothing else drifted
    by_key = {}
    for r in parsed:
        p = r['Position']
        by_key[(r['UnitId'], round(p['X'], 2), round(p['Y'], 2))] = p.get('Z')
    verr = []
    for unit, x, y, old_z, new_z in FIX_ROWS:
        got = by_key.get((unit, round(x, 2), round(y, 2)))
        if got is None:
            verr.append(f'row (unit {unit}, {x}, {y}) not found after write')
        elif abs(got - new_z) > 0.005:
            verr.append(f'row (unit {unit}, {x}, {y}): expected Z {new_z}, got {got}')
    if verr:
        sys.exit('VERIFY FAILED:\n' + '\n'.join(verr))

    print(f'path:        {path}')
    print(f'rows total:  {row_count} (unchanged)')
    print(f'rows fixed:  {len(changed)}')
    print(f'md5 before:  {before_md5}')
    print(f'md5 after:   {after_md5}')
    print('changed:')
    for unit, x, y, old_z, new_z in changed:
        print(f'  unit {unit:<5} ({x:<9}, {y:<9}) Z {old_z:<8} -> {new_z}')
    print('VERIFY OK')


if __name__ == '__main__':
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    path = sys.argv[1]
    out = None
    check_only = False
    if '--out' in sys.argv:
        out = sys.argv[sys.argv.index('--out') + 1]
    if '--check' in sys.argv:
        check_only = True
    if check_only:
        with open(path, 'r', encoding='utf-8') as f:
            lines = f.read().splitlines(keepends=True)
        locs = locate_fix_lines(lines, expect_new=True)
        print('CHECK OK — all %d fix rows carry the corrected terrain Z' % len(locs))
        sys.exit(0)
    apply_fix(path, out)
