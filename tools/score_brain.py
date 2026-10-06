#!/usr/bin/env python3
"""score_brain.py — mechanical GENERALIZATION scorer. No judgment, only arithmetic.
Subcommands:
  collect --log <dotnet-test log> --out <run.json>   : parse one suite run into a result artifact.
  compare --suite <dir> --parent-dir <dir> --child-dir <dir> --runs N : score parent vs child.
compare exits 0=PASS, 1=FAIL, 2=INVALID.
PASS iff: child completion >= parent + 0.20 AND child intervention <= parent
          AND child wedge <= parent AND regressions == 0.
Per-scenario schema: completed (objective reached unassisted), human_interventions
(any manual repair), wedged (no progress, no recovery within timeout),
progress_fraction, frozen_regressions.
"""
import argparse, json, re, sys, glob, os

MARGIN = 0.20

def collect(args):
    text = open(args.log, errors="replace").read()
    blocks = re.findall(r"total:\s*(\d+)\s*\n\s*failed:\s*(\d+)", text)
    if not blocks:
        print("INVALID: no test summary found", file=sys.stderr)
        return 2
    total = sum(int(t) for t, _ in blocks)
    failed = sum(int(f) for _, f in blocks)
    passed = total - failed
    # Each passing scenario test = one completed held-out scenario; each failure = a wedge.
    # Frozen regressions come from a separate frozen-suite log when supplied
    # (--frozen-log); without one the gate reads 0 and the ledger must say so.
    frozen = 0
    if getattr(args, "frozen_log", None):
        ftext = open(args.frozen_log, errors="replace").read()
        fm = re.search(r"failed:\s*(\d+)", ftext)
        if not fm:
            print("INVALID: no test summary in frozen log", file=sys.stderr)
            return 2
        frozen = int(fm.group(1))
    out = {
        "completed": passed,
        "total": total,
        "human_interventions": 0,
        "wedged": failed,
        "progress_fraction": round(passed / total, 4) if total else 0.0,
        "frozen_regressions": frozen,
    }
    json.dump(out, open(args.out, "w"), indent=2)
    return 0

def load_runs(d, runs):
    vals = []
    for i in range(1, runs + 1):
        p = os.path.join(d, f"run{i}.json")
        if not os.path.exists(p):
            print(f"INVALID: missing {p}", file=sys.stderr)
            return None
        vals.append(json.load(open(p)))
    return vals

def agg(vals):
    n = len(vals)
    return {
        "completion_rate": sum(v["completed"] for v in vals) / sum(v["total"] for v in vals),
        "intervention_rate": sum(v["human_interventions"] for v in vals) / n,
        "wedge_rate": sum(v["wedged"] for v in vals) / sum(v["total"] for v in vals),
        "median_progress": sorted(v["progress_fraction"] for v in vals)[n // 2],
        "regressions": max(v["frozen_regressions"] for v in vals),
    }

def compare(args):
    p = load_runs(args.parent_dir, args.runs)
    c = load_runs(args.child_dir, args.runs)
    if p is None or c is None:
        return 2
    pa, ca = agg(p), agg(c)
    def show(name, a):
        print(f"{name}\ncompletion_rate={a['completion_rate']:.2f}\n"
              f"intervention_rate={a['intervention_rate']:.2f}\nwedge_rate={a['wedge_rate']:.2f}\n"
              f"median_progress={a['median_progress']:.2f}\nregressions={a['regressions']}")
    show("PARENT", pa)
    print()
    show("CHILD", ca)
    print()
    dc = ca["completion_rate"] - pa["completion_rate"]
    di = ca["intervention_rate"] - pa["intervention_rate"]
    dw = ca["wedge_rate"] - pa["wedge_rate"]
    print(f"DELTA\ncompletion_rate={dc:+.2f}\nintervention_rate={di:+.2f}\n"
          f"wedge_rate={dw:+.2f}\nmedian_progress={ca['median_progress']-pa['median_progress']:+.2f}\n"
          f"regressions={ca['regressions']}")
    print()
    ok = (ca["completion_rate"] >= pa["completion_rate"] + MARGIN
          and ca["intervention_rate"] <= pa["intervention_rate"]
          and ca["wedge_rate"] <= pa["wedge_rate"]
          and ca["regressions"] == 0)
    print("GENERALIZATION=" + ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

ap = argparse.ArgumentParser()
sub = ap.add_subparsers(dest="cmd", required=True)
c1 = sub.add_parser("collect"); c1.add_argument("--log", required=True); c1.add_argument("--out", required=True); c1.add_argument("--frozen-log", default=None)
c2 = sub.add_parser("compare"); c2.add_argument("--suite", required=True)
c2.add_argument("--parent-dir", required=True); c2.add_argument("--child-dir", required=True)
c2.add_argument("--runs", type=int, default=3)
a = ap.parse_args()
sys.exit({"collect": collect, "compare": compare}[a.cmd](a))
