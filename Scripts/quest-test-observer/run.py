#!/usr/bin/env python3
"""Observe existing snapshot files; this process never starts/stops AAEmu or a bot."""
import argparse
import hashlib
import json
import os
import signal
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from evaluate import Evaluator
from observe import BridgeQuestAdapter, SnapshotAdapter, file_hash

EXIT = {"PASS": 0, "FAIL": 1, "STALLED": 2, "INCONCLUSIVE": 3}


def utc():
    return datetime.now(timezone.utc).isoformat()


def process_start(pid):
    return Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]


def write_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def observe(args):
    os.umask(0o077)
    manifest = json.loads(Path(args.manifest).read_text())
    if manifest.get("collection_mode") not in ("historical-replay", "live-follow"):
        raise ValueError("Manifest must declare historical-replay or live-follow")
    if manifest["collection_mode"] == "live-follow" and not args.follow:
        raise ValueError("Live collection needs --follow")
    output = Path(args.out)
    output.mkdir(parents=True, exist_ok=False)
    raw = Path(args.snapshots).resolve()
    if not raw.is_file():
        raise ValueError("Existing raw snapshot file is required")
    input_format = manifest.get("input_format", "campaign-snapshots")
    if input_format not in ("bridge-quest-observe", "campaign-snapshots", "normalized-events"):
        raise ValueError("Unsupported existing snapshot/event format")
    adapter_type = BridgeQuestAdapter if input_format == "bridge-quest-observe" else SnapshotAdapter
    adapter, evaluator = adapter_type(manifest), Evaluator(manifest)
    started = time.monotonic()
    limits = {"max_seconds": args.seconds, "max_samples": args.max_samples,
              "max_bytes": args.max_bytes, "max_rss_mb": args.max_rss_mb}
    custody = {"pid": os.getpid(), "proc_start_ticks": process_start(os.getpid()),
               "started_utc": utc(), "source": str(raw), "limits": limits,
               "collector_control": "none", "manifest": manifest}
    write_json(output / "contract.json", custody)
    write_json(output / "manifest.json", manifest)
    line_number, byte_count, samples = 0, 0, 0
    reason, last_seen, pending = "source_eof", manifest["started_utc"], b""
    digest = hashlib.sha256()
    with raw.open("rb") as stream, (output / "events.jsonl").open("w") as event_file, \
            (output / "notifications.jsonl").open("w") as notices:
        opened = os.fstat(stream.fileno())
        while True:
            if time.monotonic() - started >= args.seconds:
                reason = "observer_deadline"
                break
            rss = int(Path("/proc/self/statm").read_text().split()[1]) * os.sysconf("SC_PAGE_SIZE")
            if rss > args.max_rss_mb * 1024 ** 2:
                reason = "observer_resource_cap"
                break
            chunk = stream.readline(args.max_bytes - byte_count + 1)
            if chunk:
                byte_count += len(chunk)
                digest.update(chunk)
                pending += chunk
                if byte_count > args.max_bytes:
                    reason = "raw_byte_cap"
                    break
                if not pending.endswith(b"\n"):
                    if not args.follow:
                        reason = "incomplete_raw_line"
                        break
                    continue
                line_number += 1
                try:
                    row = json.loads(pending)
                    if not isinstance(row, dict):
                        raise ValueError("Snapshot/event must be an object")
                    last_seen = row.get("time", last_seen)
                except (ValueError, UnicodeError):
                    row = {"time": last_seen}
                pending = b""
                events = [row] if input_format == "normalized-events" else adapter.consume(row, raw, line_number)
                if events:
                    last_seen = events[-1].get("timestamp", last_seen)
                for event in events:
                    event_file.write(json.dumps(event) + "\n")
                    for notice in evaluator.consume(event):
                        notices.write(json.dumps(notice) + "\n")
                        notices.flush()
                        print(json.dumps(notice), flush=True)
                        if args.request_stop_file and notice["code"] in ("REPEATED_INVALID", "STALL_THRESHOLD"):
                            try:
                                with Path(args.request_stop_file).open("x") as stop:
                                    json.dump(notice, stop)
                            except FileExistsError:
                                pass
                            reason = "cooperative_test_stop"
                event_file.flush()
                samples += 1
                if reason == "cooperative_test_stop":
                    break
                if samples >= args.max_samples:
                    reason = "sample_cap"
                    break
                if args.follow:
                    continue
            elif not args.follow or (args.stop_file and Path(args.stop_file).is_file()):
                reason = "producer_concluded" if args.follow else "source_eof"
                break
            else:
                time.sleep(.1)
    current = raw.stat()
    prefix_digest = hashlib.sha256()
    remaining = byte_count
    with raw.open("rb") as stream:
        while remaining:
            chunk = stream.read(min(remaining, 1024 * 1024))
            if not chunk:
                break
            prefix_digest.update(chunk)
            remaining -= len(chunk)
    raw_changed = ((opened.st_dev, opened.st_ino) != (current.st_dev, current.st_ino)
                   or remaining > 0 or prefix_digest.digest() != digest.digest())
    if reason in ("observer_resource_cap", "raw_byte_cap", "sample_cap", "incomplete_raw_line") or pending or raw_changed:
        evaluator.consume(adapter.event("telemetry_gap",
            {"reason": "Observer cap, partial line or raw source change: " + reason},
            {"time": last_seen}, raw, line_number + 1, "collection"))
    end = utc() if manifest["collection_mode"] == "live-follow" and reason == "observer_deadline" else last_seen
    report = evaluator.finish(reason, end)
    report["collection"] = {"raw_records": samples,
                            "observations": sum(e["kind"] == "heartbeat" for e in evaluator.events),
                            "bytes": byte_count,
                            "wall_seconds": time.monotonic() - started,
                            "raw_path": str(raw), "raw_sha256": file_hash(raw),
                            "raw_preserved": not raw_changed, "consumed_prefix_sha256": digest.hexdigest(),
                            "manifest_sha256": file_hash(args.manifest),
                            "controls_sent": 0,
                            "llm_calls_during_collection": 0}
    write_json(output / "verdict.json", report)
    with (output / "notifications.jsonl").open("a") as stream:
        stream.write(json.dumps(report["notifications"][-1]) + "\n")
    print(json.dumps({"status": report["status"], "report": str(output / "verdict.json"),
                      "samples": samples, "notifications": len(report["notifications"])}), flush=True)
    return EXIT[report["status"]]


