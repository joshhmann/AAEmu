# AAEmu PlayerTrace Coverage Mapper

Coverage-guided human action trace analysis utility for AAEmu.

## Overview

As human action traces are collected via `PlayerTraceService`, manual examination of every trace file becomes impractical. The **PlayerTrace Coverage Mapper** analyzes trace corpora against the authoritative AAEmu packet inventory, producing:

- Comprehensive packet and semantic event coverage metrics.
- Noise-filtered, normalized action sequence skeletons and n-gram transitions.
- Scenario-to-feature matrix and Jaccard similarity indices.
- Conservative candidate action family groupings (labeled `POSSIBLE RELATED ACTIONS`).
- Coverage gap taxonomy and ranked next-trace recommendations.
- Baseline delta comparison across successive play sessions.

> [!CAUTION]
> **CORRECTNESS MANDATE — PACKET SIMILARITY IS NOT SEMANTIC EQUIVALENCE**
>
> Packet co-occurrence and sequence similarity provide structural evidence of how client-server interactions unfold. They **do not prove** that two actions share the same underlying server implementation or game mechanics.
>
> For instance, placing a chick and planting a potato both emit `CSCreateDoodadPacket`, but potato planting involves crop growth timers while chick placement instantiates an animal entity. Harvesting a crop and gathering an animal product both use `CSStartInteractionPacket` &rarr; `CSStartSkillPacket`, but interact with distinct game data templates.
>
> Never treat candidate action families as proof of gameplay equivalence without corroborating server handler implementations.

---

## Directory & File Structure

```text
Scripts/playertrace-coverage/
├── playertrace_coverage.py            # Main CLI entry point & report generator
├── coverage_engine.py                 # Streaming metrics, n-grams, similarity, gap analytics
├── packet_inventory.py                # Static scanner for AAEmu packet classes & opcodes
├── trace_parser.py                    # High-throughput, fault-tolerant JSONL stream reader
├── task_evaluator.py                  # Automated task evaluation & packet assertion engine
├── dashboard_server.py                # Interactive web dashboard HTTP server (:8085)
├── test_coverage_mapper.py            # Unit test suite (python3 -m unittest)
├── TASK_SPEC_AND_EVALUATION_GUIDE.md  # Standard schema & evaluation protocol documentation
└── README.md                          # This documentation
```

---

## Requirements

- Python 3.9+ (Standard Library only — zero external pip dependencies).

---

## Usage

### 1. Full Corpus Analysis

Run against the default trace directory (`traces/player-actions/`):

```bash
python3 Scripts/playertrace-coverage/playertrace_coverage.py \
    traces/player-actions \
    --repo-root . \
    --out playertrace-coverage
```

### 2. Compare Against a Baseline (Delta Mode)

After a new play session, compare current findings with a previous run:

```bash
python3 Scripts/playertrace-coverage/playertrace_coverage.py \
    traces/player-actions \
    --repo-root . \
    --out playertrace-coverage-new \
    --baseline playertrace-coverage/summary.json
```

### 3. Filter to Specific Scenarios or Thresholds

```bash
python3 Scripts/playertrace-coverage/playertrace_coverage.py \
    traces/player-actions \
    --scenario pick_potato harvest_chicken \
    --similarity-threshold 0.40 \
    --min-count 2
```

---

## Interactive Task Dashboard

Launch the interactive web dashboard for real-time task instructions, required checklists, one-click copy, and trace evaluation:

```bash
python3 Scripts/playertrace-coverage/dashboard_server.py --port 8085 --host 0.0.0.0 --repo-root .
```

Access in your browser:
- LAN / Remote: `http://192.168.0.187:8085`
- Localhost: `http://localhost:8085`

---

## Automated Task Evaluation

Verify a trace file against target packet assertions and lifecycle integrity:

```bash
# Evaluate a specific trace file against a task ID
python3 Scripts/playertrace-coverage/task_evaluator.py \
    traces/player-actions/buy_chick__Dingus__20260914_072441.jsonl \
    --task-id buy_chick

# Auto-discover and evaluate the latest trace for a task ID
python3 Scripts/playertrace-coverage/task_evaluator.py \
    --task-id combat_basic_melee
```

For schema and protocol details, see [`TASK_SPEC_AND_EVALUATION_GUIDE.md`](TASK_SPEC_AND_EVALUATION_GUIDE.md).

---

## Generated Artifacts

The tool generates the following outputs in the target `--out` directory:

| Artifact | Format | Description |
| --- | --- | --- |
| `summary.md` | Markdown | Executive coverage report, sequence skeletons, candidate families, and next traces. |
| `summary.json` | JSON | Machine-readable analytics snapshot (`schema_version: 1`) for dashboards or CI gates. |
| `packet-coverage.csv` | CSV | All AAEmu packets: count, scenarios seen, source path, opcode, and status (`OBSERVED_AND_KNOWN`, `DISCOVERY_LEAD`, `OBSERVED_UNMAPPED`). |
| `event-coverage.csv` | CSV | Non-packet semantic events (`skill`, `world`, `interaction`, `resource`, `movement`). |
| `scenario-matrix.csv` | CSV | Matrix of observed packet/event occurrence counts across scenarios. |
| `packet-transitions.csv` | CSV | Adjacent 2-gram normalized event transitions with frequency and scenario membership. |
| `sequence-patterns.csv` | CSV | Common 2-gram and 3-gram action skeletons. |
| `scenario-similarity.csv` | CSV | Pairwise scenario Jaccard similarity and transition overlap. |
| `cooccurrence.csv` | CSV | Pairwise event co-occurrence across scenarios with joint support indices. |

---

## Running Unit Tests

Execute the automated test suite with standard Python unittest:

```bash
python3 -m unittest discover -s Scripts/playertrace-coverage -p "test_*.py" -v
```

---

## Interpreting Outputs

1. **Discovery Leads vs Missing Gameplay**:
   Unobserved AAEmu packet classes are classified as `DISCOVERY_LEAD`, not missing gameplay requirements. Many packets belong to server-internal, GM/admin, proxy, or legacy mechanics not expected during ordinary player gameplay.
2. **Normalized Action Sequences**:
   High-frequency background traffic (pings/pongs, server time broadcasts, operator `/trace` chat commands) and continuous unit movement jitter (`SCOneUnitMovementPacket`) are collapsed in normalized sequence analysis so that core gameplay mechanics are clearly exposed.
3. **Candidate Families**:
   Groupings represent observed structural overlap. Always review the `key_differences` section to see what distinguishes members within a family.
