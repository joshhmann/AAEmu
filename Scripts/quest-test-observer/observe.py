"""One-bot adapter for existing passive AAEmu campaign snapshots. No gameplay calls."""
import hashlib
import base64
import json
import math
import re
from datetime import datetime
from pathlib import Path

SCHEMA = "aaemu.quest-observer/v1"


def timestamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def completed_flags(text, owner):
    """CharacterQuests.HasQuestCompleted: 64-bit blocks, BitArray byte order."""
    result = set()
    blocks = set()
    for line in text.splitlines():
        fields = line.split("\t")
        if len(fields) != 3:
            raise ValueError("Unreadable saved completed_quests row")
        player, block = map(int, fields[:2])
        body = bytes.fromhex(fields[2])
        if len(body) != 8 or block < 0:
            raise ValueError("Invalid completed quest block")
        if player != owner:
            continue
        if block in blocks:
            raise ValueError("Duplicate completed quest block")
        blocks.add(block)
        bits = int.from_bytes(body, "little")
        result.update(block * 64 + bit for bit in range(64) if bits & (1 << bit))
    return result


def active_quests(text, owner):
    result = set()
    for line in text.splitlines():
        fields = line.split("\t")
        if len(fields) != 4:
            raise ValueError("Unreadable saved quests row")
        player, quest, state = map(int, fields[:3])
        if player == owner and state in (1, 3):  # Progress, Ready; not Failed/Dropped.
            result.add(quest)
    return result


def inventory(text, owner, item):
    found = []
    for line in text.splitlines():
        fields = line.split("\t")
        if len(fields) != 3:
            raise ValueError("Expected existing collector's aggregate inventory rows")
        player, template, count = map(int, fields)
        if count < 0:
            raise ValueError("Negative item count")
        if player == owner and template == item:
            found.append(count)
    if len(found) > 1:
        raise ValueError("Duplicate aggregate item count")
    return found[0] if found else 0


