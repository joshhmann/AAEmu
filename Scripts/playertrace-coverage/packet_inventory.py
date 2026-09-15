#!/usr/bin/env python3
"""
packet_inventory.py - Discovers and indexes all known AAEmu packet classes from source code.

Extracts class names, directions, source file paths, and opcodes without requiring runtime
execution or Roslyn compilers.
"""

from dataclasses import dataclass
import os
import re
from typing import Dict, List, Optional, Tuple


@dataclass
class PacketMetadata:
    """Metadata describing a known AAEmu packet class."""
    name: str
    direction: str  # C2S, S2C, INTERNAL, PROXY, UNKNOWN
    file_path: str
    opcode: Optional[str] = None
    subsystem: str = "Game"  # Game, Login, Stream
    channel: str = ""        # C2G, G2C, C2S, S2C, C2L, L2C, G2L, L2G, Proxy


def _normalize_hex(val: str) -> str:
    """Format hex or decimal integer into standard uppercase 0xXXX format."""
    val = val.strip()
    try:
        if val.lower().startswith("0x"):
            num = int(val, 16)
        else:
            num = int(val)
        return f"0x{num:03X}"
    except ValueError:
        return val


class PacketInventory:
    """Repository of known AAEmu packet definitions discovered from source."""

    def __init__(self, repo_root: str = "."):
        self.repo_root = os.path.abspath(repo_root)
        self.packets: Dict[str, PacketMetadata] = {}
        self.offsets_by_name: Dict[str, str] = {}
        self._scan()

    def _collect_offsets(self) -> None:
        """Scan *Offsets.cs files to build a dictionary of named opcode constants."""
        for root, dirs, files in os.walk(self.repo_root):
            # Exclude worktrees, git, build outputs
            if any(part in root for part in [".worktrees", ".git", "bin", "obj"]):
                continue
            for f in files:
                if f.endswith("Offsets.cs"):
                    p = os.path.join(root, f)
                    try:
                        with open(p, "r", encoding="utf-8", errors="replace") as fh:
                            for line in fh:
                                m = re.search(r"public\s+const\s+\w+\s+(\w+)\s*=\s*(0x[0-9a-fA-F]+|\d+);", line)
                                if m:
                                    name, raw_hex = m.group(1), m.group(2)
                                    self.offsets_by_name[name] = _normalize_hex(raw_hex)
                    except Exception:
                        pass

    def _determine_direction_and_channel(self, rel_path: str, class_name: str) -> Tuple[str, str, str]:
        """Infer direction (C2S/S2C/INTERNAL/PROXY), channel, and subsystem from path and name."""
        norm_path = rel_path.replace("\\", "/")
        subsystem = "Login" if "AAEmu.Login" in norm_path else "Game"

        if "/C2G/" in norm_path:
            return "C2S", "C2G", subsystem
        if "/G2C/" in norm_path:
            return "S2C", "G2C", subsystem
        if "/C2S/" in norm_path:
            return "C2S", "C2S", subsystem
        if "/S2C/" in norm_path:
            return "S2C", "S2C", subsystem
        if "/C2L/" in norm_path:
            return "C2S", "C2L", subsystem
        if "/L2C/" in norm_path:
            return "S2C", "L2C", subsystem
        if "/G2L/" in norm_path:
            return "INTERNAL", "G2L", subsystem
        if "/L2G/" in norm_path:
            return "INTERNAL", "L2G", subsystem
        if "/Proxy/" in norm_path:
            return "PROXY", "Proxy", subsystem

        # Fallback to class prefix
        if class_name.startswith("CS") or class_name.startswith("CT") or class_name.startswith("CA"):
            return "C2S", "C2G", subsystem
        if class_name.startswith("SC") or class_name.startswith("TC") or class_name.startswith("AC"):
            return "S2C", "G2C", subsystem
        if class_name.startswith("GL") or class_name.startswith("LG"):
            return "INTERNAL", "G2L", subsystem

        return "UNKNOWN", "Unknown", subsystem

    def _scan(self) -> None:
        """Scan AAEmu source directories for packet class definitions."""
        self._collect_offsets()

        search_dirs = [
            os.path.join(self.repo_root, "AAEmu.Game", "Core", "Packets"),
            os.path.join(self.repo_root, "AAEmu.Login", "Core", "Packets"),
            os.path.join(self.repo_root, "AAEmu.Commons", "Network"),
        ]

        for sdir in search_dirs:
            if not os.path.exists(sdir):
                continue
            for root, dirs, files in os.walk(sdir):
                if any(part in root for part in [".worktrees", ".git", "bin", "obj"]):
                    continue
                for f in files:
                    if not f.endswith(".cs") or f.endswith("Offsets.cs"):
                        continue
                    full_path = os.path.join(root, f)
                    rel_path = os.path.relpath(full_path, self.repo_root)

                    try:
                        with open(full_path, "r", encoding="utf-8", errors="replace") as fh:
                            content = fh.read()
                    except Exception:
                        continue

                    # Search for class definition
                    m_class = re.search(r"public\s+(?:sealed\s+|abstract\s+)?class\s+(\w+)", content)
                    if not m_class:
                        continue
                    class_name = m_class.group(1)

                    # Only index packet-like classes
                    if not (class_name.endswith("Packet") or "Packet" in class_name):
                        continue

                    direction, channel, subsystem = self._determine_direction_and_channel(rel_path, class_name)

                    # Resolve opcode
                    opcode: Optional[str] = None
                    if class_name in self.offsets_by_name:
                        opcode = self.offsets_by_name[class_name]
                    else:
                        # Check if constructor references an offset
                        m_off = re.search(r"\b([A-Za-z0-9_]+Offsets)\.(\w+)", content)
                        if m_off and m_off.group(2) in self.offsets_by_name:
                            opcode = self.offsets_by_name[m_off.group(2)]
                        else:
                            # Check for numeric opcode literal
                            m_num = re.search(r":\s*(?:GamePacket|LoginPacket|PacketBase)\(\s*(0x[0-9a-fA-F]+|\d+)", content)
                            if m_num:
                                opcode = _normalize_hex(m_num.group(1))

                    meta = PacketMetadata(
                        name=class_name,
                        direction=direction,
                        file_path=rel_path,
                        opcode=opcode,
                        subsystem=subsystem,
                        channel=channel
                    )
                    self.packets[class_name] = meta

    def get(self, packet_name: str) -> Optional[PacketMetadata]:
        return self.packets.get(packet_name)

    def is_known(self, packet_name: str) -> bool:
        return packet_name in self.packets

    def all_packets(self) -> List[PacketMetadata]:
        return sorted(self.packets.values(), key=lambda p: (p.direction, p.name))


if __name__ == "__main__":
    import sys
    root = sys.argv[1] if len(sys.argv) > 1 else "."
    inventory = PacketInventory(root)
    print(f"Discovered {len(inventory.packets)} packets across repository.")
    by_dir: Dict[str, int] = {}
    for p in inventory.packets.values():
        by_dir[p.direction] = by_dir.get(p.direction, 0) + 1
    for d, c in sorted(by_dir.items()):
        print(f"  {d}: {c}")
