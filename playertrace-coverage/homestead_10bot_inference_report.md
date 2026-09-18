# AAEmu PlayerTrace Homestead Intent & Milestone Inference Report

> **Evidence Layer:** `SYNTHETIC ORCHESTRATION CONTRACT (Unit Rig)`
> **Synthetic Verdict:** **SYNTHETIC PASS** (synthetic scenario sequence only)
> **Live Verdict:** **N/A — synthetic input can never earn a live verdict**
> **Gameplay Loop Proof:** **UNPROVED (synthetic orchestration contract only)**
> **Evaluator:** `infer_homestead_traces.py` @ `cac214ff0f0d6a6230ce89dd1006f9126f58c569` (sha256 `a5812177b50ba144769e1ca6ea719ea2a3e1fc0d849359d666b2f4051b522357`)
> **Input Trace:** `scorecard-explorations/generated/m7-homestead-10bot-spike.jsonl` (100 audit records)
> **Input SHA-256:** `3c9827ad83d5bf48c03c20fa9e2fadd68c16e94b0e26e8db12aaf393b6e83b40`
> **Input Modified (UTC):** `2026-09-17T14:42:16Z`
> **Trace Window (UTC):** `2026-09-17T14:42:15Z` → `2026-09-17T14:42:15Z`
> **Producing Command:** `python3 Scripts/playertrace-coverage/infer_homestead_traces.py scorecard-explorations/generated/m7-homestead-10bot-spike.jsonl --out-dir playertrace-coverage`
> **Source HEAD:** `533bfcae5becc0264946dbdd1ecde3141595b8b3` · dirty: True (11 files)
> **Provenance Status:** RECORDED
> **Evaluation Method:** heuristic text matching over recorded audit fields; not resource conservation and not verified intent.
> **Multi-Bot Scale Claim:** UNSUPPORTED — trace carries 1 distinct actor_id value(s); run grouping is by spawn events, not actor identity.
> **Honesty Notice (AGENTS.md):** Trace contains explicit synthetic harness/fixture markers. This report validates the rig's synthetic orchestration sequence and actor contract ONLY. It does NOT prove live server, network, or human client gameplay (Live=UNKNOWN, H=UNKNOWN). A fully synthetic successful trace can never earn a live verdict.
> **Seeded / bypassed steps:** fake actor, manual position changes, manual request completion, state/material/ownership/construction overrides, and injected starter kit.
> **Superseded claim (2026-09-17):** an earlier revision of this report and of its generator labelled this same synthetic input `LIVE SERVER / NETWORK TRACE` and a bare `PASS (10/10 bots fully verified)` because the detector read non-existent `Payload`/`Target` fields instead of the trace's real `detail` field. That claim is withdrawn here; SCORECARD.md's 2026-09-16 evidence correction notice already rejects it. The historical text remains in git history. No gameplay or milestone evidence is promoted by this report.

## Macro Telemetry Summary

- **Total Starter Bots Evaluated:** 10
- **Sequence-Complete Bots:** 10
- **Synthetic-Pass Bots:** 10
- **Live-Pass Bots:** 0
- **Residential Plots Claimed:** 10
- **Farmsteads Constructed:** 10
- **Material Packs Crafted:** 10
- **Cumulative Transit Distance:** 5632.3 meters

## Inferred Bot Progression Matrix

| Bot # | Inferred Race | Starting Hub | Plot ID | Milestones | Distinct Actors | Inferred Goals | Verdict |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Bot 01** | Nuian | Solzreed Peninsula (Nuia) | `9000` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 02** | Nuian | Solzreed Peninsula (Nuia) | `9001` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 03** | Nuian | Solzreed Peninsula (Nuia) | `9002` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 04** | Elf | Gweonid Forest (Nuia) | `9003` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 05** | Elf | Gweonid Forest (Nuia) | `9004` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 06** | Harani (Hariharan) | Arcum Iris (Haranya) | `9005` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 07** | Harani (Hariharan) | Arcum Iris (Haranya) | `9006` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 08** | Harani (Hariharan) | Arcum Iris (Haranya) | `9007` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 09** | Firran (Ferre) | Falcon Plateau (Haranya) | `9008` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |
| **Bot 10** | Firran (Ferre) | Falcon Plateau (Haranya) | `9009` | 10/10 | 1 | GoalClaimHomestead, GoalErectHome | **SYNTHETIC PASS** |

## Detailed Milestone Inference Breakdown

### Bot #01 (Nuian)
- **Starting Origin:** Solzreed Peninsula (Nuia)
- **Plot Claimed:** House ID `9000`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #02 (Nuian)
- **Starting Origin:** Solzreed Peninsula (Nuia)
- **Plot Claimed:** House ID `9001`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #03 (Nuian)
- **Starting Origin:** Solzreed Peninsula (Nuia)
- **Plot Claimed:** House ID `9002`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #04 (Elf)
- **Starting Origin:** Gweonid Forest (Nuia)
- **Plot Claimed:** House ID `9003`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #05 (Elf)
- **Starting Origin:** Gweonid Forest (Nuia)
- **Plot Claimed:** House ID `9004`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #06 (Harani (Hariharan))
- **Starting Origin:** Arcum Iris (Haranya)
- **Plot Claimed:** House ID `9005`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #07 (Harani (Hariharan))
- **Starting Origin:** Arcum Iris (Haranya)
- **Plot Claimed:** House ID `9006`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #08 (Harani (Hariharan))
- **Starting Origin:** Arcum Iris (Haranya)
- **Plot Claimed:** House ID `9007`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #09 (Firran (Ferre))
- **Starting Origin:** Falcon Plateau (Haranya)
- **Plot Claimed:** House ID `9008`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

### Bot #10 (Firran (Ferre))
- **Starting Origin:** Falcon Plateau (Haranya)
- **Plot Claimed:** House ID `9009`
- **Distinct Actors In Run:** 1 (actor_id [0]) — distinct-actor claim: UNSUPPORTED — all records carry actor_id [0]
- **Run Integrity:** no duplicate ids, no non-terminal requests, timestamps ordered
- **Verdict:** **SYNTHETIC PASS**
- **Resource Conversions (heuristic text matches, not conservation):**
  - Scarecrow Designs: **1**
  - Tax Certificates: **10**
  - Saplings Planted: **1**
  - Timber Harvested: **1**
  - Packs Crafted: **1**
  - Homes Constructed: **1**
- **Detected Sequence Flow:**
  - `[OK]` BORN_IN_WORLD
  - `[OK]` STARTER_KIT_VERIFIED
  - `[OK]` HOUSING_ZONE_REACHED
  - `[OK]` PLOT_CLAIMED
  - `[OK]` CROPS_CULTIVATED
  - `[OK]` TIMBER_HARVESTED
  - `[OK]` WORKBENCH_REACHED
  - `[OK]` MATERIAL_PACK_CRAFTED
  - `[OK]` PACK_DELIVERED_TO_SITE
  - `[OK]` HOMESTEAD_CONSTRUCTED

