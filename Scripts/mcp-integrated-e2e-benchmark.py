#!/usr/bin/env python3
"""Bounded, client-neutral MCP + Game WebApi benchmark.

The Login/Game stack is deliberately an external prerequisite. This driver
spawns both generic stdio sidecars, records every JSON-RPC call/reply, and
cross-checks the action against the existing BotDriveBridge when available.
It never writes gameplay state or logs the token.
"""
import argparse
import json
import os
import selectors
import socket
import subprocess
import time
from pathlib import Path

TERMINAL = {"Completed", "Failed", "Rejected", "Interrupted", "TimedOut"}


class Sidecar:
    def __init__(self, root, project, label, env, transcript):
        self.label = label
        self.transcript = transcript
        self.process = subprocess.Popen(
            ["dotnet", "run", "--no-restore", "--project", str(root / project), "--no-launch-profile"],
            cwd=root,
            env=env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )
        self.selector = selectors.DefaultSelector()
        self.selector.register(self.process.stdout, selectors.EVENT_READ)

    def call(self, request, expect=True):
        self._record("->", request)
        self.process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        if not expect:
            return None
        if not self.selector.select(timeout=30):
            raise TimeoutError(f"{self.label} response timeout")
        line = self.process.stdout.readline().strip()
        if not line:
            stderr = self.process.stderr.read().strip()
            raise RuntimeError(f"{self.label} exited without JSON: {stderr}")
        reply = json.loads(line)
        self._record("<-", reply)
        return reply

    def _record(self, direction, value):
        encoded = json.dumps(value, separators=(",", ":"))
        with self.transcript.open("a", encoding="utf-8") as stream:
            stream.write(f"{self.label} {direction} {encoded}\n")

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()


def tool(sidecar, request_id, name, arguments):
    return sidecar.call({
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "tools/call",
        "params": {"name": name, "arguments": arguments},
    })


def text_result(reply):
    if "error" in reply:
        return None
    return json.loads(reply["result"]["content"][0]["text"])


def wait_terminal(sidecar, next_id, trace_id, interval, attempts):
    last_reply = None
    for _ in range(attempts):
        last_reply = tool(sidecar, next_id, "action_status", {"traceId": trace_id})
        next_id += 1
        payload = text_result(last_reply)
        if payload and payload.get("state") in TERMINAL:
            return last_reply, payload
        time.sleep(interval)
    raise TimeoutError(f"trace {trace_id} did not reach a terminal state in {attempts} status calls")


def bridge_char_pos(port, bot, transcript):
    request = {"cmd": "drive", "bot": bot, "op": "charPos"}
    with socket.create_connection(("127.0.0.1", port), timeout=10) as connection:
        wire = json.dumps(request, separators=(",", ":")) + "\n"
        connection.sendall(wire.encode())
        data = b""
        while not data.endswith(b"\n"):
            chunk = connection.recv(65536)
            if not chunk:
                break
            data += chunk
    with transcript.open("a", encoding="utf-8") as stream:
        stream.write(f"bridge -> {wire}")
        stream.write(f"bridge <- {data.decode().rstrip(chr(10))}\n")
    return json.loads(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bot", default=os.getenv("AAEMU_MCP_BOT", "McpIntegrated01"))
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--transcript", type=Path, default=Path("mcp-integrated-transcript.jsonl"))
    parser.add_argument("--bridge-port", type=int, default=int(os.getenv("E2E_BRIDGE_PORT", "1260")))
    parser.add_argument("--skip-bridge", action="store_true")
    args = parser.parse_args()
    transcript = args.transcript.resolve()
    transcript.parent.mkdir(parents=True, exist_ok=True)
    transcript.unlink(missing_ok=True)
    env = dict(os.environ)
    env.setdefault("AAEMU_BOT_CTRL_URL", "http://127.0.0.1:1280")
    if not env.get("AAEMU_BOT_CTRL_TOKEN"):
        parser.error("AAEMU_BOT_CTRL_TOKEN is required and must be supplied out-of-band")

    management = actions = None
    try:
        management = Sidecar(args.root, "AAEmu.BotControl", "management", env, transcript)
        management.call({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}})
        management.call({"jsonrpc": "2.0", "method": "notifications/initialized"}, expect=False)
        management.call({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        tool(management, 3, "bot_status", {})
        tool(management, 4, "bot_list", {})
        tool(management, 5, "bot_add", {"name": args.bot})
        tool(management, 6, "bot_add", {"name": args.bot})
        tool(management, 7, "bot_status", {})
        tool(management, 8, "bot_list", {})

        actions = Sidecar(args.root, "AAEmu.BotControlMcp", "actions", env, transcript)
        actions.call({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}})
        actions.call({"jsonrpc": "2.0", "method": "notifications/initialized"}, expect=False)
        actions.call({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        observe_ack = tool(actions, 3, "observe", {"bot": args.bot})
        observe = text_result(observe_ack)
        _, observed = wait_terminal(actions, 4, observe["trace_id"], 1.0, 12)
        actions.call({"jsonrpc": "2.0", "id": 20, "method": "tools/call", "params": {
            "name": "trace", "arguments": {"bot": args.bot, "limit": 10}}})

        position = observed["result_payload"]["Position"]
        move_args = {
            "bot": args.bot,
            "x": round(float(position["X"]) + 2.0, 1),
            "y": round(float(position["Y"]), 1),
            "z": round(float(position["Z"]), 1),
            "speed": 1.0,
            "timeoutSec": 8,
        }
        move_ack = tool(actions, 21, "move", move_args)
        move = text_result(move_ack)
        if move and move.get("trace_id"):
            wait_terminal(actions, 22, move["trace_id"], 1.0, 12)
            actions.call({"jsonrpc": "2.0", "id": 40, "method": "tools/call", "params": {
                "name": "trace", "arguments": {"bot": args.bot, "limit": 20}}})
        bridge_reply = None
        if not args.skip_bridge:
            try:
                bridge_reply = bridge_char_pos(args.bridge_port, args.bot, transcript)
            except Exception as error:
                bridge_reply = {"error": str(error)}
                with transcript.open("a", encoding="utf-8") as stream:
                    stream.write(f"bridge-error <- {json.dumps(bridge_reply, separators=(',', ':'))}\n")
        print(json.dumps({"bot": args.bot, "observe_trace": observe["trace_id"],
                          "move_trace": move.get("trace_id") if move else None,
                          "bridge": bridge_reply, "transcript": str(transcript)}))
    finally:
        if actions:
            actions.close()
        if management:
            management.close()


if __name__ == "__main__":
    main()
