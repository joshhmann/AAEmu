# PlayerTrace Task Specification & Evaluation Standard

This document defines the official standard for specifying new human trace tasks and evaluating collected trace files in AAEmu.

---

## 1. Core Purpose

The goal of the Human Action Trace corpus is to establish empirical ground truth for player behavior across ArcheAge mechanics without manual guessing.

To ensure trace quality, every trace task must have:
1. An unambiguous **Task Specification** (what action is being tested, preparation, in-game steps, and target wire packets).
2. A deterministic **Evaluation Protocol** that automatically validates whether a captured trace satisfies the specification.

---

## 2. Task Specification Standard (Schema)

Every task is defined as a structured JSON record with the following schema:

```json
{
  "id": "combat_basic_melee",
  "title": "Basic Melee Combat & Auto-Attack",
  "category": "Combat",
  "priority": "High",
  "scenario": "combat_basic_melee",
  "status": "Pending",
  "description": "Engage a solitary, low-level hostile NPC using standard melee weapon strikes and auto-attacks.",
  "target_packets": [
    "CSSetTargetPacket",
    "CSAttackPacket",
    "SCDamagePacket",
    "SCSkillDamagePacket"
  ],
  "target_events": [
    "targeting:target_selected"
  ],
  "instructions": [
    "Locate a solitary hostile mob in an open area.",
    "Stand at ~15m range with weapon drawn.",
    "Execute `/trace start combat_basic_melee` in chat.",
    "Target the hostile unit by clicking or pressing Tab.",
    "Approach into melee range and initiate auto-attack.",
    "Defeat the enemy and wait 2 seconds.",
    "Execute `/trace stop` in chat."
  ],
  "checklist": [
    { "id": "step1", "text": "Execute `/trace start combat_basic_melee`", "done": false, "required": true },
    { "id": "step2", "text": "Acquire target lock on hostile mob", "done": false, "required": true },
    { "id": "step3", "text": "Execute melee attack and record damage packet", "done": false, "required": true },
    { "id": "step4", "text": "Wait for mob defeat / death state", "done": false, "required": true },
    { "id": "step5", "text": "Execute `/trace stop`", "done": false, "required": true }
  ],
  "notes": ""
}
```

### Required Fields Reference

| Field | Type | Description |
| --- | --- | --- |
| `id` | string | Unique kebab_case identifier (matches `scenario`). |
| `title` | string | Human-readable action name. |
| `category` | enum | `Combat`, `Quests`, `Crafting`, `Inventory`, `Economy`, `NPC Services`, `Farming`, `Vehicles`, `Mates`. |
| `priority` | enum | `High` (untraced family), `Medium` (partially traced), `Low` (auxiliary/edge). |
| `scenario` | string | Exact scenario token passed to `/trace start <scenario>`. |
| `target_packets` | string[] | Array of mandatory packet class names that MUST appear in the trace. |
| `instructions` | string[] | Ordered sequence of physical/in-game actions for the human operator. |
| `checklist` | object[] | Granular verification checklist items. Items with `required: true` gate task completion. |

---

## 3. Evaluation Protocol

When a trace is completed (`traces/player-actions/<scenario>__<char>__<ts>.jsonl`), the evaluation engine assesses it across 5 criteria:

### Criterion 1: Stream Integrity & Boundaries
- **Check:** File parses with `0` malformed lines.
- **Check:** Starts with `category: "lifecycle", event: "trace_started"`.
- **Check:** Ends with `category: "lifecycle", event: "trace_stopped"`.
- *Failure:* If total records is 0, verdict is `FAIL`. Missing start/stop yields `CAVEAT`.

### Criterion 2: Target Packet Assertions
- **Check:** For every packet in `target_packets`, `count >= 1`.
- *Failure:* If any mandatory target packet is absent, verdict is `FAIL`.

### Criterion 3: Refusal & Error Checks
- **Check:** Count occurrences of `category: "refusal"` and `SCErrorMsgPacket`.
- *Notice:* Refusals indicate illegal distance, insufficient labor, or action lockout. Emits a `CAVEAT` warning.

### Criterion 4: Normalized Sequence Alignment
- **Check:** When noise packets (`PingPacket`, `PongPacket`, `/trace` echoes) and repetitive movement updates are collapsed, the remaining tokens must form a coherent action skeleton.

### Criterion 5: Semantic Non-Equivalence Verification
- **Rule:** Never assume an action is equivalent to an existing family merely because they share 1 packet (e.g. `CSCreateDoodadPacket` is not `Skill.Use`).

---

## 4. Evaluation Verdicts

**Scope of these verdicts:** PASS/CAVEAT/FAIL describe the named trace-capture
contract. They do not automatically establish correct gameplay, complete loops or
human acceptance. Use the [discovery-to-delivery contract](../../.kanban-templates/implementation.md)
to join raw corpus evidence with canonical requirements, actual source paths and
authoritative state consequences. Preserve producer/build/scenario provenance;
synthetic input remains synthetic after evaluation. When evidence is missing,
specify the smallest capture question rather than assuming the feature is absent.

- **`PASS`**: All mandatory target packets present, lifecycle boundaries clean, zero malformed records, zero fatal server refusals.
- **`CAVEAT`**: All mandatory target packets present, but anomalies detected (missing stop lifecycle, high refusal count, or non-fatal server errors).
- **`FAIL`**: Missing one or more mandatory target packets, empty trace file, or completely unparsed payload.

---

## 5. CLI Usage

Evaluate any trace file directly from the terminal:

```bash
# Evaluate against a specific task definition
python3 Scripts/playertrace-coverage/task_evaluator.py \
    traces/player-actions/buy_chick__Dingus__20260914_072441.jsonl \
    --task-id buy_chick

# Auto-evaluate the latest trace for a task ID
python3 Scripts/playertrace-coverage/task_evaluator.py \
    --task-id combat_basic_melee
```

Machine-readable JSON output for CI / automation:
```bash
python3 Scripts/playertrace-coverage/task_evaluator.py \
    traces/player-actions/harvest_chicken__Dingus__20260914_072511.jsonl \
    --task-id harvest_chicken \
    --json
```

---

## 6. Dashboard Integration

In the web dashboard (`http://192.168.0.187:8085`):
- Click **`🔍 Evaluate Latest Trace`** on any task card to instantly run the evaluator against the newest trace file.
- View real-time packet assertion badges (`[PASS]` / `[FAIL]`) and caveat diagnostics directly in the UI.
