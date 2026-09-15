#!/usr/bin/env python3
"""
trace_parser.py - Streamed, fault-tolerant reader for AAEmu PlayerTrace JSONL files.

Designed for high-throughput, low-memory streaming of large trace corpuses.
Handles malformed records, legacy schemas, and missing fields gracefully without aborting.
"""

from dataclasses import dataclass, field
import json
import os
import re
from typing import Any, Dict, Generator, Iterator, List, Optional, Set, Tuple


@dataclass
class TraceRecord:
    """Represents a single parsed PlayerTrace log event."""
    file_path: str
    line_number: int
    scenario: str
    ts: Optional[str]
    elapsed_ms: Optional[int]
    character_id: Optional[int]
    character_name: Optional[str]
    category: str
    event: str
    packet_name: Optional[str] = None
    opcode: Optional[str] = None
    data: Dict[str, Any] = field(default_factory=dict)

    @property
    def is_packet(self) -> bool:
        return self.category == "packet" and bool(self.packet_name)

    @property
    def is_packet_in(self) -> bool:
        return self.is_packet and self.event == "packet_in"

    @property
    def is_packet_out(self) -> bool:
        return self.is_packet and self.event == "packet_out"

    @property
    def is_movement(self) -> bool:
        return self.category == "movement"

    @property
    def is_marker(self) -> bool:
        return self.category == "marker" or self.event == "mark"


def infer_scenario_from_path(file_path: str) -> str:
    """Infer scenario name from file name conventions like scenario__character__timestamp.jsonl."""
    base = os.path.basename(file_path)
    if base.endswith(".jsonl"):
        base = base[:-6]
    elif base.endswith(".json"):
        base = base[:-5]

    # Pattern: <scenario>__<character>__<timestamp>
    parts = base.split("__")
    if len(parts) >= 2:
        return parts[0]
    return base


def stream_trace_file(
    file_path: str,
    on_malformed: Optional[Any] = None
) -> Generator[TraceRecord, None, None]:
    """
    Stream records from a single JSONL file line-by-line.
    If a line is malformed JSON or lacks required fields, it is skipped (or logged via on_malformed).
    """
    scenario = infer_scenario_from_path(file_path)
    line_num = 0

    with open(file_path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line_num += 1
            line = line.strip()
            if not line:
                continue

            try:
                raw = json.loads(line)
            except Exception as ex:
                if on_malformed:
                    on_malformed(file_path, line_num, str(ex), line)
                continue

            if not isinstance(raw, dict):
                if on_malformed:
                    on_malformed(file_path, line_num, "Record is not a JSON object", line)
                continue

            # Check if this record declares or updates the scenario
            category = str(raw.get("category", "")).strip().lower()
            event = str(raw.get("event", "")).strip().lower()
            data = raw.get("data")
            if not isinstance(data, dict):
                data = {}

            if category == "lifecycle" and event == "trace_started":
                declared_scenario = data.get("Scenario") or data.get("scenario")
                if declared_scenario:
                    scenario = str(declared_scenario).strip()

            packet_name = (
                data.get("Packet")
                or data.get("packet")
                or data.get("PacketName")
                or data.get("packet_name")
            )
            if packet_name is not None:
                packet_name = str(packet_name).strip()

            opcode = (
                data.get("Opcode")
                or data.get("opcode")
            )
            if opcode is not None:
                opcode = str(opcode).strip()

            elapsed = raw.get("elapsed_ms")
            if elapsed is not None:
                try:
                    elapsed = int(elapsed)
                except (ValueError, TypeError):
                    elapsed = None

            char_id = raw.get("character_id")
            if char_id is not None:
                try:
                    char_id = int(char_id)
                except (ValueError, TypeError):
                    char_id = None

            yield TraceRecord(
                file_path=file_path,
                line_number=line_num,
                scenario=scenario,
                ts=raw.get("ts"),
                elapsed_ms=elapsed,
                character_id=char_id,
                character_name=raw.get("character_name"),
                category=category or "unknown",
                event=event or "unknown",
                packet_name=packet_name,
                opcode=opcode,
                data=data
            )


def discover_trace_files(paths: List[str]) -> List[str]:
    """Find all .jsonl trace files given paths (files or directories)."""
    discovered: Set[str] = set()

    for p in paths:
        if os.path.isfile(p):
            if p.endswith(".jsonl") or p.endswith(".json"):
                discovered.add(os.path.abspath(p))
        elif os.path.isdir(p):
            for root, _, files in os.walk(p):
                for f in files:
                    if f.endswith(".jsonl") or f.endswith(".json"):
                        discovered.add(os.path.abspath(os.path.join(root, f)))

    return sorted(discovered)
