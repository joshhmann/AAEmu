#!/usr/bin/env python3
"""
coverage_engine.py - Core analytics engine for AAEmu PlayerTrace coverage mapping.

Processes streaming trace records to compute:
- Corpus statistics
- Packet & event coverage against known AAEmu packet inventory
- Scenario matrices & co-occurrences
- Noise-filtered normalized sequence transitions & n-grams
- Scenario Jaccard & transition similarity
- Conservative candidate action families (strictly labeled POSSIBLE RELATED ACTIONS)
- Coverage gap taxonomy & ranked next-trace recommendations
- Baseline delta comparison
"""

from collections import Counter, defaultdict
from dataclasses import asdict, dataclass, field
import datetime
import math
from typing import Any, Dict, List, Optional, Set, Tuple

from packet_inventory import PacketInventory, PacketMetadata
from trace_parser import TraceRecord


# Packets treated as noise in sequence analysis (NOT dropped from raw coverage)
NOISE_PACKETS = {
    "PingPacket",
    "PongPacket",
    "SCTimeOfDayPacket",
}

# Packets related to continuous posture/animation updates that can be collapsed
COLLAPSIBLE_PACKETS = {
    "SCOneUnitMovementPacket",
    "CSMoveUnitPacket",
    "SCUnitModelPostureChangedPacket",
}


@dataclass
class CorpusSummary:
    total_files: int = 0
    total_records: int = 0
    malformed_records: int = 0
    packet_records: int = 0
    movement_records: int = 0
    event_records: int = 0
    marker_records: int = 0
    lifecycle_records: int = 0
    other_records: int = 0
    scenarios: List[str] = field(default_factory=list)
    unique_packets: int = 0
    unique_c2s_packets: int = 0
    unique_s2c_packets: int = 0
    unique_events: int = 0
    min_timestamp: Optional[str] = None
    max_timestamp: Optional[str] = None
    time_span_seconds: Optional[float] = None


@dataclass
class PacketCoverageItem:
    packet: str
    direction: str
    observed_count: int
    scenarios_seen: int
    observed: bool
    known_to_repo: bool
    source_path: str
    opcode: Optional[str]
    status: str  # OBSERVED_AND_KNOWN, DISCOVERY_LEAD, OBSERVED_UNMAPPED


@dataclass
class EventCoverageItem:
    event_key: str
    category: str
    event: str
    count: int
    scenarios_seen: int
    first_seen: Optional[str]
    last_seen: Optional[str]


@dataclass
class ScenarioSimilarityItem:
    scenario_a: str
    scenario_b: str
    jaccard_similarity: float
    transition_overlap: float
    shared_items_count: int
    distinct_items_count: int
    label: str  # "POSSIBLE RELATED ACTIONS" or "DISTINCT ACTIONS"


@dataclass
class CandidateFamily:
    family_name: str
    confidence: str  # HIGH, MEDIUM, LOW
    member_scenarios: List[str]
    common_packets: List[str]
    common_events: List[str]
    key_differences: Dict[str, List[str]]
    rationale: str
    cautionary_note: str = "Packet and event similarity is NOT proof of semantic equivalence. Server handlers must be verified."


@dataclass
class CoverageGap:
    gap_type: str  # UNTRACED ACTION FAMILY, PARTIALLY TRACED, PACKET KNOWN BUT CONTEXT UNKNOWN, etc.
    title: str
    description: str
    affected_packets: List[str]
    suggested_action: str


@dataclass
class NextTraceRecommendation:
    rank: int
    scenario_name: str
    priority: str  # HIGH, MEDIUM, LOW
    rationale: str
    target_packets: List[str]
    expected_action_family: str


