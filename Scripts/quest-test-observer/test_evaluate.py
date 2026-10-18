"""Synthetic, deterministic tests of observer verdict boundaries."""

import importlib.util
from datetime import datetime, timedelta, timezone
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("quest_observer_evaluate", Path(__file__).with_name("evaluate.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
Evaluator = module.Evaluator


def manifest():
    return {"schema": module.SCHEMA, "run_id": "run-1", "bot_id": 297,
            "bot_name": "Qrunaa", "source_sha": "5914c3e7", "database_id": "game-1",
            "evidence_layer": "synthetic rig", "acceptance": {"quest_id": 251,
            "item_id": 4058, "item_count": 3, "require_followup": True,
            "independent_selection": True}, "thresholds": {"inactivity_seconds": 30,
            "repeated_failures": 3, "scheduler_stall_seconds": 20,
            "max_gap_seconds": 15}}


START = datetime(2026, 10, 12, tzinfo=timezone.utc)


def stamp(seconds):
    return (START + timedelta(seconds=seconds)).isoformat()


def event(number, seconds, kind, **data):
    return {"schema": module.SCHEMA, "run_id": "run-1", "bot_id": 297,
            "source_sha": "5914c3e7", "database_id": "game-1",
            "timestamp": stamp(seconds), "event_id": f"e{number}", "kind": kind,
            "source": {"artifact": "fixture.jsonl", "line": number,
                       "field": kind, "layer": "synthetic rig"}, "data": data}


def terminal(number, seconds, trace, action, quest, result="Completed", **extra):
    return event(number, seconds, "action_result", trace_id=trace, action=action,
                 quest_id=quest, result=result, **extra)


class EvaluatorTests(unittest.TestCase):
    def feed(self, *events):
        evaluator = Evaluator(manifest())
        for item in events:
            evaluator.consume(item)
        return evaluator

    def success_events(self):
        return [
            event(1, 0, "heartbeat", wake_sequence=1, active=True, scheduler_running=True),
            event(2, 1, "inventory_change", quest_id=251, item_id=4058, before=0, after=1),
            event(3, 2, "inventory_change", quest_id=251, item_id=4058, before=1, after=2),
            event(4, 3, "inventory_change", quest_id=251, item_id=4058, before=2, after=3),
            event(5, 4, "action_attempt", trace_id="turn-a", action="TurnInQuest", quest_id=251),
            terminal(6, 5, "turn-a", "TurnInQuest", 251),
            event(7, 6, "quest_completed", quest_id=251, before=False, after=True,
                  authority="saved completed_quests bit"),
            event(8, 7, "goal_selected", quest_id=324, reason="eligible offer",
                  policy="quest-v1", independent=True, trace_id="accept-b"),
            terminal(9, 8, "accept-b", "AcceptQuest", 324),
            event(10, 9, "quest_accepted", quest_id=324, before=False, after=True,
                  authority="saved quests row"),
            event(11, 10, "heartbeat", wake_sequence=2, active=True, scheduler_running=True),
        ]

    def test_success_needs_terminal_saved_bit_and_independent_followup(self):
        evaluator = self.feed(*self.success_events())
        report = evaluator.finish("bounded end", stamp(11))
        self.assertEqual("PASS", report["status"])
        self.assertTrue(all(report["criteria"].values()))
        self.assertIn("e6", [a["event_id"] for a in report["confirmed_actions"]])
        self.assertIn("QUEST_COMPLETED_CONFIRMED", [n["code"] for n in report["notifications"]])
        self.assertIn("INDEPENDENT_FOLLOWUP_CONFIRMED", [n["code"] for n in report["notifications"]])
        self.assertEqual("VERDICT_PASS", report["notifications"][-1]["code"])
        self.assertEqual([], report["unconfirmed_attempts"])
        self.assertEqual("synthetic rig", report["evidence_layer"])

    def test_repeated_invalid_relevant_action_fails_but_background_busy_does_not(self):
        evaluator = self.feed(event(1, 0, "heartbeat", wake_sequence=1, active=True),
                              terminal(2, 1, "bg", "DiscoverSelfQuests", 0,
                                       "Rejected", failure_class="busy", reason="actor busy"),
                              terminal(3, 2, "a", "Move", 251, "Rejected", failure_class="invalid"),
                              terminal(4, 3, "b", "Move", 251, "Rejected", failure_class="invalid"),
                              terminal(5, 4, "c", "Move", 251, "Rejected", failure_class="invalid"))
        report = evaluator.finish("bounded end", stamp(5))
        self.assertEqual("FAIL", report["status"])
        self.assertIsNone(report["telemetry_fault"])
        self.assertEqual(["e3", "e4", "e5"], report["failure_or_stall_evidence"])
        self.assertEqual(1, sum(n["code"] == "REPEATED_INVALID" for n in report["notifications"]))

    def test_movement_without_quest_effect_stalls_for_inactivity(self):
        events = [event(1, 0, "heartbeat", wake_sequence=1, active=True),
                  event(2, 10, "movement", distance_m=40, position=[1, 2, 3], quest_id=251),
                  event(3, 20, "heartbeat", wake_sequence=2, active=True),
                  event(4, 30, "movement", distance_m=30, position=[4, 5, 6], quest_id=251),
                  event(5, 40, "heartbeat", wake_sequence=3, active=True)]
        report = self.feed(*events).finish("bounded end", stamp(41))
        self.assertEqual("STALLED", report["status"])
        self.assertEqual("quest progression (suspected)", report["likely_subsystem"])
        self.assertIsNone(report["last_meaningful_progress"])

    def test_stable_wake_and_no_activity_suspects_scheduler(self):
        events = [event(i + 1, i * 5, "heartbeat", wake_sequence=7,
                        active=True, scheduler_running=True) for i in range(6)]
        report = self.feed(*events).finish("bounded end", stamp(26))
        self.assertEqual("STALLED", report["status"])
        self.assertEqual("scheduler (suspected)", report["likely_subsystem"])

    def test_missing_wake_cannot_prove_scheduler_stall(self):
        events = [event(i + 1, i * 5, "heartbeat", wake_sequence=None,
                        active=True) for i in range(7)]
        report = self.feed(*events).finish("bounded end", stamp(31))
        self.assertEqual("STALLED", report["status"])
        self.assertEqual("quest progression (suspected)", report["likely_subsystem"])

    def test_telemetry_gap_overrides_apparent_success(self):
        events = self.success_events() + [event(12, 11, "telemetry_gap", reason="brain unavailable")]
        report = self.feed(*events).finish("bounded end", stamp(12))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertIn("e12", report["failure_or_stall_evidence"])

    def test_wrong_identity_and_missing_payload_fail_closed(self):
        wrong = event(1, 0, "quest_completed", quest_id=251, before=False,
                      after=True, authority="saved completed_quests bit")
        wrong["bot_id"] = 298
        evaluator = self.feed(wrong)
        report = evaluator.finish("bounded end", stamp(1))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertIn("bot_id", report["telemetry_fault"])

    def test_terminal_without_saved_bit_cannot_pass(self):
        events = [e for e in self.success_events() if e["event_id"] != "e7"]
        report = self.feed(*events).finish("bounded end", stamp(11))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertFalse(report["criteria"]["authoritative_completion"])
        self.assertFalse(report["criteria"]["independent_followup"])

    def test_same_event_deduplicates_conflicting_trace_becomes_gap(self):
        events = self.success_events()
        duplicate = dict(events[5])
        evaluator = self.feed(*events[:6], duplicate)
        self.assertEqual(6, len(evaluator.events))
        conflict = terminal(20, 6, "turn-a", "TurnInQuest", 251, "Rejected")
        evaluator.consume(conflict)
        report = evaluator.finish("bounded end", stamp(7))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertIn("conflicting repeated terminal", report["telemetry_fault"])

    def test_saved_bit_can_arrive_before_terminal_and_followup_save_can_lag(self):
        events = self.success_events()
        bit = events.pop(6)
        bit["timestamp"] = stamp(4)
        events.insert(4, bit)
        followup_saved = events.pop(-2)
        followup_saved["timestamp"] = stamp(13)
        events.append(followup_saved)
        report = self.feed(*events).finish("bounded end", stamp(14))
        self.assertEqual("PASS", report["status"])

    def test_progress_resets_invalid_failure_count(self):
        events = [event(1, 0, "heartbeat", wake_sequence=1, active=True),
                  terminal(2, 1, "a", "Move", 251, "Rejected", failure_class="invalid"),
                  terminal(3, 2, "b", "Move", 251, "Rejected", failure_class="invalid"),
                  event(4, 3, "objective_progress", quest_id=251, before=0, after=1,
                        authority="native quest progress"),
                  terminal(5, 4, "c", "Move", 251, "Rejected", failure_class="invalid")]
        report = self.feed(*events).finish("bounded end", stamp(5))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertEqual(stamp(3), report["last_meaningful_progress"])

    def test_wrong_source_layer_is_telemetry_fault(self):
        bad = event(1, 0, "heartbeat", wake_sequence=1, active=True)
        bad["source"]["layer"] = "source code"
        report = self.feed(bad).finish("bounded end", stamp(1))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertIn("source location", report["telemetry_fault"])

    def test_nonfinite_threshold_is_rejected(self):
        config = manifest()
        config["thresholds"]["max_gap_seconds"] = float("nan")
        with self.assertRaisesRegex(ValueError, "max_gap_seconds"):
            Evaluator(config)

    def test_nonfinite_movement_fails_closed(self):
        bad = event(2, 1, "movement", distance_m=float("inf"),
                    position=[1, 2, 3], quest_id=251)
        report = self.feed(event(1, 0, "heartbeat", wake_sequence=1, active=True),
                           bad).finish("bounded end", stamp(2))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertIn("distance_m", report["telemetry_fault"])

    def test_unrelated_invalid_actions_do_not_combine_into_failure(self):
        events = [event(1, 0, "heartbeat", wake_sequence=1, active=True),
                  terminal(2, 1, "a", "Move", 251, "Rejected", reason="bad route", failure_class="invalid"),
                  terminal(3, 2, "b", "Loot", 251, "Rejected", reason="bad route", failure_class="invalid"),
                  terminal(4, 3, "c", "TurnInQuest", 251, "Rejected", reason="bad route", failure_class="invalid")]
        report = self.feed(*events).finish("bounded end", stamp(4))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertEqual(0, report["repeated_invalid_group_count"])

    def test_different_invalid_reasons_do_not_combine(self):
        events = [event(1, 0, "heartbeat", wake_sequence=1, active=True),
                  terminal(2, 1, "a", "Move", 251, "Rejected", reason="route", failure_class="invalid"),
                  terminal(3, 2, "b", "Move", 251, "Rejected", reason="permission", failure_class="invalid"),
                  terminal(4, 3, "c", "Move", 251, "Rejected", reason="target", failure_class="invalid")]
        report = self.feed(*events).finish("bounded end", stamp(4))
        self.assertEqual("INCONCLUSIVE", report["status"])

    def test_unrelated_saved_completion_does_not_reset_objective_inactivity(self):
        events = [event(1, 0, "heartbeat", wake_sequence=1, active=True),
                  event(2, 20, "quest_completed", quest_id=900, before=False, after=True,
                        authority="saved completed_quests bit"),
                  event(3, 30, "heartbeat", wake_sequence=2, active=True)]
        # No observation gaps: expand the cap while preserving objective limit.
        config = manifest()
        config["thresholds"]["max_gap_seconds"] = 25
        evaluator = Evaluator(config)
        for item in events:
            evaluator.consume(item)
        report = evaluator.finish("bounded end", stamp(31))
        self.assertEqual("STALLED", report["status"])
        self.assertIsNone(report["last_meaningful_progress"])

    def test_untrusted_objective_progress_fails_closed(self):
        events = [event(1, 0, "heartbeat", wake_sequence=1, active=True),
                  event(2, 1, "objective_progress", quest_id=251, before=0, after=1,
                        authority="guessed from movement")]
        report = self.feed(*events).finish("bounded end", stamp(2))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertIsNone(report["last_meaningful_progress"])

    def test_same_sample_followup_choice_cannot_prove_order(self):
        events = self.success_events()
        events[7]["timestamp"] = events[5]["timestamp"]
        events[8]["timestamp"] = events[5]["timestamp"]
        events[9]["timestamp"] = stamp(6)
        # Put all records in observed-time order; A and B share a sample time.
        events = sorted(events, key=lambda e: e["timestamp"])
        report = self.feed(*events).finish("bounded end", stamp(11))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertFalse(report["criteria"]["independent_followup"])

    def test_native_failed_and_cancelled_terminals_are_valid_observations(self):
        events = [event(1, 0, "heartbeat", wake_sequence=1, active=True),
                  terminal(2, 1, "a", "Move", 251, "Failed", reason="navigation"),
                  terminal(3, 2, "b", "Move", 251, "Cancelled", reason="owner changed")]
        report = self.feed(*events).finish("bounded end", stamp(3))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertIsNone(report["telemetry_fault"])


class HuntModeTests(unittest.TestCase):
    @staticmethod
    def config():
        config = manifest()
        config["acceptance"] = {"quest_id": 266,
                                "objective": {"mode": "native_hunt", "act_id": 26549,
                                              "required_progress": 1},
                                "require_followup": True, "independent_selection": True}
        return config

    @staticmethod
    def completed_chain():
        return [
            event(1, 0, "heartbeat", wake_sequence=1, active=True),
            event(2, 1, "objective_progress", quest_id=266, act_id=26549,
                  before=0, after=1, authority="native quest progress"),
            terminal(3, 2, "turn-266", "TurnInQuest", 266),
            event(4, 3, "quest_completed", quest_id=266, before=False, after=True,
                  authority="saved completed_quests bit"),
            event(5, 4, "goal_selected", quest_id=267, reason="eligible offer",
                  policy="quest-v1", independent=True, trace_id="accept-267"),
            terminal(6, 5, "accept-267", "AcceptQuest", 267),
            event(7, 6, "quest_accepted", quest_id=267, before=False, after=True,
                  authority="saved quests row"),
        ]

    def report(self, events):
        evaluator = Evaluator(self.config())
        for entry in events:
            evaluator.consume(entry)
        return evaluator.finish("bounded end", stamp(7))

    def test_hunt_without_inventory_increase_passes_with_all_world_proofs(self):
        report = self.report(self.completed_chain())
        self.assertEqual("PASS", report["status"])
        self.assertTrue(all(report["criteria"].values()))
        self.assertNotIn("item_progress", report["criteria"])
        self.assertEqual("e2", report["criterion_evidence"]["hunt_progress"])
        self.assertEqual(1, sum(n["code"] == "HUNT_PROGRESS_OBSERVED"
                                for n in report["notifications"]))

    def test_wrong_act_or_quest_does_not_count_as_hunt_credit(self):
        for quest, act in ((266, 999), (999, 26549)):
            with self.subTest(quest=quest, act=act):
                events = self.completed_chain()
                events[1]["data"].update(quest_id=quest, act_id=act)
                report = self.report(events)
                self.assertEqual("INCONCLUSIVE", report["status"])
                self.assertFalse(report["criteria"]["hunt_progress"])
                self.assertIsNone(report["telemetry_fault"])

    def test_missing_hunt_credit_bit_or_followup_cannot_pass(self):
        for missing, index in (("credit", 1), ("bit", 3), ("followup", 4)):
            with self.subTest(missing=missing):
                events = self.completed_chain()
                if missing == "followup":
                    events = events[:index]
                else:
                    events.pop(index)
                report = self.report(events)
                self.assertEqual("INCONCLUSIVE", report["status"])
                self.assertFalse(all(report["criteria"].values()))

    def test_gap_overrides_complete_hunt_chain(self):
        events = self.completed_chain()
        events.append(event(8, 7, "telemetry_gap", reason="native progress unavailable"))
        report = self.report(events)
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertIsNotNone(report["telemetry_fault"])

    def test_decreasing_or_discontinuous_hunt_count_fails_closed(self):
        for before, after in ((1, 0), (0, 2)):
            with self.subTest(before=before, after=after):
                events = self.completed_chain()
                events.insert(2, event(8, 1.5, "objective_progress", quest_id=266,
                                       act_id=26549, before=before, after=after,
                                       authority="native quest progress"))
                report = self.report(events)
                self.assertEqual("INCONCLUSIVE", report["status"])
                self.assertFalse(report["criteria"]["hunt_progress"])
                self.assertIn("count decreased or skipped", report["telemetry_fault"])

    def test_wrong_authority_and_missing_act_fail_closed(self):
        for change in ({"authority": "guessed"}, {"act_id": None}):
            with self.subTest(change=change):
                events = self.completed_chain()
                events[1]["data"].update(change)
                report = self.report(events)
                self.assertEqual("INCONCLUSIVE", report["status"])
                self.assertIsNotNone(report["telemetry_fault"])

    def test_repeated_invalid_hunt_action_retains_fail_precedence(self):
        events = self.completed_chain()
        for number in range(3):
            events.append(terminal(10 + number, 6.1 + number * .1,
                          f"invalid-{number}", "Move", 266, "Rejected",
                          failure_class="invalid", reason="unsupported"))
        report = self.report(events)
        self.assertEqual("FAIL", report["status"])

    def test_late_hunt_credit_cannot_complete_an_earlier_episode(self):
        events = self.completed_chain()
        credit = events.pop(1)
        credit["timestamp"] = stamp(6.5)
        events.append(credit)
        report = self.report(events)
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertFalse(report["criteria"]["hunt_progress"])
        self.assertIsNone(report["criterion_evidence"]["hunt_progress"])
        credit["source"]["native_timestamp"] = stamp(1)
        self.assertEqual("PASS", self.report(events)["status"])
        credit["source"]["native_timestamp"] = stamp(8)
        self.assertIsNotNone(self.report(events)["telemetry_fault"])

    def test_objective_mode_requires_positive_contract(self):
        for objective in ({"mode": "native_hunt", "act_id": 0, "required_progress": 1},
                          {"mode": "native_hunt", "act_id": 26549, "required_progress": 0},
                          {"mode": "unknown", "act_id": 26549, "required_progress": 1}):
            with self.subTest(objective=objective):
                config = self.config()
                config["acceptance"]["objective"] = objective
                with self.assertRaises(ValueError):
                    Evaluator(config)


if __name__ == "__main__":
    unittest.main()
