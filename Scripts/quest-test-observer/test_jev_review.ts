import { describe, expect, test } from "bun:test";
import { interpretAnswer, reviewPacket, validatePacket } from "./jev_review";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

function fixture(status = "STALLED") {
    const report = { status, run_id: "run-1", bot_id: 297, source_sha: "5914c3e7",
        database_id: "game-1", evidence_layer: "historical replay" };
    const packet = {
        schema: "aaemu-jev-triage-v1", deterministic_status: status,
        identity: { run_id: report.run_id, bot_id: report.bot_id,
            source_sha: report.source_sha, database_id: report.database_id },
        evidence_layer: report.evidence_layer,
        acceptance: { required: { quest_id: 251 }, observed: { turn_in: false } },
        provenance: { verdict_path: "/saved/verdict.json", verdict_sha256: "a".repeat(64),
            events_path: "/saved/events.jsonl", events_sha256: "b".repeat(64),
            raw_path: "/saved/raw.log", raw_sha256: "c".repeat(64), consumed_prefix_sha256: "d".repeat(64) },
        first_discrepancy: { event_id: "e1", reason: "Earliest sampled unchanged wake; not a proven cause." },
        events: [{ event_id: "e1", timestamp: "2026-10-12T00:00:00Z", kind: "decision_snapshot",
            data: { trace_id: "trace-exact-1", goal: "Travel", wake_sequence: 2 },
            source: { artifact: "/saved/raw.log", line: 7, field: "decision_snapshot", layer: "historical replay" } }],
        hypotheses: [
            { id: "unchanged_wake", summary: "The sampled wake did not advance the observed decision.",
                supporting_event_ids: ["e1"], missing_evidence: ["Scheduler enrollment and next wake are not sampled."],
                check: "Compare trace-exact-1 with scheduler enrollment and subsequent wake telemetry; do not drive gameplay." },
            { id: "pending_action", summary: "A legitimately pending travel action may explain the unchanged wake.",
                supporting_event_ids: ["e1"], missing_evidence: ["No correlated travel terminal is included."],
                check: "Read trace-exact-1 travel lifecycle and measured displacement in the preserved raw telemetry." },
            { id: "unknown", summary: "UNKNOWN: the sample cannot distinguish a pending action from a missed wake.",
                supporting_event_ids: [], missing_evidence: ["Full correlated wake/action lifecycle."],
                check: "Read the omitted identity-matched wake/action lifecycle before choosing a repair." },
        ],
        coverage: { total_events: 4, included_events: 1, omitted_events: 3,
            bounds_hit: [], selection_scope: "Earliest discrepancy and its sampled trace; later events omitted." },
        abstention_reason: null as string | null,
        test_execution: { status: "RUNNING", cleanup: { status: "PENDING" } },
    };
    return { report, packet };
}

function validatedFixture() {
    const { report, packet } = fixture();
    const parsed = validatePacket(report, JSON.stringify(packet));
    if (!parsed.packet) throw new Error(`Invalid test fixture: ${parsed.reason}`);
    return parsed.packet;
}