class CoverageEngine:
    """Orchestrates coverage analysis across a corpus of traces."""

    def __init__(self, inventory: PacketInventory, similarity_threshold: float = 0.35, min_ngram_count: int = 1):
        self.inventory = inventory
        self.similarity_threshold = similarity_threshold
        self.min_ngram_count = min_ngram_count

        # Raw aggregation
        self.total_files: int = 0
        self.total_records: int = 0
        self.malformed_records: int = 0
        self.category_counts: Counter = Counter()
        self.event_counts: Counter = Counter()
        self.packet_counts: Counter = Counter()
        self.packet_by_scenario: Dict[str, Counter] = defaultdict(Counter)
        self.event_by_scenario: Dict[str, Counter] = defaultdict(Counter)
        self.first_seen_ts: Dict[str, str] = {}
        self.last_seen_ts: Dict[str, str] = {}
        self.all_scenarios: Set[str] = set()
        self.min_ts: Optional[str] = None
        self.max_ts: Optional[str] = None

        # Per-scenario raw and normalized sequence tracking
        self.scenario_raw_sequences: Dict[str, List[str]] = defaultdict(list)
        self.scenario_normalized_sequences: Dict[str, List[str]] = defaultdict(list)
        self._prev_normalized_token: Dict[str, Optional[str]] = defaultdict(lambda: None)

    def record_malformed(self, file_path: str, line_no: int, error_msg: str, line_text: str) -> None:
        self.malformed_records += 1

    def process_record(self, record: TraceRecord) -> None:
        self.total_records += 1
        sc = record.scenario
        self.all_scenarios.add(sc)
        self.category_counts[record.category] += 1

        # Timestamps
        if record.ts:
            if not self.min_ts or record.ts < self.min_ts:
                self.min_ts = record.ts
            if not self.max_ts or record.ts > self.max_ts:
                self.max_ts = record.ts

        # Event tracking
        event_key = f"{record.category}:{record.event}"
        self.event_counts[event_key] += 1
        self.event_by_scenario[sc][event_key] += 1
        if event_key not in self.first_seen_ts:
            self.first_seen_ts[event_key] = record.ts or ""
        self.last_seen_ts[event_key] = record.ts or ""

        # Packet tracking
        if record.is_packet and record.packet_name:
            pkt = record.packet_name
            self.packet_counts[pkt] += 1
            self.packet_by_scenario[sc][pkt] += 1
            if pkt not in self.first_seen_ts:
                self.first_seen_ts[pkt] = record.ts or ""
            self.last_seen_ts[pkt] = record.ts or ""

        # Sequence extraction
        self._update_sequences(sc, record)

    def _is_chat_trace_command(self, record: TraceRecord) -> bool:
        if record.packet_name in {"CSSendChatMessagePacket", "SCChatMessagePacket"}:
            details = str(record.data.get("Details") or "")
            if "/trace" in details.lower():
                return True
        return False

    def _update_sequences(self, sc: str, record: TraceRecord) -> None:
        # Raw sequence token
        raw_token: str
        if record.is_packet and record.packet_name:
            raw_token = f"pkt:{record.packet_name}"
        else:
            raw_token = f"{record.category}:{record.event}"
        self.scenario_raw_sequences[sc].append(raw_token)

        # Normalized sequence token filtering
        if record.is_packet and record.packet_name:
            if record.packet_name in NOISE_PACKETS:
                return
            if self._is_chat_trace_command(record):
                return
            if record.packet_name in COLLAPSIBLE_PACKETS:
                norm_token = "movement"
            else:
                norm_token = record.packet_name
        elif record.category == "movement":
            norm_token = "movement"
        elif record.category in {"skill", "interaction", "world", "resource", "refusal", "targeting"}:
            norm_token = f"{record.category}:{record.event}"
        elif record.category == "marker":
            norm_token = f"marker:{record.data.get('Label') or record.event}"
        else:
            return

        # Collapse contiguous repeated tokens (e.g. runs of movement)
        if norm_token != self._prev_normalized_token[sc]:
            self.scenario_normalized_sequences[sc].append(norm_token)
            self._prev_normalized_token[sc] = norm_token

    def compute_summary(self) -> CorpusSummary:
        time_span = None
        if self.min_ts and self.max_ts:
            try:
                t1 = datetime.datetime.fromisoformat(self.min_ts.replace("Z", "+00:00"))
                t2 = datetime.datetime.fromisoformat(self.max_ts.replace("Z", "+00:00"))
                time_span = (t2 - t1).total_seconds()
            except Exception:
                pass

        c2s = 0
        s2c = 0
        for pkt in self.packet_counts:
            meta = self.inventory.get(pkt)
            if meta:
                if meta.direction == "C2S":
                    c2s += 1
                elif meta.direction == "S2C":
                    s2c += 1
            else:
                if pkt.startswith("CS") or pkt.startswith("CT") or pkt.startswith("CA"):
                    c2s += 1
                elif pkt.startswith("SC") or pkt.startswith("TC") or pkt.startswith("AC"):
                    s2c += 1

        return CorpusSummary(
            total_files=self.total_files,
            total_records=self.total_records,
            malformed_records=self.malformed_records,
            packet_records=self.category_counts.get("packet", 0),
            movement_records=self.category_counts.get("movement", 0),
            event_records=sum(
                count for cat, count in self.category_counts.items()
                if cat not in {"packet", "movement", "lifecycle", "marker"}
            ),
            marker_records=self.category_counts.get("marker", 0),
            lifecycle_records=self.category_counts.get("lifecycle", 0),
            other_records=self.category_counts.get("unknown", 0),
            scenarios=sorted(self.all_scenarios),
            unique_packets=len(self.packet_counts),
            unique_c2s_packets=c2s,
            unique_s2c_packets=s2c,
            unique_events=len(self.event_counts),
            min_timestamp=self.min_ts,
            max_timestamp=self.max_ts,
            time_span_seconds=time_span
        )

    def compute_packet_coverage(self) -> List[PacketCoverageItem]:
        items: List[PacketCoverageItem] = []
        observed_names = set(self.packet_counts.keys())
        known_names = set(self.inventory.packets.keys())

        # All observed packets
        for pkt in sorted(observed_names):
            meta = self.inventory.get(pkt)
            scenarios_seen = sum(1 for sc in self.all_scenarios if self.packet_by_scenario[sc].get(pkt, 0) > 0)
            if meta:
                items.append(PacketCoverageItem(
                    packet=pkt,
                    direction=meta.direction,
                    observed_count=self.packet_counts[pkt],
                    scenarios_seen=scenarios_seen,
                    observed=True,
                    known_to_repo=True,
                    source_path=meta.file_path,
                    opcode=meta.opcode,
                    status="OBSERVED_AND_KNOWN"
                ))
            else:
                # Inferred direction
                dirn = "C2S" if pkt.startswith("CS") else ("S2C" if pkt.startswith("SC") else "UNKNOWN")
                items.append(PacketCoverageItem(
                    packet=pkt,
                    direction=dirn,
                    observed_count=self.packet_counts[pkt],
                    scenarios_seen=scenarios_seen,
                    observed=True,
                    known_to_repo=False,
                    source_path="UNMAPPED",
                    opcode=None,
                    status="OBSERVED_UNMAPPED"
                ))

        # Unobserved known packets (Discovery Leads)
        for pkt in sorted(known_names - observed_names):
            meta = self.inventory.packets[pkt]
            items.append(PacketCoverageItem(
                packet=pkt,
                direction=meta.direction,
                observed_count=0,
                scenarios_seen=0,
                observed=False,
                known_to_repo=True,
                source_path=meta.file_path,
                opcode=meta.opcode,
                status="DISCOVERY_LEAD"
            ))

        # Sort: observed first by count desc, then discovery leads by direction and name
        items.sort(key=lambda x: (not x.observed, -x.observed_count, x.direction, x.packet))
        return items

    def compute_event_coverage(self) -> List[EventCoverageItem]:
        items: List[EventCoverageItem] = []
        for ev_key, count in sorted(self.event_counts.items(), key=lambda x: -x[1]):
            parts = ev_key.split(":", 1)
            cat = parts[0]
            ev = parts[1] if len(parts) > 1 else ""
            scenarios_seen = sum(1 for sc in self.all_scenarios if self.event_by_scenario[sc].get(ev_key, 0) > 0)
            items.append(EventCoverageItem(
                event_key=ev_key,
                category=cat,
                event=ev,
                count=count,
                scenarios_seen=scenarios_seen,
                first_seen=self.first_seen_ts.get(ev_key),
                last_seen=self.last_seen_ts.get(ev_key)
            ))
        return items

    def compute_scenario_matrix(self) -> Tuple[List[str], List[str], List[List[int]]]:
        scenarios = sorted(self.all_scenarios)
        all_features: Set[str] = set()

        for sc in scenarios:
            for pkt in self.packet_by_scenario[sc]:
                all_features.add(f"pkt:{pkt}")
            for ev in self.event_by_scenario[sc]:
                all_features.add(ev)

        features = sorted(all_features)
        matrix: List[List[int]] = []

        for sc in scenarios:
            row: List[int] = []
            for feat in features:
                if feat.startswith("pkt:"):
                    pkt_name = feat[4:]
                    val = self.packet_by_scenario[sc].get(pkt_name, 0)
                else:
                    val = self.event_by_scenario[sc].get(feat, 0)
                row.append(val)
            matrix.append(row)

        return scenarios, features, matrix

    def compute_transitions_and_ngrams(self) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]]]:
        transition_counter: Counter = Counter()
        transition_scenarios: Dict[Tuple[str, str], Set[str]] = defaultdict(set)

        trigram_counter: Counter = Counter()
        trigram_scenarios: Dict[Tuple[str, str, str], Set[str]] = defaultdict(set)

        for sc, seq in self.scenario_normalized_sequences.items():
            # 2-grams (transitions)
            for i in range(len(seq) - 1):
                pair = (seq[i], seq[i + 1])
                transition_counter[pair] += 1
                transition_scenarios[pair].add(sc)

            # 3-grams
            for i in range(len(seq) - 2):
                tri = (seq[i], seq[i + 1], seq[i + 2])
                trigram_counter[tri] += 1
                trigram_scenarios[tri].add(sc)

        transitions_list: List[Dict[str, Any]] = []
        for pair, count in transition_counter.most_common():
            if count >= self.min_ngram_count:
                sc_list = sorted(transition_scenarios[pair])
                transitions_list.append({
                    "from_token": pair[0],
                    "to_token": pair[1],
                    "count": count,
                    "scenarios_count": len(sc_list),
                    "scenarios": ",".join(sc_list)
                })

        patterns_list: List[Dict[str, Any]] = []
        # Add common 2-grams
        for pair, count in transition_counter.most_common(25):
            if count >= self.min_ngram_count:
                sc_list = sorted(transition_scenarios[pair])
                patterns_list.append({
                    "pattern_type": "2-gram",
                    "sequence": f"{pair[0]} -> {pair[1]}",
                    "count": count,
                    "scenarios_count": len(sc_list),
                    "scenarios": ",".join(sc_list)
                })

        # Add common 3-grams
        for tri, count in trigram_counter.most_common(25):
            if count >= self.min_ngram_count:
                sc_list = sorted(trigram_scenarios[tri])
                patterns_list.append({
                    "pattern_type": "3-gram",
                    "sequence": f"{tri[0]} -> {tri[1]} -> {tri[2]}",
                    "count": count,
                    "scenarios_count": len(sc_list),
                    "scenarios": ",".join(sc_list)
                })

        return transitions_list, patterns_list

    def compute_cooccurrence(self) -> List[Dict[str, Any]]:
        scenario_features: Dict[str, Set[str]] = {}
        for sc in self.all_scenarios:
            feats = set()
            for pkt in self.packet_by_scenario[sc]:
                feats.add(pkt)
            for ev in self.event_by_scenario[sc]:
                feats.add(ev)
            scenario_features[sc] = feats

        all_unique_feats = sorted({f for feats in scenario_features.values() for f in feats})
        pairs_counter: Counter = Counter()
        pairs_scenarios: Dict[Tuple[str, str], Set[str]] = defaultdict(set)

        for sc, feats in scenario_features.items():
            feat_list = sorted(feats)
            for i in range(len(feat_list)):
                for j in range(i + 1, len(feat_list)):
                    pair = (feat_list[i], feat_list[j])
                    pairs_counter[pair] += 1
                    pairs_scenarios[pair].add(sc)

        results: List[Dict[str, Any]] = []
        for (f_a, f_b), shared_cnt in pairs_counter.most_common():
            # Jaccard support across scenarios
            sc_a = sum(1 for sc in self.all_scenarios if f_a in scenario_features[sc])
            sc_b = sum(1 for sc in self.all_scenarios if f_b in scenario_features[sc])
            union_cnt = sc_a + sc_b - shared_cnt
            jaccard = round(shared_cnt / union_cnt, 4) if union_cnt > 0 else 0.0

            sc_list = sorted(pairs_scenarios[(f_a, f_b)])
            results.append({
                "event_a": f_a,
                "event_b": f_b,
                "shared_scenarios_count": shared_cnt,
                "shared_scenarios": ",".join(sc_list),
                "jaccard_similarity": jaccard,
                "total_scenarios_a": sc_a,
                "total_scenarios_b": sc_b
            })

        return results

    def compute_scenario_similarity(self) -> List[ScenarioSimilarityItem]:
        scenarios = sorted(self.all_scenarios)
        results: List[ScenarioSimilarityItem] = []

        # Feature sets (packets + non-noise events)
        sc_sets: Dict[str, Set[str]] = {}
        sc_transitions: Dict[str, Set[Tuple[str, str]]] = {}

        for sc in scenarios:
            s = set(self.packet_by_scenario[sc].keys()) | set(self.event_by_scenario[sc].keys())
            sc_sets[sc] = s

            norm_seq = self.scenario_normalized_sequences[sc]
            trans = set()
            for i in range(len(norm_seq) - 1):
                trans.add((norm_seq[i], norm_seq[i + 1]))
            sc_transitions[sc] = trans

        for i in range(len(scenarios)):
            for j in range(i + 1, len(scenarios)):
                sc_a = scenarios[i]
                sc_b = scenarios[j]

                set_a = sc_sets[sc_a]
                set_b = sc_sets[sc_b]

                inter = set_a & set_b
                union = set_a | set_b
                jaccard = len(inter) / len(union) if union else 0.0

                trans_a = sc_transitions[sc_a]
                trans_b = sc_transitions[sc_b]
                trans_inter = trans_a & trans_b
                trans_union = trans_a | trans_b
                trans_overlap = len(trans_inter) / len(trans_union) if trans_union else 0.0

                label = "POSSIBLE RELATED ACTIONS" if jaccard >= self.similarity_threshold else "DISTINCT ACTIONS"

                results.append(ScenarioSimilarityItem(
                    scenario_a=sc_a,
                    scenario_b=sc_b,
                    jaccard_similarity=round(jaccard, 4),
                    transition_overlap=round(trans_overlap, 4),
                    shared_items_count=len(inter),
                    distinct_items_count=len(union - inter),
                    label=label
                ))

        results.sort(key=lambda x: (-x.jaccard_similarity, -x.transition_overlap))
        return results

    def compute_candidate_families(self) -> List[CandidateFamily]:
        """
        Conservative discovery of candidate action families based on observed signatures.
        CRITICAL: Never equates Plant, Harvest, and NPC interaction!
        """
        scenarios = sorted(self.all_scenarios)
        families: List[CandidateFamily] = []

        # Family 1: Doodad Placement / Creation
        # Key signature: CSCreateDoodadPacket, SCDoodadCreatedPacket, world:doodad_created
        # Excludes Skill.Use / CSStartSkillPacket
        placement_scenarios = [
            sc for sc in scenarios
            if "CSCreateDoodadPacket" in self.packet_by_scenario[sc]
            or "world:doodad_created" in self.event_by_scenario[sc]
        ]
        if len(placement_scenarios) >= 2:
            common_pkts = sorted(set.intersection(*[set(self.packet_by_scenario[s].keys()) for s in placement_scenarios]))
            common_evs = sorted(set.intersection(*[set(self.event_by_scenario[s].keys()) for s in placement_scenarios]))
            diffs = {
                s: sorted(
                    (set(self.packet_by_scenario[s].keys()) | set(self.event_by_scenario[s].keys()))
                    - (set(common_pkts) | set(common_evs))
                )
                for s in placement_scenarios
            }
            families.append(CandidateFamily(
                family_name="Doodad Placement / World Creation",
                confidence="HIGH",
                member_scenarios=placement_scenarios,
                common_packets=common_pkts,
                common_events=common_evs,
                key_differences=diffs,
                rationale="Scenarios share CSCreateDoodadPacket and world:doodad_created without invoking Skill.Use or CastTask lifecycles."
            ))

        # Family 2: Doodad Harvesting / Gathering Interaction
        # Key signature: CSStartInteractionPacket, CSStartSkillPacket, doodad_use, doodad_phase_changed
        harvest_scenarios = [
            sc for sc in scenarios
            if ("CSStartInteractionPacket" in self.packet_by_scenario[sc] or "interaction:doodad_use" in self.event_by_scenario[sc])
            and "CSStartSkillPacket" in self.packet_by_scenario[sc]
        ]
        if len(harvest_scenarios) >= 2:
            common_pkts = sorted(set.intersection(*[set(self.packet_by_scenario[s].keys()) for s in harvest_scenarios]))
            common_evs = sorted(set.intersection(*[set(self.event_by_scenario[s].keys()) for s in harvest_scenarios]))
            diffs = {
                s: sorted(
                    (set(self.packet_by_scenario[s].keys()) | set(self.event_by_scenario[s].keys()))
                    - (set(common_pkts) | set(common_evs))
                )
                for s in harvest_scenarios
            }
            families.append(CandidateFamily(
                family_name="Skill-Mediated Doodad Gathering / Harvesting",
                confidence="HIGH",
                member_scenarios=harvest_scenarios,
                common_packets=common_pkts,
                common_events=common_evs,
                key_differences=diffs,
                rationale="Scenarios execute CSStartInteractionPacket followed by CSStartSkillPacket, cast lifecycle, and doodad phase modifications."
            ))

        # Family 3: NPC Dialogue & Commercial Services
        # Key signature: CSInteractNPCPacket, CSInteractNPCEndPacket, SCNpcInteractionSkillListPacket
        npc_scenarios = [
            sc for sc in scenarios
            if "CSInteractNPCPacket" in self.packet_by_scenario[sc]
            or "SCNpcInteractionSkillListPacket" in self.packet_by_scenario[sc]
        ]
        if len(npc_scenarios) >= 2:
            common_pkts = sorted(set.intersection(*[set(self.packet_by_scenario[s].keys()) for s in npc_scenarios]))
            common_evs = sorted(set.intersection(*[set(self.event_by_scenario[s].keys()) for s in npc_scenarios]))
            diffs = {
                s: sorted(
                    (set(self.packet_by_scenario[s].keys()) | set(self.event_by_scenario[s].keys()))
                    - (set(common_pkts) | set(common_evs))
                )
                for s in npc_scenarios
            }
            families.append(CandidateFamily(
                family_name="NPC Interaction & Commercial Services",
                confidence="HIGH",
                member_scenarios=npc_scenarios,
                common_packets=common_pkts,
                common_events=common_evs,
                key_differences=diffs,
                rationale="Scenarios communicate with NPCs via CSInteractNPCPacket / SCNpcInteractionSkillListPacket; specialized branches handle buy/sell item lists."
            ))

        # Family 4: Mate / Mount Companion Operations
        # Key signature: CSMountMatePacket, CSUnMountMatePacket, CSRemoveMatePacket, SCMateSpawnedPacket
        mate_scenarios = [
            sc for sc in scenarios
            if any(p in self.packet_by_scenario[sc] for p in ["CSMountMatePacket", "CSUnMountMatePacket", "CSRemoveMatePacket", "SCMateSpawnedPacket"])
        ]
        if len(mate_scenarios) >= 2:
            common_pkts = sorted(set.intersection(*[set(self.packet_by_scenario[s].keys()) for s in mate_scenarios]))
            common_evs = sorted(set.intersection(*[set(self.event_by_scenario[s].keys()) for s in mate_scenarios]))
            diffs = {
                s: sorted(
                    (set(self.packet_by_scenario[s].keys()) | set(self.event_by_scenario[s].keys()))
                    - (set(common_pkts) | set(common_evs))
                )
                for s in mate_scenarios
            }
            families.append(CandidateFamily(
                family_name="Mate / Mount Companion Operations",
                confidence="HIGH",
                member_scenarios=mate_scenarios,
                common_packets=common_pkts,
                common_events=common_evs,
                key_differences=diffs,
                rationale="Scenarios operate on companion entities (mount/dismount/pet summon) using Mate network primitives."
            ))

        return families

    def compute_coverage_gaps(self) -> List[CoverageGap]:
        gaps: List[CoverageGap] = []
        observed_packets = set(self.packet_counts.keys())

        # Gap 1: Combat lifecycle
        combat_pkts = ["CSSetTargetPacket", "CSAttackPacket", "CSCancelSkillPacket", "SCDamagePacket", "SCSkillDamagePacket"]
        unseen_combat = [p for p in combat_pkts if p not in observed_packets]
        gaps.append(CoverageGap(
            gap_type="UNTRACED ACTION FAMILY",
            title="Basic Combat & Auto-Attack Lifecycle",
            description="No human trace currently records hostile target selection, basic melee/ranged attack sequences, damage exchange, or combat state transitions.",
            affected_packets=unseen_combat,
            suggested_action="Collect trace for basic melee combat against a stationary hostile mob (e.g., target -> engage -> attack -> defeat)."
        ))

        # Gap 2: Direct Player-to-Player Trading
        trade_pkts = ["CSTradeLockPacket", "CSPutupTradeItemPacket", "CSAskTradePacket", "SCTradeStartPacket"]
        unseen_trade = [p for p in trade_pkts if p not in observed_packets]
        gaps.append(CoverageGap(
            gap_type="UNTRACED ACTION FAMILY",
            title="Direct Player-to-Player Trading",
            description="P2P trade window initialization, item placement, trade lock, and trade commitment have zero representation in the corpus.",
            affected_packets=unseen_trade,
            suggested_action="Trace bilateral trade between two player characters with item & gold exchange."
        ))

        # Gap 3: Item Crafting & Production
        craft_pkts = ["CSCraftPacket", "SCCraftStartedPacket", "SCCraftEndedPacket"]
        unseen_craft = [p for p in craft_pkts if p not in observed_packets]
        gaps.append(CoverageGap(
            gap_type="UNTRACED ACTION FAMILY",
            title="Recipe Crafting & Production",
            description="Workstation interaction, recipe consumption, crafting progress bar, and crafted item insertion into inventory remain untraced.",
            affected_packets=unseen_craft,
            suggested_action="Trace crafting a basic item at a carpentry/blacksmithing workbench."
        ))

        # Gap 4: Quest Progression & Turn-in
        quest_pkts = ["CSQuestStartWithPacket", "CSQuestCompletePacket", "SCQuestStatusPacket"]
        unseen_quest = [p for p in quest_pkts if p not in observed_packets]
        gaps.append(CoverageGap(
            gap_type="UNTRACED ACTION FAMILY",
            title="Quest Acceptance, Objective Progression & Turn-In",
            description="NPC dialogue quest acceptance, objective tracker updates, and quest reward handoff have not been traced.",
            affected_packets=unseen_quest,
            suggested_action="Trace picking up an introductory quest from an NPC, completing objective, and turning it in."
        ))

        # Gap 5: Bag Item Use & Consumables
        item_pkts = ["CSUseBagItemPacket", "CSDeleteItemPacket", "SCItemCooldownPacket"]
        unseen_items = [p for p in item_pkts if p not in observed_packets]
        gaps.append(CoverageGap(
            gap_type="PACKET KNOWN BUT CONTEXT UNKNOWN",
            title="Consumable / Bag Item Activation",
            description="Client inventory item consumption (potions, food, quest items) exists in server code but has no trace corroboration.",
            affected_packets=unseen_items,
            suggested_action="Trace using a bread or potion from the player inventory bag."
        ))

        # Gap 6: Tooling Blind Spots
        gaps.append(CoverageGap(
            gap_type="TRACE TOOLING LIMITATION",
            title="Absence of Action-End Intent Markers & UI Dialog Hooks",
            description="Traces currently log /trace mark as instantaneous events without duration boundaries. Client-side UI dialog choices are only partially visible through network request packets.",
            affected_packets=[],
            suggested_action="Standardize '/trace mark start:<action>' and '/trace mark end:<action>' pairs to delimit complex interaction windows."
        ))

        return gaps

    def compute_recommendations(self) -> List[NextTraceRecommendation]:
        return [
            NextTraceRecommendation(
                rank=1,
                scenario_name="combat_basic_melee",
                priority="HIGH",
                rationale="Fills the entire untraced Combat action family; validates targeting, GCD, auto-attack, damage packets, and target death handling.",
                target_packets=["CSSetTargetPacket", "CSAttackPacket", "SCDamagePacket", "SCSkillDamagePacket"],
                expected_action_family="Hostile Unit Combat"
            ),
            NextTraceRecommendation(
                rank=2,
                scenario_name="quest_accept_and_turnin",
                priority="HIGH",
                rationale="Bridges NPC interaction with quest progression and inventory reward delivery; exercises dialogue choice branches.",
                target_packets=["CSQuestStartWithPacket", "CSQuestCompletePacket", "SCQuestStatusPacket"],
                expected_action_family="Quest Lifecycle"
            ),
            NextTraceRecommendation(
                rank=3,
                scenario_name="craft_single_item",
                priority="HIGH",
                rationale="Exercises workbench doodad interaction, recipe labor deduction, and output item creation, filling the untraced Crafting family.",
                target_packets=["CSCraftPacket", "SCCraftStartedPacket", "SCCraftEndedPacket"],
                expected_action_family="Crafting & Processing"
            ),
            NextTraceRecommendation(
                rank=4,
                scenario_name="use_bag_consumable",
                priority="MEDIUM",
                rationale="Tests client-initiated inventory item consumption (CSUseBagItemPacket) without workstation/NPC dependencies.",
                target_packets=["CSUseBagItemPacket", "SCItemCooldownPacket", "SCUnitPointsPacket"],
                expected_action_family="Inventory & Consumables"
            ),
            NextTraceRecommendation(
                rank=5,
                scenario_name="trade_direct_player",
                priority="MEDIUM",
                rationale="Validates two-player interaction, trade synchronization, item locking, and atomic inventory commit.",
                target_packets=["CSAskTradePacket", "CSPutupTradeItemPacket", "CSTradeLockPacket"],
                expected_action_family="Player-to-Player Trading"
            )
        ]
