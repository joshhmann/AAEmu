"""Deterministic, read-only verdicts for one AAEmu quest observer run.

The adapter supplies events; this module never calls the game or changes it.
An actor's Completed result confirms an action ended, while saved quest rows
confirm the resulting world state. Both are needed for a PASS.
"""

from __future__ import annotations

from datetime import datetime, timezone
import math
from typing import Any, Iterable


SCHEMA = "aaemu.quest-observer/v1"
KINDS = {
    "heartbeat", "goal_selected", "action_attempt", "action_result",
    "inventory_change", "movement", "objective_progress",
    "quest_completed", "quest_accepted", "telemetry_gap",
}


def _time(value: Any) -> datetime:
    if not isinstance(value, str):
        raise ValueError("timestamp must be an ISO-8601 string")
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("timestamp needs a timezone")
    return parsed.astimezone(timezone.utc)


def _positive(value: Any, name: str) -> float:
    if (isinstance(value, bool) or not isinstance(value, (int, float))
            or not math.isfinite(value) or value <= 0):
        raise ValueError(f"{name} must be positive")
    return float(value)


class Evaluator:
    """Consumes one bot's ordered observations and emits provisional findings."""

    def __init__(self, manifest: dict[str, Any]):
        if manifest.get("schema") != SCHEMA:
            raise ValueError("unsupported manifest schema")
        self.manifest = manifest
        self.identity = {key: manifest[key] for key in
                         ("run_id", "bot_id", "source_sha", "database_id")}
        if (not isinstance(self.identity["bot_id"], int)
                or isinstance(self.identity["bot_id"], bool)
                or self.identity["bot_id"] <= 0):
            raise ValueError("bot_id must be a positive integer")
        self.bot_name = manifest["bot_name"]
        self.layer = manifest["evidence_layer"]
        self.acceptance = manifest["acceptance"]
        self.quest = self.acceptance["quest_id"]
        if isinstance(self.quest, bool) or not isinstance(self.quest, int) or self.quest <= 0:
            raise ValueError("quest_id must be a positive integer")
        self.objective = self.acceptance.get("objective")
        self.hunt = self.objective is not None
        if self.hunt:
            if not isinstance(self.objective, dict) or self.objective.get("mode") != "native_hunt":
                raise ValueError("unsupported objective mode")
            self.hunt_act = self.objective.get("act_id")
            self.hunt_required = self.objective.get("required_progress")
            if any(isinstance(v, bool) or not isinstance(v, int) or v <= 0
                   for v in (self.hunt_act, self.hunt_required)):
                raise ValueError("hunt act_id and required_progress must be positive integers")
            # Hunt credit is a native quest counter, not an inventory item.
            self.item = self.acceptance.get("item_id")
            self.item_count = self.acceptance.get("item_count")
        else:
            self.item = self.acceptance["item_id"]
            self.item_count = self.acceptance["item_count"]
            if any(isinstance(v, bool) or not isinstance(v, int) or v <= 0
                   for v in (self.item, self.item_count)):
                raise ValueError("acceptance IDs and item_count must be positive integers")
        if self.acceptance.get("require_followup") is not True:
            raise ValueError("this version requires a subsequent quest")
        if self.acceptance.get("independent_selection") is not True:
            raise ValueError("this version requires independent selection")
        thresholds = manifest["thresholds"]
        self.inactivity = _positive(thresholds["inactivity_seconds"], "inactivity_seconds")
        self.repeated_failures = thresholds["repeated_failures"]
        if (not isinstance(self.repeated_failures, int)
                or isinstance(self.repeated_failures, bool)
                or self.repeated_failures < 2):
            raise ValueError("repeated_failures must be at least 2")
        self.scheduler_stall = _positive(thresholds["scheduler_stall_seconds"], "scheduler_stall_seconds")
        self.max_gap = _positive(thresholds["max_gap_seconds"], "max_gap_seconds")
        self.events: list[dict[str, Any]] = []
        self.notifications: list[dict[str, Any]] = []
        self._notified: set[str] = set()
        self._ids: dict[str, dict[str, Any]] = {}
        self._terminals: dict[str, tuple[Any, ...]] = {}
        self._attempts: dict[str, dict[str, Any]] = {}
        self._results: list[dict[str, Any]] = []
        self._selections: list[dict[str, Any]] = []
        self._accepted: list[dict[str, Any]] = []
        self._completed: list[dict[str, Any]] = []
        self._invalid: list[dict[str, Any]] = []
        self._invalid_groups: dict[tuple[Any, ...], list[dict[str, Any]]] = {}
        self._last_progress: datetime | None = None
        self._first: datetime | None = None
        self._last: datetime | None = None
        self._gap: str | None = None
        self._gap_events: list[str] = []
        self._movement_or_action_at: datetime | None = None
        self._wake: Any = None
        self._wake_since: datetime | None = None
        self._heartbeat_at: datetime | None = None
        self._heartbeat_active = False
        self._scheduler_running = True
        self._finished = False
        self._current_goal: str | None = None
        self._goal_reason: str | None = None
        self._hunt_last_count: int | None = None
        self._hunt_qualified: dict[str, Any] | None = None

    def _notify(self, code: str, event: dict[str, Any], detail: str) -> list[dict[str, Any]]:
        if code in self._notified:
            return []
        self._notified.add(code)
        notice = {"code": code, "timestamp": event["timestamp"],
                  "event_id": event["event_id"], "detail": detail}
        self.notifications.append(notice)
        return [notice]

    def _fault(self, reason: str, event_id: str | None = None) -> None:
        self._gap = self._gap or reason
        if event_id and event_id not in self._gap_events:
            self._gap_events.append(event_id)

    def consume(self, event: dict[str, Any]) -> list[dict[str, Any]]:
        if self._finished:
            raise RuntimeError("run already finished")
        try:
            if not isinstance(event, dict) or event.get("schema") != SCHEMA:
                raise ValueError("invalid event schema")
            for key, expected in self.identity.items():
                if event.get(key) != expected or type(event.get(key)) is not type(expected):
                    raise ValueError(f"{key} differs from manifest")
            event_id = event["event_id"]
            if not isinstance(event_id, str) or not event_id:
                raise ValueError("event_id missing")
            kind = event["kind"]
            if kind not in KINDS:
                raise ValueError("unknown event kind")
            source = event["source"]
            if (not isinstance(source, dict)
                    or any(key not in source for key in ("artifact", "line", "field", "layer"))
                    or source["layer"] != self.layer):
                raise ValueError("source location missing")
            data = event["data"]
            if not isinstance(data, dict):
                raise ValueError("event data missing")
            at = _time(event["timestamp"])
        except (KeyError, TypeError, ValueError) as exc:
            self._fault(f"invalid event: {exc}", event.get("event_id") if isinstance(event, dict) else None)
            return []
        if event_id in self._ids:
            if event != self._ids[event_id]:
                self._fault("conflicting repeated event_id", event_id)
            return []
        self._ids[event_id] = event
        if self._last is not None:
            if at < self._last:
                self._fault("event time moved backwards", event_id)
            elif (at - self._last).total_seconds() > self.max_gap:
                self._fault("observation gap exceeded max_gap_seconds", event_id)
        self._first = self._first or at
        self._last = max(self._last or at, at)
        self.events.append(event)
        if kind == "telemetry_gap":
            self._fault("telemetry gap: " + str(data.get("reason", "unspecified")), event_id)
            return []
        try:
            notices = self._apply(event, at)
            if self._gap:
                return notices
            if kind in ("action_result", "goal_selected", "quest_completed", "quest_accepted"):
                terminal_a, bit_a, followup = self._proof()
                if terminal_a and bit_a:
                    notices += self._notify("QUEST_COMPLETED_CONFIRMED", event,
                                            "turn-in terminal and new authoritative completion bit observed")
                if followup:
                    notices += self._notify("INDEPENDENT_FOLLOWUP_CONFIRMED", event,
                                            "independent accept terminal and new authoritative quest state observed")
            if self._failing_group():
                notices += self._notify("REPEATED_INVALID", event, "same invalid quest action repeated")
            elif not self._acceptance_satisfied() and self._stall_kind(at):
                notices += self._notify("STALL_THRESHOLD", event, self._stall_kind(at) or "stalled")
            return notices
        except (KeyError, TypeError, ValueError) as exc:
            self._fault(f"invalid {kind} payload: {exc}", event_id)
            return []

    def _apply(self, event: dict[str, Any], at: datetime) -> list[dict[str, Any]]:
        kind, data = event["kind"], event["data"]
        if kind == "heartbeat":
            wake = data["wake_sequence"]
            if wake is not None and (not isinstance(wake, int) or isinstance(wake, bool) or wake < 0):
                raise ValueError("wake_sequence must be a nonnegative integer or null")
            active = data["active"]
            if not isinstance(active, bool):
                raise ValueError("active must be boolean")
            running = data.get("scheduler_running")
            if running is not None and not isinstance(running, bool):
                raise ValueError("scheduler_running must be boolean or null")
            if wake is None or wake != self._wake or not active:
                self._wake_since = at
            self._wake = wake
            self._heartbeat_at = at
            self._heartbeat_active = active
            self._scheduler_running = running
            self._current_goal = data.get("current_goal") or data.get("quest_id")
            self._goal_reason = data.get("selection_reason")
            return []
        if kind == "movement":
            distance = data["distance_m"]
            if (isinstance(distance, bool) or not isinstance(distance, (int, float))
                    or not math.isfinite(distance) or distance < 0):
                raise ValueError("distance_m must be nonnegative")
            if distance > 0:
                self._movement_or_action_at = at
            return []
        if kind == "goal_selected":
            if not isinstance(data["independent"], bool):
                raise ValueError("independent must be boolean")
            for key in ("quest_id", "reason", "policy", "trace_id"):
                if data.get(key) in (None, ""):
                    raise ValueError(f"{key} missing")
            self._selections.append(event)
            self._current_goal = f"quest.accept:{data['quest_id']}"
            self._goal_reason = data["reason"]
            return []
        if kind in ("action_attempt", "action_result"):
            trace = data["trace_id"]
            action = data["action"]
            quest = data.get("quest_id")
            if quest is None:
                quest = 0  # background actor work has no quest parent
            if not isinstance(trace, str) or not trace or not isinstance(action, str) or not action:
                raise ValueError("trace_id/action missing")
            if not isinstance(quest, int) or isinstance(quest, bool) or quest < 0:
                raise ValueError("quest_id must be nonnegative")
            self._movement_or_action_at = at
            if kind == "action_attempt":
                prior = self._attempts.get(trace)
                if prior and (prior["data"]["action"], prior["data"].get("quest_id") or 0) != (action, quest):
                    self._fault("conflicting repeated action attempt", event["event_id"])
                else:
                    self._attempts.setdefault(trace, event)
                return []
            result = data["result"]
            if result not in ("Completed", "Rejected", "Interrupted", "TimedOut", "Failed", "Cancelled"):
                raise ValueError("invalid terminal result")
            signature = (action, quest, result, data.get("reason"), data.get("failure_class"))
            if trace in self._terminals:
                if self._terminals[trace] != signature:
                    self._fault("conflicting repeated terminal trace", event["event_id"])
                return []
            attempt = self._attempts.get(trace)
            if attempt and (attempt["data"]["action"], attempt["data"].get("quest_id") or 0) != (action, quest):
                self._fault("terminal differs from attempt", event["event_id"])
                return []
            self._terminals[trace] = signature
            self._results.append(event)
            if action == "AcceptQuest" and result == "Completed" and self._proof()[2]:
                self._last_progress = at
                self._invalid.clear()
                self._invalid_groups.clear()
            if quest == self.quest and result != "Completed" and data.get("failure_class") == "invalid":
                self._invalid.append(event)
                group = (action, quest, data.get("reason"), data.get("failure_class"))
                self._invalid_groups.setdefault(group, []).append(event)
            return []
        quest = data["quest_id"]
        if not isinstance(quest, int) or isinstance(quest, bool) or quest <= 0:
            raise ValueError("quest_id must be positive")
        if kind in ("inventory_change", "objective_progress"):
            before, after = data["before"], data["after"]
            if (not isinstance(before, int) or isinstance(before, bool)
                    or not isinstance(after, int) or isinstance(after, bool)
                    or before < 0 or after < 0):
                raise ValueError("before/after must be nonnegative integers")
            relevant = kind == "objective_progress" and quest == self.quest
            if kind == "inventory_change":
                relevant = not self.hunt and quest == self.quest and data["item_id"] == self.item
            if kind == "objective_progress" and data.get("authority") != "native quest progress":
                raise ValueError("objective_progress requires native quest progress authority")
            if kind == "objective_progress" and self.hunt and quest == self.quest:
                act = data.get("act_id")
                if isinstance(act, bool) or not isinstance(act, int) or act <= 0:
                    raise ValueError("hunt objective_progress requires a positive act_id")
                relevant = act == self.hunt_act
                if relevant:
                    credited_at = _time(event["source"].get("native_timestamp", event["timestamp"]))
                    if credited_at > at:
                        raise ValueError("native hunt credit timestamp exceeds observation")
                    if after < before or (self._hunt_last_count is not None
                                          and before != self._hunt_last_count):
                        self._fault("hunt objective count decreased or skipped", event["event_id"])
                    self._hunt_last_count = after
                    if after > before and after >= self.hunt_required:
                        self._hunt_qualified = event
            if relevant and after > before:
                self._last_progress = at
                self._invalid.clear()
                self._invalid_groups.clear()
                if kind == "inventory_change" and after >= self.item_count:
                    return self._notify("REQUIRED_ITEMS_OBSERVED", event, "required item count observed; quest credit still needs completion proof")
                if kind == "objective_progress" and self.hunt and self._hunt_qualified is event:
                    return self._notify("HUNT_PROGRESS_OBSERVED", event,
                                        "required native hunt count observed; completion still needs proof")
                return []
            return []
        if kind in ("quest_completed", "quest_accepted"):
            expected_authority = ("saved completed_quests bit" if kind == "quest_completed"
                                  else "saved quests row")
            if (data["before"] is not False or data["after"] is not True
                    or data.get("authority") not in (expected_authority,
                        "native CharacterQuests completion bit" if kind == "quest_completed"
                        else "native CharacterQuests active row")):
                raise ValueError("requires false-to-true authoritative saved state")
            if kind == "quest_completed":
                self._completed.append(event)
            else:
                self._accepted.append(event)
            followup_acceptance = kind == "quest_accepted" and self._proof()[2] is not None
            if quest == self.quest or followup_acceptance:
                # A new acceptance can be the next chosen quest; an unrelated
                # saved completion does not keep an old objective alive.
                self._last_progress = at
                self._invalid.clear()
                self._invalid_groups.clear()
            return []
        raise ValueError("unhandled kind")

    def _proof(self):
        terminal_a = next((e for e in self._results if e["data"]["quest_id"] == self.quest
                           and e["data"]["action"] == "TurnInQuest"
                           and e["data"]["result"] == "Completed"), None)
        # The two read surfaces can be sampled in either order. A new saved
        # bit and a request terminal are both required before claiming A.
        bit_a = next((e for e in self._completed if e["data"]["quest_id"] == self.quest), None)
        followup = None
        if terminal_a and bit_a:
            for selected in self._selections:
                choice = selected["data"]
                if (_time(selected["timestamp"]) <= _time(terminal_a["timestamp"])
                        or choice["quest_id"] == self.quest or not choice["independent"]
                        or choice["policy"] != "quest-v1"):
                    continue
                accepted_result = next((e for e in self._results
                                        if e["data"]["trace_id"] == choice["trace_id"]
                                        and e["data"]["quest_id"] == choice["quest_id"]
                                        and e["data"]["action"] == "AcceptQuest"
                                        and e["data"]["result"] == "Completed"
                                        and _time(e["timestamp"]) >= _time(selected["timestamp"])), None)
                saved = next((e for e in self._accepted + self._completed
                              if e["data"]["quest_id"] == choice["quest_id"]
                              and _time(e["timestamp"]) >= _time(selected["timestamp"])), None)
                if accepted_result and saved:
                    followup = (selected, accepted_result, saved)
                    break
        return terminal_a, bit_a, followup

    def _hunt_proof(self, terminal, bit):
        if not self._hunt_qualified or self._gap:
            return None
        event = self._hunt_qualified
        credited_at = _time(event["source"].get("native_timestamp", event["timestamp"]))
        # Credit belongs to this completion episode, including delayed log sampling.
        if any(credited_at > _time(e["timestamp"]) for e in (terminal, bit) if e):
            return None
        return event

    def _acceptance_satisfied(self):
        terminal, bit, followup = self._proof()
        if self.hunt:
            return bool(terminal and bit and followup and self._hunt_proof(terminal, bit))
        return bool(terminal and bit and followup and any(
            e["kind"] == "inventory_change" and e["data"]["quest_id"] == self.quest
            and e["data"]["item_id"] == self.item
            and e["data"]["after"] >= self.item_count
            and e["data"]["after"] > e["data"]["before"] for e in self.events))

    def _failing_group(self):
        return next((rows for rows in self._invalid_groups.values()
                     if len(rows) >= self.repeated_failures), None)

    def _stall_kind(self, at: datetime) -> str | None:
        if (self._heartbeat_at and self._heartbeat_active and self._scheduler_running is True
                and self._wake is not None and self._wake_since
                and (at - self._heartbeat_at).total_seconds() <= self.max_gap
                and (at - self._wake_since).total_seconds() >= self.scheduler_stall
                and (self._movement_or_action_at is None
                     or self._movement_or_action_at < self._wake_since)
                and (self._last_progress is None or self._last_progress < self._wake_since)):
            return "scheduler wake unchanged while bot active"
        base = self._last_progress or self._first
        if base and (at - base).total_seconds() >= self.inactivity:
            return "quest objective inactive"
        return None

    def finish(self, reason: str, timestamp: str) -> dict[str, Any]:
        if self._finished:
            raise RuntimeError("run already finished")
        self._finished = True
        try:
            end = _time(timestamp)
            if self._last and end < self._last:
                self._fault("finish time precedes last event")
            if self._last and (end - self._last).total_seconds() > self.max_gap:
                self._fault("telemetry stopped before finish")
        except ValueError as exc:
            self._fault(f"invalid finish time: {exc}")
            end = self._last or datetime.now(timezone.utc)
        terminal_a, bit_a, followup = self._proof()
        confirmed = [e for e in self._results if e["data"]["result"] == "Completed"]
        pending = [e for trace, e in self._attempts.items() if trace not in self._terminals]
        criteria = {"turn_in_terminal": bool(terminal_a), "authoritative_completion": bool(bit_a),
                    "independent_followup": bool(followup)}
        if self.hunt:
            criteria["hunt_progress"] = bool(self._hunt_proof(terminal_a, bit_a))
        else:
            criteria["item_progress"] = any(e["kind"] == "inventory_change"
                                            and e["data"]["quest_id"] == self.quest
                                            and e["data"]["item_id"] == self.item
                                            and e["data"]["after"] >= self.item_count
                                            and e["data"]["after"] > e["data"]["before"]
                                            for e in self.events)
        failure_group = self._failing_group()
        invalid_fail = bool(failure_group)
        inactivity_base = self._last_progress or self._first
        inactive = bool(inactivity_base and (end - inactivity_base).total_seconds() >= self.inactivity)
        scheduler_stalled = bool(self._heartbeat_at and self._heartbeat_active and self._scheduler_running is True
                                 and self._wake is not None
                                 and (end - self._heartbeat_at).total_seconds() <= self.max_gap
                                 and self._wake_since and (end - self._wake_since).total_seconds() >= self.scheduler_stall
                                 and (self._movement_or_action_at is None
                                      or self._movement_or_action_at < self._wake_since)
                                 and (self._last_progress is None or self._last_progress < self._wake_since))
        if invalid_fail:
            status, subsystem, action = "FAIL", "quest action validation", "Inspect repeated invalid quest requests."
        elif self._gap:
            status, subsystem, action = "INCONCLUSIVE", "observer telemetry", "Restore continuous, identity-matched telemetry and replay."
        elif all(criteria.values()):
            status, subsystem, action = "PASS", "none indicated", "Review the saved quest chain and continue bounded testing."
        elif scheduler_stalled:
            status, subsystem, action = "STALLED", "scheduler (suspected)", "Inspect bot wake scheduling and active state."
        elif inactive:
            status, subsystem, action = "STALLED", "quest progression (suspected)", "Inspect the current quest action and its missing effect."
        else:
            status, subsystem, action = "INCONCLUSIVE", "observation window", "Continue bounded observation for missing quest effects."
        relevant = [e["event_id"] for e in self.events if e["kind"] != "heartbeat"]
        evidence = ([e["event_id"] for e in failure_group] if invalid_fail else
                    self._gap_events if self._gap else
                    [e["event_id"] for e in self.events[-3:]] if status == "STALLED" else [])
        conclusion = {"code": "VERDICT_" + status, "timestamp": end.isoformat(),
                      "event_id": None, "detail": reason}
        self.notifications.append(conclusion)
        confirmed_details = [{"event_id": e["event_id"], "action": e["data"]["action"],
                              "quest_id": e["data"].get("quest_id"), "result": e["data"]["result"],
                              "seen_at": e["timestamp"]} for e in confirmed]
        failure_details = [{"event_id": e["event_id"], "action": e["data"]["action"],
                            "quest_id": e["data"].get("quest_id"), "result": e["data"]["result"],
                            "reason": e["data"].get("reason"), "seen_at": e["timestamp"]}
                           for e in (failure_group or [])]
        pending_details = [{"event_id": e["event_id"], "action": e["data"]["action"],
                            "quest_id": e["data"].get("quest_id"), "trace_id": e["data"]["trace_id"],
                            "seen_at": e["timestamp"]} for e in pending]
        criterion_evidence = {"turn_in_terminal": terminal_a["event_id"] if terminal_a else None,
                              "authoritative_completion": bit_a["event_id"] if bit_a else None,
                              "independent_followup": [e["event_id"] for e in followup] if followup else []}
        if self.hunt:
            criterion_evidence["hunt_progress"] = self._hunt_qualified["event_id"] if self._hunt_proof(terminal_a, bit_a) else None
        report = {"schema": SCHEMA, "run_id": self.identity["run_id"], "bot_id": self.identity["bot_id"],
                "bot_name": self.bot_name, "source_sha": self.identity["source_sha"],
                "database_id": self.identity["database_id"], "evidence_layer": self.layer,
                "status": status, "finish_reason": reason, "finished_at": end.isoformat(),
                "criteria": criteria, "acceptance_contract": self.acceptance,
                "criterion_evidence": criterion_evidence,
                "confirmed_actions": confirmed_details,
                "confirmed_actions_meaning": "actor request terminals, not world effects",
                "unconfirmed_attempts": pending_details,
                "last_meaningful_progress": self._last_progress.isoformat() if self._last_progress else None,
                "failure_or_stall_evidence": evidence, "failure_details": failure_details,
                "world_state_sources": sorted({e["data"]["authority"] for e in self._completed + self._accepted}),
                "repeated_invalid_group_count": len(failure_group) if failure_group else 0,
                "current_goal": self._current_goal, "goal_selection_reason": self._goal_reason,
                "counts": {"events": len(self.events), "action_terminals": len(self._results),
                           "pending_attempts": len(pending), "completion_changes": len(self._completed),
                           "acceptance_changes": len(self._accepted)},
                "likely_subsystem": subsystem, "relevant_events": relevant,
                "next_engineering_action": action, "notifications": self.notifications,
                "telemetry_fault": self._gap}
        if self.hunt:
            report["confirmed_objective_progress"] = [
                {"event_id": e["event_id"], "timestamp": e["timestamp"],
                 "data": e["data"], "source": e["source"]}
                for e in self.events if e["kind"] == "objective_progress"
                and e["data"]["quest_id"] == self.quest
                and e["data"].get("act_id") == self.hunt_act
                and e["data"].get("authority") == "native quest progress"
                and e["data"]["after"] > e["data"]["before"]][-8:]
        return report


def evaluate(events: Iterable[dict[str, Any]], manifest: dict[str, Any],
             reason: str, timestamp: str) -> dict[str, Any]:
    evaluator = Evaluator(manifest)
    for event in events:
        evaluator.consume(event)
    return evaluator.finish(reason, timestamp)