describe("bounded interpretation", () => {
    test("returns exact engineer-authored refs and check, not generated actions or alleged cause", () => {
        const packet = validatedFixture();
        const result = interpretAnswer(packet, { answers: { hypothesis: {
            choice: "pending_action", confidence: 0.64,
            summary: "Scheduler is certainly broken", supporting_event_ids: ["fabricated-event"],
            recommended_next_engineering_action: "Teleport the bot and retry", cause_is_proven: true,
        } } });
        expect(result).toEqual({ hypothesis_id: "pending_action",
            summary: packet.hypotheses[1].summary, confidence: 0.64, cause_is_proven: false,
            supporting_event_ids: ["e1"], missing_evidence: packet.hypotheses[1].missing_evidence,
            recommended_next_engineering_action: packet.hypotheses[1].check });
    });

    test("unknown is an honest completed choice with no invented causal evidence", async () => {
        const { report, packet } = fixture();
        const review = await reviewPacket(report, JSON.stringify(packet), async () => ({
            answers: { hypothesis: { choice: "unknown", confidence: 0.85 } },
        }), () => 1);
        expect(review.review_status).toBe("completed");
        expect(review.status).toBe("STALLED");
        expect(review.interpretation.hypothesis_id).toBe("unknown");
        expect(review.interpretation.supporting_event_ids).toEqual([]);
        expect(review.interpretation.cause_is_proven).toBe(false);
        expect(review.backend.http_attempts).toBe(1);
    });

    test("rejects unknown candidates, unconstrained no_issue and malformed confidence", () => {
        const packet = validatedFixture();
        for (const choice of ["fabricated-event", "scheduler", "no_issue", "PASS", ""]) {
            expect(interpretAnswer(packet, { answers: { hypothesis: { choice, confidence: 0.8 } } })).toBeNull();
        }
        for (const confidence of [-0.1, 1.1, NaN, Infinity, "0.8", null, undefined]) {
            expect(interpretAnswer(packet, { answers: { hypothesis: { choice: "pending_action", confidence } } })).toBeNull();
        }
        for (const result of [null, {}, { answers: {} }, { answers: { status: { choice: "STALLED", confidence: 1 } } }]) {
            expect(interpretAnswer(packet, result)).toBeNull();
        }
    });

    test("no_issue only selects a supplied clean-success candidate", async () => {
        const { report, packet } = fixture("PASS");
        packet.test_execution.status = "PASS";
        packet.hypotheses.push({ id: "no_issue", summary: "No sampled acceptance or native failure is present.",
            supporting_event_ids: ["e1"], missing_evidence: [], check: "Read the saved acceptance evidence without replaying gameplay." });
        const review = await reviewPacket(report, JSON.stringify(packet), async () => ({
            answers: { hypothesis: { choice: "no_issue", confidence: 0.9 } },
        }));
        expect(review.review_status).toBe("completed");
        expect(review.interpretation.hypothesis_id).toBe("no_issue");
        packet.test_execution.status = "FAIL";
        expect(validatePacket(report, JSON.stringify(packet)).reason).toBe("invalid_triage_packet");
    });

    test("provisional native acceptance and pending cleanup stay visible, never an empty no_issue", async () => {
        const { report, packet } = fixture("PASS");
        const provisional = { ...packet, test_execution: {
            review_phase: "provisional", cleanup_state: "pending", native_test_returncode: null,
        } };
        const review = await reviewPacket(report, JSON.stringify(provisional), async () => ({
            answers: { hypothesis: { choice: "unknown", confidence: 0.7 } },
        }));
        expect(review.review_status).toBe("completed");
        expect(review.status).toBe("PASS");
        expect(review.test_execution).toEqual(provisional.test_execution);
        expect(review.coverage).toEqual(packet.coverage);
        provisional.hypotheses.push({ id: "no_issue", summary: "Not justified while native acceptance is pending.",
            supporting_event_ids: [], missing_evidence: [], check: "Read saved native acceptance." });
        expect(validatePacket(report, JSON.stringify(provisional)).reason).toBe("invalid_triage_packet");
    });
});

