"""Read-only, bounded engineering hypotheses over existing normalized evidence.

The first discrepancy is an observation, not a causal finding. Action terminals
are never substituted for authoritative quest effects.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from datetime import datetime, timezone
from pathlib import Path

from evaluate import KINDS, SCHEMA as EVENT_SCHEMA

SCHEMA = "aaemu-jev-triage-v1"
MAX_PACKET_BYTES = 16384
MAX_EVENT_BYTES = 64 * 1024 * 1024
MAX_EVENTS = 100000
MAX_LINE_BYTES = 1024 * 1024
IDENTITY = ("run_id", "bot_id", "source_sha", "database_id")
TERMINALS = {"Completed", "Rejected", "Failed", "TimedOut", "Interrupted", "Cancelled"}


def encode(packet):
    return json.dumps(packet, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")


def _json(raw):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate JSON key")
            result[key] = value
        return result

    def invalid_constant(value):
        raise ValueError("nonfinite JSON number")

    return json.loads(raw, object_pairs_hook=unique, parse_constant=invalid_constant)


def _time(value):
    if not isinstance(value, str):
        raise ValueError("missing event timestamp")
    value = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if value.tzinfo is None:
        raise ValueError("event timestamp lacks timezone")
    return value.astimezone(timezone.utc)


def _integer(value, minimum=0):
    return type(value) is int and value >= minimum


def _text(value):
    return isinstance(value, str) and bool(value)


def _validate(event, identity, layer):
    if not isinstance(event, dict) or event.get("schema") != EVENT_SCHEMA:
        raise ValueError("unsupported normalized event schema")
    if any(type(event.get(key)) is not type(value) or event.get(key) != value
           for key, value in identity.items()):
        raise ValueError("event identity differs from verdict")
    if not _text(event.get("event_id")) or event.get("kind") not in KINDS:
        raise ValueError("missing event ID or unsupported event kind")
    source, data = event.get("source"), event.get("data")
    if (not isinstance(source, dict) or not _text(source.get("artifact"))
            or not _integer(source.get("line"), 1) or not _text(source.get("field"))
            or source.get("layer") != layer or not isinstance(data, dict)):
        raise ValueError("missing or conflicting exact source reference")
    at = _time(event.get("timestamp"))
    kind = event["kind"]
    if kind == "heartbeat":
        if ("wake_sequence" not in data or type(data.get("active")) is not bool
                or (data.get("wake_sequence") is not None and not _integer(data["wake_sequence"]))
                or (data.get("scheduler_running") is not None and type(data["scheduler_running"]) is not bool)):
            raise ValueError("invalid heartbeat evidence")
    elif kind in ("action_attempt", "action_result", "goal_selected"):
        if not _text(data.get("trace_id")):
            raise ValueError("missing exact action trace")
        if kind == "goal_selected":
            if (not _integer(data.get("quest_id"), 1) or type(data.get("independent")) is not bool
                    or not _text(data.get("reason")) or not _text(data.get("policy"))):
                raise ValueError("invalid goal selection evidence")
        elif (not _text(data.get("action"))
              or (data.get("quest_id") is not None and not _integer(data["quest_id"]))
              or (kind == "action_result" and data.get("result") not in TERMINALS)):
            raise ValueError("invalid action evidence")
    elif kind in ("inventory_change", "objective_progress", "quest_completed", "quest_accepted"):
        if not _integer(data.get("quest_id"), 1):
            raise ValueError("invalid authoritative quest identity")
        if kind in ("inventory_change", "objective_progress"):
            if not all(_integer(data.get(key)) for key in ("before", "after")):
                raise ValueError("invalid authoritative progress count")
            if kind == "inventory_change" and not _integer(data.get("item_id"), 1):
                raise ValueError("invalid inventory identity")
            if kind == "objective_progress":
                if (not _integer(data.get("act_id"), 1) or data.get("authority") != "native quest progress"
                        or data["after"] < data["before"]):
                    raise ValueError("unsupported objective authority or regressing progress")
                if _time(source.get("native_timestamp", event["timestamp"])) > at:
                    raise ValueError("objective credit timestamp exceeds observation")
        else:
            authorities = ({"saved completed_quests bit", "native CharacterQuests completion bit"}
                           if kind == "quest_completed" else
                           {"saved quests row", "native CharacterQuests active row"})
            if data.get("before") is not False or data.get("after") is not True or data.get("authority") not in authorities:
                raise ValueError("unsupported authoritative quest effect")
    elif kind == "movement":
        distance = data.get("distance_m")
        if type(distance) not in (int, float) or not math.isfinite(distance) or distance < 0:
            raise ValueError("invalid movement evidence")
    elif kind == "telemetry_gap" and not _text(data.get("reason")):
        raise ValueError("missing telemetry fault detail")
    return at


def _read_events(path, identity, layer):
    events, ids, attempts, terminals, counts = [], set(), {}, {}, {}
    digest = hashlib.sha256()
    total, size, last, error, bounds = 0, 0, None, None, []
    complete = False
    with Path(path).open("rb") as stream:
        before = os.fstat(stream.fileno())
        while True:
            raw = stream.readline(MAX_LINE_BYTES + 1)
            if not raw:
                complete = True
                break
            size += len(raw)
            digest.update(raw)
            total += 1
            if len(raw) > MAX_LINE_BYTES or size > MAX_EVENT_BYTES or total > MAX_EVENTS:
                bounds.append("event_line_bytes" if len(raw) > MAX_LINE_BYTES else
                              "event_bytes" if size > MAX_EVENT_BYTES else "event_rows")
                error = error or "event input exceeds declared streaming bounds; complete validation unavailable"
                break
            try:
                if not raw.endswith(b"\n"):
                    raise ValueError("incomplete normalized event line")
                event = _json(raw)
                at = _validate(event, identity, layer)
                if event["event_id"] in ids:
                    raise ValueError("duplicate normalized event ID")
                ids.add(event["event_id"])
                if last is not None and at < last:
                    raise ValueError("normalized event chronology regressed")
                last = at
                kind, data = event["kind"], event["data"]
                if kind in ("action_attempt", "action_result"):
                    trace = data["trace_id"]
                    binding = (data["action"], data.get("quest_id") or 0)
                    if trace in attempts and attempts[trace] != binding:
                        raise ValueError("conflicting trace action/quest binding")
                    attempts[trace] = binding
                    if kind == "action_result":
                        signature = (binding, data["result"], data.get("reason"), data.get("failure_class"))
                        if trace in terminals and terminals[trace] != signature:
                            raise ValueError("contradictory terminal trace")
                        terminals[trace] = signature
                if kind == "objective_progress":
                    key = (data["quest_id"], data["act_id"])
                    if key in counts and counts[key] != data["before"]:
                        raise ValueError("objective progress continuity lost")
                    counts[key] = data["after"]
                events.append(event)
            except (ValueError, TypeError, KeyError, OverflowError, RecursionError) as exc:
                error = error or str(exc)
        after = os.fstat(stream.fileno())
        named = Path(path).stat()
        signature = lambda value: (value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns)
        if signature(before) != signature(after) or signature(after) != signature(named):
            error = error or "event evidence changed during validation"
    return events, total, digest.hexdigest() if complete else None, error, bounds, complete


def _references(report, by_id):
    refs = set()
    for field in ("failure_or_stall_evidence", "relevant_events"):
        values = report.get(field, [])
        if not isinstance(values, list) or any(not _text(value) for value in values):
            raise ValueError("invalid verdict evidence references")
        refs.update(values)
    for field in ("confirmed_actions", "failure_details", "unconfirmed_attempts", "confirmed_objective_progress", "notifications"):
        values = report.get(field, [])
        if not isinstance(values, list):
            raise ValueError("invalid verdict evidence detail list")
        for value in values:
            if not isinstance(value, dict):
                raise ValueError("invalid verdict evidence detail")
            ref = value.get("event_id")
            if ref is not None:
                if not _text(ref):
                    raise ValueError("invalid verdict detail reference")
                refs.add(ref)
                event = by_id.get(ref)
                if event is not None and field != "notifications":
                    expected_kind = ("action_attempt" if field == "unconfirmed_attempts" else
                                     "objective_progress" if field == "confirmed_objective_progress" else "action_result")
                    if event["kind"] != expected_kind:
                        raise ValueError("verdict detail refers to the wrong evidence kind")
                    for key in ("action", "quest_id", "result", "reason", "trace_id"):
                        if key in value and value[key] != event["data"].get(key):
                            raise ValueError("verdict detail contradicts normalized evidence")
                    if "seen_at" in value and value["seen_at"] != event["timestamp"]:
                        raise ValueError("verdict detail timestamp contradicts evidence")
                    if field == "confirmed_actions" and event["data"]["result"] != "Completed":
                        raise ValueError("confirmed action is not a Completed terminal")
                    if field == "failure_details" and event["data"]["result"] == "Completed":
                        raise ValueError("failure detail promotes a Completed terminal to failure")
                    if field == "confirmed_objective_progress" and any(value.get(key) != event[key] for key in ("timestamp", "source", "data")):
                        raise ValueError("objective detail contradicts exact source evidence")
    criteria = report.get("criterion_evidence", {})
    if not isinstance(criteria, dict):
        raise ValueError("invalid criterion evidence references")
    for value in criteria.values():
        values = value if isinstance(value, list) else [] if value is None else [value]
        if any(not _text(ref) for ref in values):
            raise ValueError("invalid criterion reference")
        refs.update(values)
    return refs


def _check_criteria(report, by_id):
    criteria, evidence, contract = report.get("criteria"), report.get("criterion_evidence", {}), report.get("acceptance_contract")
    if (not isinstance(criteria, dict) or not criteria or any(type(value) is not bool for value in criteria.values())
            or not isinstance(contract, dict) or not _integer(contract.get("quest_id"), 1)):
        raise ValueError("missing essential acceptance evidence")
    quest = contract["quest_id"]
    expected = {"turn_in_terminal": "action_result", "authoritative_completion": "quest_completed",
                "independent_followup": "quest_accepted", "hunt_progress": "objective_progress"}
    if set(criteria) - (set(expected) | {"item_progress"}):
        raise ValueError("unsupported acceptance criterion")
    required = {"turn_in_terminal", "authoritative_completion", "independent_followup",
                "hunt_progress" if contract.get("objective") is not None else "item_progress"}
    if set(criteria) != required or contract.get("require_followup") is not True or contract.get("independent_selection") is not True:
        raise ValueError("missing or unsupported acceptance contract")
    objective = contract.get("objective")
    if objective is not None:
        if (not isinstance(objective, dict) or objective.get("mode") != "native_hunt"
                or not _integer(objective.get("act_id"), 1) or not _integer(objective.get("required_progress"), 1)):
            raise ValueError("unsupported native objective acceptance contract")
    elif not _integer(contract.get("item_id"), 1) or not _integer(contract.get("item_count"), 1):
        raise ValueError("missing inventory acceptance contract")
    for key, met in criteria.items():
        if not met:
            if evidence.get(key):
                raise ValueError("criterion result conflicts with its evidence")
            continue
        if key == "item_progress":
            if not any(e["kind"] == "inventory_change" and e["data"]["quest_id"] == quest
                       and e["data"]["item_id"] == contract.get("item_id")
                       and e["data"]["after"] > e["data"]["before"]
                       and e["data"]["after"] >= contract.get("item_count", math.inf) for e in by_id.values()):
                raise ValueError("item criterion lacks authoritative inventory evidence")
            continue
        refs = evidence.get(key)
        refs = refs if isinstance(refs, list) else [refs] if refs else []
        selected = [by_id[ref] for ref in refs]
        allowed = {"quest_accepted", "quest_completed"} if key == "independent_followup" else {expected[key]}
        if not selected or not any(e["kind"] in allowed for e in selected):
            raise ValueError("criterion lacks supporting authoritative event")
        if key == "turn_in_terminal" and not all(e["kind"] == "action_result" and e["data"].get("action") == "TurnInQuest"
                                                 and e["data"].get("result") == "Completed" and e["data"].get("quest_id") == quest for e in selected):
            raise ValueError("turn-in criterion conflicts with terminal evidence")
        if key == "authoritative_completion" and not all(e["kind"] == "quest_completed" and e["data"]["quest_id"] == quest for e in selected):
            raise ValueError("completion criterion conflicts with authoritative identity")
        if key == "independent_followup":
            goals = [e for e in selected if e["kind"] == "goal_selected" and e["data"].get("independent") is True and e["data"].get("policy") == "quest-v1"]
            results = [e for e in selected if e["kind"] == "action_result" and e["data"].get("action") == "AcceptQuest" and e["data"].get("result") == "Completed"]
            accepted = [e for e in selected if e["kind"] in ("quest_accepted", "quest_completed") and e["data"]["quest_id"] != quest]
            turn_ref = evidence.get("turn_in_terminal")
            turn = by_id.get(turn_ref) if isinstance(turn_ref, str) else None
            completion_ref = evidence.get("authoritative_completion")
            if not turn or not isinstance(completion_ref, str) or completion_ref not in by_id:
                raise ValueError("followup lacks preceding quest completion proof")
            if not any(g["data"]["trace_id"] == r["data"]["trace_id"] and g["data"]["quest_id"] == r["data"].get("quest_id") == a["data"]["quest_id"]
                       and _time(g["timestamp"]) > _time(turn["timestamp"])
                       and _time(r["timestamp"]) >= _time(g["timestamp"])
                       and _time(a["timestamp"]) >= _time(g["timestamp"])
                       for g in goals for r in results for a in accepted):
                raise ValueError("followup criterion lacks independent terminal and authoritative effect")
        if key == "hunt_progress":
            objective = contract.get("objective", {})
            if not isinstance(objective, dict) or not any(e["kind"] == "objective_progress" and e["data"]["quest_id"] == quest
                    and e["data"]["act_id"] == objective.get("act_id") and e["data"]["after"] > e["data"]["before"]
                    and e["data"]["after"] >= objective.get("required_progress", math.inf) for e in selected):
                raise ValueError("hunt criterion conflicts with native act evidence")
            credit = next(e for e in selected if e["kind"] == "objective_progress")
            credited_at = _time(credit["source"].get("native_timestamp", credit["timestamp"]))
            for proof in ("turn_in_terminal", "authoritative_completion"):
                ref = evidence.get(proof)
                if isinstance(ref, str) and ref in by_id and credited_at > _time(by_id[ref]["timestamp"]):
                    raise ValueError("hunt credit belongs after this completion episode")
    if report.get("status") == "PASS" and not all(criteria.values()):
        raise ValueError("PASS conflicts with unsatisfied acceptance criteria")


def _hypotheses(packet):
    candidates = [{"id": "unknown", "summary": "The sampled evidence does not distinguish a cause.",
                   "supporting_event_ids": [], "missing_evidence": ["A distinguishing source-bound observation."],
                   "check": "Read the preserved source at the selected event references and compare the competing checks; make no gameplay change."}]

    def add(identifier, summary, events, missing, check):
        candidates.append({"id": identifier, "summary": summary, "supporting_event_ids": [e["event_id"] for e in events],
                           "missing_evidence": missing, "check": check})

    discrepancy = packet["first_discrepancy"]
    events = packet["events"]
    if discrepancy:
        fault = next(e for e in events if e["event_id"] == discrepancy["event_id"])
        if fault["kind"] == "action_result":
            correlated = [e for e in events if e["data"].get("trace_id") == fault["data"]["trace_id"]]
            add("request_validation", "The observed noncompleted terminal may reflect request rejection or validation failure, not a proven world-state fault.", correlated,
                ["The validation branch and request arguments for this exact trace."],
                "Read the exact terminal/attempt source fields for this trace and compare its reason and arguments with the existing action validator's rejection conditions.")
            add("binding_context", "The same terminal may instead reflect stale or invalid quest/actor binding at submission.", correlated,
                ["Actor/enrollment and quest binding at the exact submission time."],
                "Read preserved enrollment, actor identity and quest state around this trace; compare them with the request binding without submitting another action.")
            if any(word in fault["data"]["action"].lower() for word in ("move", "travel")):
                add("travel_execution", "The sampled travel terminal may reflect travel execution failure rather than missing target context.", correlated,
                    ["Resolved destination and travel outcome for the trace."],
                    "Compare preserved destination resolution and travel diagnostics for this exact trace; do not infer a target or combat outcome from the terminal.")
                add("target_context", "The same travel terminal may reflect missing or stale destination context rather than a travel engine failure.", correlated,
                    ["Target/destination resolution recorded for this trace; no target is inferred."],
                    "Read the preserved goal and destination-resolution fields bound to this trace and compare their validity at submission with the travel terminal reason.")
    heartbeats = [e for e in events if e["kind"] == "heartbeat"]
    if len(heartbeats) >= 2 and heartbeats[0]["data"].get("wake_sequence") is not None and heartbeats[0]["data"].get("wake_sequence") == heartbeats[-1]["data"].get("wake_sequence"):
        pending = [e for e in events if e["kind"] == "action_attempt"]
        add("wake_not_advancing", "Equal sampled wake values may indicate scheduling did not advance; they do not prove no intervening action.", [heartbeats[0], heartbeats[-1]],
            ["Scheduler state and wake history across the sampled interval."],
            "Read scheduler-running/active fields and preserved wake history between the two exact heartbeat sources; compare with any live action state.")
        add("legitimate_pending", "An unchanged sampled wake may be consistent with an action legitimately pending rather than a scheduler defect.", [heartbeats[0], heartbeats[-1]] + pending,
            ["Pending action deadline and completion/cancellation history."],
            "Compare preserved running-action trace/deadline state with terminal history and scheduler policy for that interval; absence from samples is not proof of nonexecution.")
    completed = [e for e in events if e["kind"] == "action_result" and e["data"]["result"] == "Completed"]
    movements = [e for e in events if e["kind"] == "movement" and e["data"]["distance_m"] > 0]
    if movements and not completed and not all(packet["acceptance"]["observed"].values()):
        add("travel_pending", "Observed displacement may belong to travel that has not yet reached its legal destination.", movements,
            ["Resolved destination, legal arrival distance and running travel trace."],
            "Compare the preserved movement source with destination, remaining distance and travel deadline; displacement alone proves neither arrival nor objective progress.")
        add("arrival_action_context", "Travel may have reached a destination but its useful action or legal target is not established by these samples.", movements,
            ["Authoritative arrival, target legality and subsequent action lifecycle."],
            "Read arrival/target-resolution and subsequent action records around the exact movement sources; distinguish ongoing travel from arrived-but-unbound work without driving the bot.")
    if completed and not all(packet["acceptance"]["observed"].values()):
        add("terminal_without_effect", "A sampled Completed terminal proves only that the actor request ended; an acceptance effect remains unconfirmed.", completed,
            ["Authoritative objective, inventory, completion or followup evidence required by the unmet criteria."],
            "Compare each selected terminal's quest/trace with preserved native counters and saved quest/inventory fields; inspect the unmet criterion without treating Completed as credit.")
        add("observation_window", "The missing acceptance proof may be outside the sampled observation window, rather than an absent world effect.", completed,
            ["Continuous authoritative observations covering the effect's expected persistence interval."],
            "Read timestamps and collection coverage at the preserved sources and compare with effect persistence timing; do not infer no effect merely from an omitted or unsampled event.")
    execution = packet["test_execution"]
    native_failed = execution.get("native_test_passed") is False or execution.get("test_exit_code") not in (None, 0)
    if native_failed:
        add("native_execution_failure", "The supplied native execution failed independently of the deterministic observer acceptance result.", [],
            ["Native failure detail reconciled with observer acceptance and cleanup status."],
            "Read the supplied native report and end reason; compare its failing assertion with observer criteria and separately recorded cleanup without rerunning gameplay.")
    elif execution and (execution.get("review_phase") == "provisional"
                        or execution.get("cleanup_state") == "pending"
                        or execution.get("native_test_passed") is not True):
        add("native_acceptance_pending", "Observer acceptance does not establish native aggregate acceptance while the supplied execution is pending or incomplete.", [],
            ["Final native assertion result and independently recorded cleanup state."],
            "Read the preserved native report and ordinary execution/cleanup receipt when finalized; keep observer acceptance separate and do not initiate another test or model review.")
    elif packet["deterministic_status"] == "PASS" and all(packet["acceptance"]["observed"].values()):
        add("no_issue", "The supplied observer criteria are satisfied; this does not certify unsampled behavior or unreported native execution.",
            [e for e in events if e["kind"] in ("quest_completed", "quest_accepted", "objective_progress", "inventory_change")], [],
            "Review the exact authoritative criterion references and supplied native/cleanup receipt; retain any separate native failure rather than replacing it with observer PASS.")
    return candidates


def build_packet(report, events_path, verdict_path=None):
    """Return a bounded packet; malformed or insufficient evidence abstains."""
    report = report if isinstance(report, dict) else {}
    identity = {key: report.get(key) for key in IDENTITY}
    packet = {"schema": SCHEMA, "deterministic_status": report.get("status", "UNKNOWN"),
              "identity": identity, "evidence_layer": report.get("evidence_layer") or "UNKNOWN",
              "acceptance": {"required": report.get("acceptance_contract", {}), "observed": report.get("criteria", {})},
              "provenance": {"verdict_path": str(verdict_path) if verdict_path is not None else None,
                             "verdict_sha256": None, "events_path": str(events_path), "events_sha256": None},
              "first_discrepancy": None, "events": [], "hypotheses": [],
              "coverage": {"total_events": 0, "included_events": 0, "omitted_events": 0, "bounds_hit": [],
                           "selection_scope": "Whole input validation required; selected first discrepancy, exact correlated trace, criterion evidence, authoritative progress and boundary heartbeats. Counts cover read rows only; if input_complete=false the remaining count is unknown. Omitted samples are not evidence of nonexecution.",
                           "input_complete": False, "validation_complete": False},
              "abstention_reason": None, "test_execution": report.get("test_execution", {})}
    try:
        if (report.get("schema") != EVENT_SCHEMA
                or not all(_text(identity[key]) for key in ("run_id", "source_sha", "database_id"))
                or not _integer(identity["bot_id"], 1) or not _text(packet["evidence_layer"]) or packet["evidence_layer"] == "UNKNOWN"
                or packet["deterministic_status"] not in ("PASS", "FAIL", "STALLED", "INCONCLUSIVE")):
            raise ValueError("missing essential verdict identity, status or evidence layer")
        if not isinstance(packet["test_execution"], dict):
            raise ValueError("invalid supplied native execution status")
        if verdict_path is not None:
            with Path(verdict_path).open("rb") as stream:
                raw = stream.read(MAX_LINE_BYTES + 1)
            if len(raw) > MAX_LINE_BYTES:
                packet["coverage"]["bounds_hit"].append("verdict_bytes")
                raise ValueError("verdict exceeds declared byte bound")
            packet["provenance"]["verdict_sha256"] = hashlib.sha256(raw).hexdigest()
            if _json(raw) != report:
                raise ValueError("verdict path differs from supplied report")
        collection = report.get("collection", {})
        if not isinstance(collection, dict):
            raise ValueError("invalid collection provenance")
        for key in ("raw_path", "raw_sha256", "consumed_prefix_sha256", "final_raw_sha256"):
            if key in collection:
                value = collection[key]
                if key.endswith("sha256") and (not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdef" for c in value)):
                    raise ValueError("invalid supplied raw/prefix digest")
                if key == "raw_path" and not _text(value):
                    raise ValueError("invalid supplied exact raw source path")
                packet["provenance"][key] = value
        events, total, digest, error, bounds, complete = _read_events(events_path, identity, packet["evidence_layer"])
        packet["provenance"]["events_sha256"] = digest
        packet["coverage"].update(total_events=total, omitted_events=total, bounds_hit=bounds, input_complete=complete)
        by_id = {e["event_id"]: e for e in events}
        thresholds = {notice.get("event_id") for notice in report.get("notifications", [])
                      if isinstance(notice, dict) and notice.get("code") in ("STALL_THRESHOLD", "REPEATED_INVALID")}
        first = next((e for e in events if e["kind"] == "telemetry_gap" or e["event_id"] in thresholds
                      or e["kind"] == "action_result" and e["data"]["result"] != "Completed"), None)
        essential = set()
        if first:
            reason = ("Earliest observed telemetry fault; not a causal finding." if first["kind"] == "telemetry_gap"
                      else "Earliest recorded evaluator threshold; not a causal finding." if first["event_id"] in thresholds
                      else "Earliest observed noncompleted action terminal; not a causal finding.")
            packet["first_discrepancy"] = {"event_id": first["event_id"], "reason": reason}
            essential.add(first["event_id"])
            trace = first["data"].get("trace_id")
            if trace:
                essential.update(e["event_id"] for e in events if e["data"].get("trace_id") == trace)
        for value in report.get("criterion_evidence", {}).values():
            essential.update(value if isinstance(value, list) else [value] if value else [])
        essential.update(report.get("failure_or_stall_evidence", []))
        for field in ("failure_details", "unconfirmed_attempts"):
            essential.update(e["event_id"] for e in report.get(field, []))
        heartbeats = [e for e in events if e["kind"] == "heartbeat"]
        if heartbeats:
            essential.update((heartbeats[0]["event_id"], heartbeats[-1]["event_id"]))
        for kind in ("action_result", "inventory_change", "movement", "objective_progress", "quest_completed", "quest_accepted", "goal_selected"):
            group = [e for e in events if e["kind"] == kind]
            if group:
                essential.update((group[0]["event_id"], group[-1]["event_id"]))
        contract = report.get("acceptance_contract", {})
        if report.get("criteria", {}).get("item_progress"):
            qualified = next((e for e in events if e["kind"] == "inventory_change"
                              and e["data"]["quest_id"] == contract.get("quest_id")
                              and e["data"]["item_id"] == contract.get("item_id")
                              and e["data"]["after"] > e["data"]["before"]
                              and e["data"]["after"] >= contract.get("item_count", math.inf)), None)
            if qualified:
                essential.add(qualified["event_id"])
        traces = {e["data"]["trace_id"] for e in events
                  if e["event_id"] in essential and "trace_id" in e["data"]}
        essential.update(e["event_id"] for e in events if e["data"].get("trace_id") in traces)
        packet["events"] = [e for e in events if e["event_id"] in essential]
        packet["coverage"].update(included_events=len(packet["events"]), omitted_events=total - len(packet["events"]))
        if error:
            raise ValueError(error)
        if not events or not heartbeats:
            raise ValueError("missing continuous heartbeat telemetry")
        if _references(report, by_id) - by_id.keys():
            raise ValueError("verdict refers to missing normalized evidence")
        if report.get("counts", {}).get("events", total) != total:
            raise ValueError("verdict/event input count differs")
        if report.get("telemetry_fault") or any(e["kind"] == "telemetry_gap" for e in events) or collection.get("raw_preserved") is False:
            raise ValueError("missing, incomplete or conflicting telemetry")
        if report.get("finished_at") and _time(report["finished_at"]) < _time(events[-1]["timestamp"]):
            raise ValueError("verdict finish precedes event evidence")
        _check_criteria(report, by_id)
        packet["coverage"]["validation_complete"] = True
        packet["hypotheses"] = _hypotheses(packet)
    except (OSError, ValueError, TypeError, KeyError, AttributeError, OverflowError, RecursionError) as exc:
        packet["abstention_reason"] = "Evidence abstention: " + (str(exc)[:240] if not isinstance(exc, OSError) else "required evidence file unavailable")
        packet["hypotheses"] = [{"id": "unknown", "summary": "Essential evidence is missing, malformed, conflicting or unbounded.", "supporting_event_ids": [],
                                 "missing_evidence": ["Complete bounded identity-matched evidence."],
                                 "check": "Inspect the preserved evidence and collection receipt read-only; do not infer a cause from invalid evidence."}]
    if packet["first_discrepancy"] and packet["first_discrepancy"]["event_id"] not in {e["event_id"] for e in packet["events"]}:
        packet["first_discrepancy"] = None
    try:
        oversized = len(encode(packet)) >= MAX_PACKET_BYTES
    except (ValueError, TypeError, OverflowError, RecursionError):
        oversized = True
    if oversized:
        packet["abstention_reason"] = "Essential evidence cannot fit the 16384-byte packet bound without losing exact references."
        packet["events"] = []
        packet["first_discrepancy"] = None
        packet["coverage"]["included_events"] = 0
        packet["coverage"]["omitted_events"] = packet["coverage"]["total_events"]
        packet["coverage"]["bounds_hit"].append("packet_bytes")
        packet["hypotheses"] = [{"id": "unknown", "summary": "Exact essential evidence exceeds the packet bound.", "supporting_event_ids": [], "missing_evidence": ["A packet containing the exact essential evidence within the declared bound."], "check": "Read the preserved complete event input; no automated causal recommendation is eligible."}]
        try:
            still_oversized = len(encode(packet)) > MAX_PACKET_BYTES
        except (ValueError, TypeError, OverflowError, RecursionError):
            still_oversized = True
        if still_oversized:
            packet["identity"] = {key: None for key in IDENTITY}
            packet["acceptance"] = {"required": {}, "observed": {}}
            packet["test_execution"] = {}
            packet["provenance"] = {key: None for key in packet["provenance"]}
            for key in ("deterministic_status", "evidence_layer"):
                if not isinstance(packet[key], str) or len(packet[key].encode("utf-8")) > 1024:
                    packet[key] = "UNKNOWN"
    return packet


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("verdict", type=Path)
    parser.add_argument("--events", type=Path)
    args = parser.parse_args()
    try:
        with args.verdict.open("rb") as stream:
            raw = stream.read(MAX_LINE_BYTES + 1)
        report = _json(raw) if len(raw) <= MAX_LINE_BYTES else {}
    except (OSError, ValueError, TypeError, RecursionError):
        report = {}
    packet = build_packet(report, args.events or args.verdict.with_name("events.jsonl"), args.verdict)
    print(encode(packet).decode("utf-8"))


if __name__ == "__main__":
    main()