def supervise(args):
    """An independent finite supervisor owns only its file-observer child."""
    os.umask(0o077)
    output = Path(args.out)
    output.parent.mkdir(parents=True, exist_ok=True)
    log_path = output.with_suffix(".supervisor.log")
    if output.exists() or log_path.exists() or output.with_suffix(".supervision.json").exists():
        raise ValueError("Existing observer custody/results must not be overwritten")
    command = [sys.executable, str(Path(__file__).resolve()), "observe",
               "--manifest", args.manifest, "--snapshots", args.snapshots, "--out", args.out,
               "--seconds", str(args.seconds), "--max-samples", str(args.max_samples),
               "--max-bytes", str(args.max_bytes), "--max-rss-mb", str(args.max_rss_mb)]
    if args.follow:
        command.append("--follow")
    if args.stop_file:
        command.extend(["--stop-file", args.stop_file])
    with log_path.open("xb") as log:
        child = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log,
                                 stderr=subprocess.STDOUT, start_new_session=True)
        identity = process_start(child.pid)
        receipt = {"supervisor_pid": os.getpid(), "supervisor_start_ticks": process_start(os.getpid()),
                   "child_pid": child.pid, "child_start_ticks": identity, "command": command,
                   "started_utc": utc(), "absolute_deadline_epoch": time.time() + args.seconds + 5,
                   "owns": "file observer only; no Login/Game/collector/bot process"}
        receipt_path = output.with_suffix(".supervision.json")
        write_json(receipt_path, receipt)
        try:
            code = child.wait(timeout=args.seconds + 5)
            receipt["forced_stop"] = False
        except subprocess.TimeoutExpired:
            if process_start(child.pid) != identity:
                raise RuntimeError("Observer child process identity changed; refuse signal")
            os.killpg(child.pid, signal.SIGTERM)
            try:
                code = child.wait(timeout=2)
            except subprocess.TimeoutExpired:
                if process_start(child.pid) != identity:
                    raise RuntimeError("Observer child identity changed; refuse hard kill")
                os.killpg(child.pid, signal.SIGKILL)
                code = child.wait(timeout=2)
            receipt["forced_stop"] = True
        receipt.update(exit_code=code, finished_utc=utc(), child_reaped=True)
        write_json(receipt_path, receipt)
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("observe", "supervise"))
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--snapshots", required=True)
    parser.add_argument("--out", required=True, help="New directory; existing results are never overwritten")
    parser.add_argument("--follow", action="store_true")
    parser.add_argument("--stop-file", help="Existing producer's end receipt, checked only at EOF")
    parser.add_argument("--request-stop-file", help="Request cooperative test cleanup on confirmed failure/stall")
    parser.add_argument("--seconds", type=float, default=60)
    parser.add_argument("--max-samples", type=int, default=2000)
    parser.add_argument("--max-bytes", type=int, default=128 * 1024 ** 2)
    parser.add_argument("--max-rss-mb", type=int, default=512)
    args = parser.parse_args()
    if not 0 < args.seconds <= 3600 or not 0 < args.max_samples <= 10000 or not 0 < args.max_bytes <= 512 * 1024 ** 2 or not 0 < args.max_rss_mb <= 2048:
        parser.error("Observer limits must be positive and within duration/sample/byte/RSS bounds")
    try:
        return supervise(args) if args.mode == "supervise" else observe(args)
    except (ValueError, KeyError, OSError) as error:
        print(f"Observer refused: {error}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