describe("packet eligibility and immutable verdicts", () => {
    test("missing telemetry abstains without invoking judgment and preserves provenance/coverage", async () => {
        const { report, packet } = fixture();
        packet.abstention_reason = "missing_telemetry";
        let calls = 0;
        const review = await reviewPacket(report, JSON.stringify(packet), async () => {
            calls++;
            throw new Error("Must not be invoked");
        });
        expect(calls).toBe(0);
        expect(review.review_status).toBe("abstained");
        expect(review.status).toBe(report.status);
        expect(review.interpretation.hypothesis_id).toBe("unknown");
        expect(review.first_discrepancy).toEqual(packet.first_discrepancy);
        expect(review.coverage).toEqual(packet.coverage);
        expect(review.provenance).toEqual(packet.provenance);
        expect(review.test_execution).toEqual(packet.test_execution);
        expect(review.backend.http_attempts).toBe(0);
    });

    test("rejects foreign identity, dangling refs, duplicate IDs, inconsistent coverage and provenance", () => {
        const { report, packet } = fixture();
        const cases = [
            { ...packet, identity: { ...packet.identity, bot_id: 165 } },
            { ...packet, deterministic_status: "PASS" },
            { ...packet, evidence_layer: "fresh live run" },
            { ...packet, events: [packet.events[0], packet.events[0]] },
            { ...packet, hypotheses: [{ ...packet.hypotheses[0], supporting_event_ids: ["omitted-event"] }, packet.hypotheses[2]] },
            { ...packet, first_discrepancy: { event_id: "omitted-event", reason: "Not in packet" } },
            { ...packet, coverage: { ...packet.coverage, omitted_events: 0 } },
            { ...packet, provenance: { ...packet.provenance, events_sha256: "not-a-hash" } },
            { ...packet, hypotheses: [packet.hypotheses[0]] },
            { ...packet, hypotheses: [packet.hypotheses[2], packet.hypotheses[2]] },
        ];
        for (const value of cases) expect(validatePacket(report, JSON.stringify(value)).reason).toBe("invalid_triage_packet");
    });

    test("malformed or oversized UTF-8 packets abstain before any judgment", async () => {
        const { report, packet } = fixture();
        for (const raw of ["not-json", JSON.stringify({ ...packet, extra: "é".repeat(9000) })]) {
            let calls = 0;
            const review = await reviewPacket(report, raw, async () => { calls++; return {}; });
            expect(calls).toBe(0);
            expect(review.review_status).toBe("abstained");
            expect(review.status).toBe("STALLED");
            expect(review.interpretation.hypothesis_id).toBe("unknown");
        }
    });

    test("backend failures preserve every deterministic status and do not expose errors or retry", async () => {
        for (const status of ["PASS", "FAIL", "STALLED", "INCONCLUSIVE"]) {
            const { report, packet } = fixture(status);
            let calls = 0;
            const review = await reviewPacket(report, JSON.stringify(packet), async () => {
                calls++;
                throw new Error("secret bearer token in backend message");
            }, () => 1);
            expect(calls).toBe(1);
            expect(review.review_status).toBe("unavailable");
            expect(review.status).toBe(status);
            expect(review.deterministic_status_locked).toBe(true);
            expect(review.interpretation.hypothesis_id).toBe("unknown");
            expect(review.backend.http_attempts).toBe(1);
            expect(JSON.stringify(review)).not.toContain("secret bearer token");
            expect(review.no_agent_session).toBe(true);
            expect(review.gameplay_actions).toBe(0);
            expect(review.source_tools).toBe(0);
        }
    });

    test("invalid model answer becomes UNKNOWN, never arbitrary event/action or changed status", async () => {
        const { report, packet } = fixture("FAIL");
        const review = await reviewPacket(report, JSON.stringify(packet), async () => ({
            answers: { hypothesis: { choice: "arbitrary-action", confidence: 1 } },
        }));
        expect(review.review_status).toBe("invalid");
        expect(review.status).toBe("FAIL");
        expect(review.interpretation.hypothesis_id).toBe("unknown");
        expect(review.interpretation.supporting_event_ids).toEqual([]);
        expect(review.interpretation.recommended_next_engineering_action).toBe(packet.hypotheses[2].check);
        expect(JSON.stringify(review)).not.toContain("arbitrary-action");
    });

    test("SDK malformed-response errors are invalid review, not gameplay failure", async () => {
        const { report, packet } = fixture("PASS");
        const review = await reviewPacket(report, JSON.stringify(packet), async () => {
            const error = new Error("untrusted response content");
            error.name = "ProviderResponseError";
            throw error;
        });
        expect(review.review_status).toBe("invalid");
        expect(review.status).toBe("PASS");
        expect(review.interpretation.hypothesis_id).toBe("unknown");
        expect(JSON.stringify(review)).not.toContain("untrusted response content");
    });
});

test("CLI flushes a complete large source-bound report through a pipe", async () => {
    const root = mkdtempSync(join(tmpdir(), "aaemu-triage-pipe-"));
    try {
        const { report } = fixture("INCONCLUSIVE");
        const identity = { run_id: report.run_id, bot_id: report.bot_id,
            source_sha: report.source_sha, database_id: report.database_id };
        const reason = "missing-context-" + "x".repeat(10000);
        const source = { artifact: "preserved/raw.jsonl", line: 2, field: "gap",
            layer: report.evidence_layer };
        const events = [
            { schema: "aaemu.quest-observer/v1", ...identity, event_id: "e1",
                timestamp: "2026-10-12T00:00:00Z", kind: "heartbeat",
                data: { wake_sequence: 1, active: true, scheduler_running: true },
                source: { ...source, line: 1, field: "heartbeat" } },
            { schema: "aaemu.quest-observer/v1", ...identity, event_id: "e2",
                timestamp: "2026-10-12T00:00:01Z", kind: "telemetry_gap",
                data: { reason }, source },
        ];
        const verdict = { ...report, schema: "aaemu.quest-observer/v1",
            acceptance_contract: { quest_id: 251, item_id: 4058, item_count: 3,
                require_followup: true, independent_selection: true },
            criteria: { turn_in_terminal: false, authoritative_completion: false,
                independent_followup: false, item_progress: false }, telemetry_fault: "missing",
            counts: { events: 2 } };
        writeFileSync(join(root, "verdict.json"), JSON.stringify(verdict));
        writeFileSync(join(root, "events.jsonl"), events.map(event => JSON.stringify(event)).join("\n") + "\n");
        const child = Bun.spawn(["bun", join(import.meta.dir, "jev_review.ts"), join(root, "verdict.json")],
            { stdout: "pipe", stderr: "pipe" });
        const review = JSON.parse(await new Response(child.stdout).text());
        expect(await child.exited).toBe(0);
        expect(review.status).toBe("INCONCLUSIVE");
        expect(review.review_status).toBe("abstained");
        expect(review.evidence.find(event => event.event_id === "e2").data.reason).toBe(reason);
        expect(review.evidence.find(event => event.event_id === "e2").source).toEqual(source);
        expect(review.backend.http_attempts).toBe(0);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