class SnapshotAdapter:
    def __init__(self, manifest):
        self.manifest = manifest
        self.previous = None
        self.history_tail = set()
        self.terminals = {}
        self.live_attempts = set()
        self.last_time = None
        self.container = manifest.get("container")
        self.serial = 0
        self.hunt_actor = None
        self.hunt_count = None
        self.hunt_log_time = None
        self.hunt_log_tail = set()

    def event(self, kind, data, row, artifact, line, field):
        self.serial += 1
        m = self.manifest
        return {
            "schema": SCHEMA, "run_id": m["run_id"], "bot_id": m["bot_id"],
            "source_sha": m["source_sha"], "database_id": m["database_id"],
            "event_id": f"{m['run_id']}:{line}:{self.serial}",
            "timestamp": row.get("time") or m.get("started_utc"), "kind": kind, "data": data,
            "source": {"artifact": str(artifact), "line": line, "field": field,
                       "layer": m["evidence_layer"], "timestamp_kind": "first_observed_at"},
        }

    def hunt_progress(self, row, artifact, line):
        """Read existing native act counters; reference positions and kills are not credit."""
        objective = self.manifest["acceptance"].get("objective")
        if objective is None:
            return []
        logs = row.get("log_lines")
        cap = row.get("log_tail_limit")
        if (not isinstance(logs, list) or type(cap) is not int or cap <= 0
                or len(logs) >= cap or cap > 5000
                or row.get("sampled_log_lines") != len(logs)):
            raise ValueError("Missing or truncated native hunt log window")
        pattern = re.compile(r"QuestObjectiveTargetDiag cycle=(\S+) char=(\d+) actor=(\d+) quest=(\d+) objective=\[ok:act(\d+):huntx\d+\].*? have=(\d+) need=(\d+)\b")
        selected = []
        for index, raw in enumerate(logs):
            if not isinstance(raw, str):
                raise ValueError("Unreadable native log line")
            match = pattern.search(raw)
            if not match:
                owner = self.manifest["bot_id"]
                quest = self.manifest["acceptance"]["quest_id"]
                identity = rf"QuestObjectiveTargetDiag .*?char={owner} .*?quest={quest} objective=\[ok:act{objective['act_id']}:huntx"
                if re.search(identity, raw):
                    raise ValueError("Malformed native hunt counter for declared objective")
                continue
            cycle, owner, actor, quest, act, have, need = match.groups()
            if int(owner) != self.manifest["bot_id"] or int(quest) != self.manifest["acceptance"]["quest_id"] or int(act) != objective["act_id"]:
                continue
            native_at = datetime.fromisoformat(raw.split(" ", 1)[0].replace("Z", "+00:00"))
            if native_at.tzinfo is None:
                raise ValueError("Native hunt counter timestamp lacks timezone")
            at = native_at.timestamp()
            if at > timestamp(row["time"]):
                raise ValueError("Native hunt counter timestamp exceeds snapshot")
            digest = hashlib.sha256(raw.encode()).hexdigest()
            selected.append((at, index, digest, int(actor), int(have), cycle, raw))
        current_tail = set()
        events = []
        for at, index, digest, actor, count, cycle, raw in sorted(selected):
            current_tail.add(digest)
            if digest in self.hunt_log_tail:
                continue
            if actor <= 0 or (self.hunt_actor is not None and actor != self.hunt_actor):
                raise ValueError("Native hunt actor identity changed")
            if self.hunt_log_time is not None and at < self.hunt_log_time:
                raise ValueError("Native hunt log time regressed")
            if self.last_time is not None and at < self.last_time:
                raise ValueError("Native hunt log predates owned observation interval")
            self.hunt_actor = actor
            if self.hunt_count is not None and count < self.hunt_count:
                raise ValueError("Native hunt counter regressed")
            if self.hunt_count is not None and count > self.hunt_count:
                event = self.event("objective_progress", {
                    "quest_id": self.manifest["acceptance"]["quest_id"],
                    "act_id": objective["act_id"], "before": self.hunt_count,
                    "after": count, "authority": "native quest progress"}, row,
                    artifact, line, f"log_lines[{index}]")
                event["source"].update(native_timestamp=raw.split(" ", 1)[0],
                                       raw_sha256=digest, cycle_id=cycle, actor_id=actor)
                events.append(event)
            self.hunt_count = count
            self.hunt_log_time = at
        self.hunt_log_tail = current_tail  # Bounded to one collector window.
        return events

    def consume(self, row, artifact, line):
        events = []
        emit = lambda kind, data, field: events.append(self.event(kind, data, row, artifact, line, field))
        m = self.manifest
        try:
            now = timestamp(row["time"])
            if self.last_time is not None and (now <= self.last_time or
                    now - self.last_time > m["thresholds"]["max_gap_seconds"]):
                raise ValueError("Nonmonotone or missing snapshot interval")
            if row["source"] != m["source_sha"]:
                raise ValueError("Source SHA changed")
            container = row["container"]
            if self.container is None:
                self.container = container["id"]
            if container["id"] != self.container or not container["running"] or container["oom"] or container["restart_count"]:
                raise ValueError("Runtime identity/health changed")
            if row.get("critical") or row.get("error"):
                raise ValueError("Collector reported incomplete/critical telemetry")
            if row["status"]["Success"] is not True:
                raise ValueError("Status request failed")
            bots = [b for b in row["status"]["Bots"] if b["Id"] == m["bot_id"] and b["Name"] == m["bot_name"]]
            if len(bots) != 1 or bots[0]["State"] != "Active":
                raise ValueError("Owned active bot missing or ambiguous")
            brain = row["brains"][m["bot_name"]]
            if brain["CharacterId"] != m["bot_id"] or brain["Name"] != m["bot_name"]:
                raise ValueError("Brain belongs to another bot")
            history = brain["History"]
            if not isinstance(history, list):
                raise ValueError("Missing terminal history")
            current_tail = {r["TraceId"] for r in history}
            if self.history_tail and len(history) >= 20 and not current_tail.intersection(self.history_tail):
                raise ValueError("Rolling action history lost overlap")
            db = row["persisted_db"]
            flags = completed_flags(db["completed"], m["bot_id"])
            quests = active_quests(db["quests"], m["bot_id"])
            items = inventory(db["items"], m["bot_id"], m["acceptance"].get("item_id", 0))
            positions = [c for c in row["world_characters"] if int(c["Id"]) == m["bot_id"]]
            if len(positions) != 1:
                raise ValueError("Missing authoritative world position")
            position = [float(positions[0][k]) for k in ("X", "Y", "Z")]
            if not all(math.isfinite(v) for v in position):
                raise ValueError("Unreadable position")
            wake = brain.get("WakeSequence")
            if not isinstance(wake, int):
                wake = max((r["WakeSequence"] for r in history if isinstance(r.get("WakeSequence"), int)), default=None)
            emit("heartbeat", {"wake_sequence": wake, "active": True,
                               "current_goal": brain.get("LiveDecisionGoal"),
                               "quest_id": brain.get("LiveDecisionQuestId"),
                               "selection_reason": brain.get("Explanation"),
                               "action": brain.get("LiveAction"), "action_state": brain.get("LiveState")}, "brains/status")
            trace = brain.get("LiveTraceId")
            if trace and brain.get("LiveState") == "Running" and trace not in self.live_attempts:
                emit("action_attempt", {"trace_id": trace, "action": brain.get("LiveAction"),
                                        "quest_id": brain.get("LiveDecisionQuestId"),
                                        "reason": brain.get("LiveDetail"),
                                        "goal": brain.get("LiveDecisionGoal"),
                                        "policy": brain.get("LiveDecisionPolicy")}, "brains.LiveTraceId")
                self.live_attempts.add(trace)
            for r in history:
                record = {"trace_id": r["TraceId"], "action": r["Action"], "result": r["Result"],
                          "reason": r.get("Reason"), "quest_id": r.get("DecisionQuestId"),
                          "goal": r.get("Goal"), "policy": r.get("Policy")}
                if r["TraceId"] in self.terminals:
                    if self.terminals[r["TraceId"]] != record:
                        raise ValueError("Conflicting repeated terminal trace")
                    continue
                if r["Result"] not in ("Completed", "Rejected", "Failed", "TimedOut", "Interrupted", "Cancelled"):
                    raise ValueError("Unrecognized terminal result")
                if r["Action"] == "AcceptQuest" and r.get("Policy") == "quest-v1":
                    emit("goal_selected", {"quest_id": r.get("DecisionQuestId"), "trace_id": r["TraceId"],
                                           "policy": "quest-v1", "reason": r.get("Reason"),
                                           "independent": bool(m.get("choices_unassigned", False))}, "brains.History")
                reason = (r.get("Reason") or "").lower()
                if r["Result"] == "Rejected" and "busy" not in reason and any(
                        text in reason for text in ("permission", "prerequisite", "invalid", "unsupported")):
                    record["failure_class"] = "invalid"
                emit("action_result", record, "brains.History")
                self.terminals[r["TraceId"]] = {k: v for k, v in record.items() if k != "failure_class"}
            if self.previous is not None:
                old_flags, old_quests, old_items, old_position = self.previous
                if not old_flags.issubset(flags):
                    raise ValueError("Saved completed quest flags regressed")
                for quest in sorted(flags - old_flags):
                    emit("quest_completed", {"quest_id": quest, "before": False, "after": True,
                                              "authority": "saved completed_quests bit"}, "persisted_db.completed")
                for quest in sorted(quests - old_quests):
                    emit("quest_accepted", {"quest_id": quest, "before": False, "after": True,
                                             "authority": "saved quests row"}, "persisted_db.quests")
                if items != old_items:
                    emit("inventory_change", {"quest_id": m["acceptance"]["quest_id"],
                                              "item_id": m["acceptance"].get("item_id", 0),
                                              "before": old_items, "after": items}, "persisted_db.items")
                distance = math.dist(position, old_position)
                if distance > .01:
                    emit("movement", {"distance_m": distance, "position": position,
                                      "quest_id": brain.get("LiveDecisionQuestId")}, "world_characters")
            events.extend(self.hunt_progress(row, artifact, line))
            self.previous = flags, quests, items, position
            self.history_tail = current_tail
            self.last_time = now
        except (KeyError, TypeError, ValueError, OverflowError) as error:
            emit("telemetry_gap", {"reason": str(error)}, "snapshot")
        return events


def file_hash(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


class BridgeQuestAdapter(SnapshotAdapter):
    """Existing BotDriveBridge quest/observe copies native state without actor actions."""
    def __init__(self, manifest):
        super().__init__(manifest)
        self.actor_object = None
        self.generation = None
        self.audit_cursor = None

    def consume(self, row, artifact, line):
        request = row.get("request", {})
        if row.get("phase") != "OBSERVE":
            return []  # Preserve other run phases in raw evidence, not as bot progress.
        if not isinstance(request, dict):
            return [self.event("telemetry_gap", {"reason": "Native observation discriminator missing"},
                               {"time": row.get("recordedAtUtc")}, artifact, line, "request")]
        if request.get("cmd") != "quest" or request.get("sub") != "observe":
            if request.get("method") and request.get("path"):
                return []  # Explicit HTTP operation receipt, not native quest observation.
            return [self.event("telemetry_gap", {"reason": "Native observation request missing"},
                               {"time": row.get("recordedAtUtc")}, artifact, line, "request")]
        events = []
        m = self.manifest
        envelope = {"time": row.get("recordedAtUtc") or m.get("started_utc")}
        emit = lambda kind, data, field: events.append(self.event(kind, data, envelope, artifact, line, field))
        try:
            if m["acceptance"].get("objective") is not None:
                raise ValueError("Native audit lacks act-specific hunt counters; use owned snapshot log windows")
            o = row["output"]
            envelope["time"] = o["observedAtUtc"]
            now = timestamp(envelope["time"])
            if self.last_time is not None and (now <= self.last_time or now - self.last_time > m["thresholds"]["max_gap_seconds"]):
                raise ValueError("Native observation gap or backward time")
            if o["id"] != m["bot_id"] or o["name"] != m["bot_name"]:
                raise ValueError("Native observer belongs to another bot")
            audits = o["actorAudit"]
            warming = self.actor_object is None and o["actorCharId"] == 0 and o["actorObjId"] == 0 and not audits and not o.get("liveTraceId")
            if not warming:
                if o["actorCharId"] != m["bot_id"] or o["actorObjId"] <= 0:
                    raise ValueError("Native actor belongs to another bot")
                if self.actor_object is None:
                    self.actor_object, self.generation = o["actorObjId"], o["enrollmentGeneration"]
                if o["actorObjId"] != self.actor_object or o["enrollmentGeneration"] != self.generation:
                    raise ValueError("Actor or scheduler generation changed")
            if not isinstance(audits, list):
                raise ValueError("Native action audit missing")
            if self.audit_cursor and self.audit_cursor not in {r["trace_id"] for r in audits}:
                raise ValueError("Native action audit cursor lost overlap")
            flags = set()
            seen_blocks = set()
            for block in o["completedQuests"]:
                if block["owner"] != m["bot_id"] or block["blockId"] in seen_blocks:
                    raise ValueError("Native completion block identity invalid")
                seen_blocks.add(block["blockId"])
                body = base64.b64decode(block["data"], validate=True)
                if len(body) != 8:
                    raise ValueError("Native completion block unreadable")
                bits = int.from_bytes(body, "little")
                flags.update(block["blockId"] * 64 + bit for bit in range(64) if bits & (1 << bit))
            quests = {q["questId"] for q in o["activeQuests"] if q["status"] in ("Progress", "Ready")}
            items = sum(i["count"] for i in o["items"] if i["owner"] == m["bot_id"]
                        and i["template"] == m["acceptance"].get("item_id", 0))
            position = [float(o[k]) for k in ("x", "y", "z")]
            if not all(math.isfinite(v) for v in position) or items < 0:
                raise ValueError("Native position or inventory unreadable")
            emit("heartbeat", {"wake_sequence": o["maxTraceWakeSeq"],
                               "active": not warming and o["enrollmentState"] == "Active",
                               "scheduler_running": o["enrollmentSchedulerRunning"],
                               "current_goal": o.get("liveDecisionGoal"),
                               "quest_id": o.get("chosenQuestId"),
                               "selection_reason": o.get("questDecideDetail")}, "output.enrollment/maxTraceWakeSeq")
            trace = o.get("liveTraceId")
            if trace and o.get("liveState") == "Running" and trace not in self.live_attempts:
                emit("action_attempt", {"trace_id": trace, "action": o["liveAction"],
                                        "quest_id": o.get("liveDecisionQuestId"),
                                        "reason": o.get("liveDetail")}, "output.liveTraceId")
                self.live_attempts.add(trace)
            for r in audits:
                if r["actor_id"] != self.actor_object:
                    raise ValueError("Action audit belongs to another actor")
                if r["result"] in ("Requested", "Accepted", "Running"):
                    continue
                record = {"trace_id": r["trace_id"], "action": r["action"], "result": r["result"],
                          "quest_id": r.get("decision_quest_id"), "reason": r.get("detail"),
                          "goal": r.get("decision_goal"), "policy": r.get("decision_policy")}
                if r["trace_id"] in self.terminals:
                    if self.terminals[r["trace_id"]] != record:
                        raise ValueError("Native terminal trace changed")
                    continue
                if r["action"] == "AcceptQuest" and r.get("decision_policy") == "quest-v1":
                    emit("goal_selected", {"quest_id": r.get("decision_quest_id"), "trace_id": r["trace_id"],
                                           "reason": r.get("detail"), "policy": "quest-v1",
                                           "independent": bool(m.get("choices_unassigned", False))}, "output.actorAudit")
                emit("action_result", record, "output.actorAudit")
                self.terminals[r["trace_id"]] = record
            if self.previous is not None:
                old_flags, old_quests, old_items, old_position = self.previous
                if not old_flags.issubset(flags):
                    raise ValueError("Native completion flags regressed")
                for quest in sorted(flags - old_flags):
                    emit("quest_completed", {"quest_id": quest, "before": False, "after": True,
                                              "authority": "native CharacterQuests completion bit"}, "output.completedQuests")
                for quest in sorted(quests - old_quests):
                    emit("quest_accepted", {"quest_id": quest, "before": False, "after": True,
                                             "authority": "native CharacterQuests active row"}, "output.activeQuests")
                if items != old_items:
                    emit("inventory_change", {"quest_id": m["acceptance"]["quest_id"],
                                              "item_id": m["acceptance"].get("item_id", 0),
                                              "before": old_items, "after": items}, "output.items")
                distance = math.dist(position, old_position)
                if distance > .01:
                    emit("movement", {"distance_m": distance, "position": position,
                                      "quest_id": o.get("chosenQuestId")}, "output.x/y/z")
            self.previous = flags, quests, items, position
            self.last_time = now
            if audits:
                self.audit_cursor = audits[-1]["trace_id"]
        except (KeyError, TypeError, ValueError, OverflowError) as error:
            emit("telemetry_gap", {"reason": str(error)}, "output")
        return events
