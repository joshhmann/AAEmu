#!/usr/bin/env python3
"""
dashboard_server.py - Interactive Web Dashboard for AAEmu Human Trace Tasks & Instructions.

Serves an interactive web interface on port 8085 with:
- Per-task step-by-step instructions and required checklists
- One-click checkoffs and task completion tracking
- One-click copy for in-game /trace commands
- Trace Evaluator integration (verifying packet assertions & stream integrity)
- Real-time trigger to run the PlayerTrace Coverage Mapper and view live stats
- Persistent task state stored in JSON and browser localStorage

Usage:
    python3 Scripts/playertrace-coverage/dashboard_server.py [--port 8085] [--host 0.0.0.0]
"""

import argparse
from dataclasses import asdict
from http.server import HTTPServer, BaseHTTPRequestHandler
import glob
import heapq
import json
import math
import os
import subprocess
import sys
from typing import Any, Dict, List, Optional
import urllib.parse
import urllib.request as urlreq

from task_evaluator import evaluate_trace_file, find_latest_trace_for_scenario


DEFAULT_TASKS = [
    # Top Recommended Uncovered Tasks
    {
        "id": "combat_basic_melee",
        "title": "Basic Melee Combat & Auto-Attack",
        "category": "Combat",
        "priority": "High",
        "scenario": "combat_basic_melee",
        "status": "Pending",
        "description": "Engage a solitary, low-level hostile NPC using standard melee weapon strikes and auto-attacks.",
        "target_packets": ["CSSetTargetPacket", "CSAttackPacket", "SCDamagePacket", "SCSkillDamagePacket"],
        "instructions": [
            "Locate a solitary hostile mob in an open area.",
            "Stand at ~15m range with weapon drawn.",
            "Open chat and start trace: `/trace start combat_basic_melee`",
            "Target the hostile unit by clicking or pressing Tab.",
            "Approach into melee range and initiate basic attack / auto-attack.",
            "Allow the attack sequence to land and observe damage numbers.",
            "Defeat the enemy and wait 2 seconds for death animation and loot generation.",
            "Stop trace: `/trace stop`",
            "Click 'Evaluate Trace' to verify packet capture."
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start combat_basic_melee`", "done": False, "required": True},
            {"id": "step2", "text": "Acquire target lock on hostile mob", "done": False, "required": True},
            {"id": "step3", "text": "Execute melee attack and record damage packet", "done": False, "required": True},
            {"id": "step4", "text": "Wait for mob defeat / death state", "done": False, "required": True},
            {"id": "step5", "text": "Execute `/trace stop`", "done": False, "required": True}
        ],
        "notes": ""
    },
    {
        "id": "quest_accept_and_turnin",
        "title": "Quest Dialogue, Objective & Turn-In",
        "category": "Quests",
        "priority": "High",
        "scenario": "quest_accept_and_turnin",
        "status": "Pending",
        "description": "Interact with a quest-giving NPC, accept an introductory quest, fulfill the objective, and turn it in for rewards.",
        "target_packets": ["CSQuestStartWithPacket", "CSQuestCompletePacket", "SCQuestStatusPacket"],
        "instructions": [
            "Find an NPC with an available quest exclamation mark (!).",
            "Start trace: `/trace start quest_accept_and_turnin`",
            "Interact with the NPC (`F` key) to open dialogue window.",
            "Select and accept the quest via the dialogue menu.",
            "Navigate to the required objective location.",
            "Complete the required action (kill mob, pick item, or interact with doodad).",
            "Return to the quest NPC and turn in the quest (`?` mark).",
            "Collect quest experience and item reward.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start quest_accept_and_turnin`", "done": False, "required": True},
            {"id": "step2", "text": "Interact with NPC and accept quest", "done": False, "required": True},
            {"id": "step3", "text": "Fulfill quest objective step", "done": False, "required": True},
            {"id": "step4", "text": "Turn in quest and accept reward", "done": False, "required": True},
            {"id": "step5", "text": "Execute `/trace stop`", "done": False, "required": True}
        ],
        "notes": ""
    },
    {
        "id": "craft_single_item",
        "title": "Workbench Crafting & Production",
        "category": "Crafting",
        "priority": "High",
        "scenario": "craft_single_item",
        "status": "Pending",
        "description": "Approach an artisan workbench, select a single recipe, and craft it using materials and labor.",
        "target_packets": ["CSCraftPacket", "SCCraftStartedPacket", "SCCraftEndedPacket"],
        "instructions": [
            "Ensure character has required raw materials and labor points.",
            "Stand directly in front of an active crafting workstation.",
            "Start trace: `/trace start craft_single_item`",
            "Interact with the workbench to open the production interface.",
            "Select an affordable single-craft recipe.",
            "Click Craft / Produce and allow the progress bar to finish.",
            "Verify product item appears in inventory and labor power is deducted.",
            "Close workstation interface.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start craft_single_item`", "done": False, "required": True},
            {"id": "step2", "text": "Interact with crafting workstation", "done": False, "required": True},
            {"id": "step3", "text": "Trigger craft action and await completion", "done": False, "required": True},
            {"id": "step4", "text": "Confirm labor deduction & inventory receipt", "done": False, "required": True},
            {"id": "step5", "text": "Execute `/trace stop`", "done": False, "required": True}
        ],
        "notes": ""
    },
    {
        "id": "use_bag_consumable",
        "title": "Use Bag Consumable Item",
        "category": "Inventory",
        "priority": "Medium",
        "scenario": "use_bag_consumable",
        "status": "Pending",
        "description": "Activate a consumable item directly from the player bag.",
        "target_packets": ["CSUseBagItemPacket", "SCItemCooldownPacket", "SCUnitPointsPacket"],
        "instructions": [
            "Open inventory bag (`I` key) and locate a food or potion item.",
            "Start trace: `/trace start use_bag_consumable`",
            "Right-click the consumable item to activate it.",
            "Observe health/mana point recovery or buff application.",
            "Wait for the item cooldown timer to appear.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start use_bag_consumable`", "done": False, "required": True},
            {"id": "step2", "text": "Right-click consumable item in bag", "done": False, "required": True},
            {"id": "step3", "text": "Verify cooldown packet and stat recovery", "done": False, "required": True},
            {"id": "step4", "text": "Execute `/trace stop`", "done": False, "required": True}
        ],
        "notes": ""
    },
    {
        "id": "trade_direct_player",
        "title": "Direct Player-to-Player Trade",
        "category": "Economy",
        "priority": "Medium",
        "scenario": "trade_direct_player",
        "status": "Pending",
        "description": "Initiate bilateral player trade, place an item or gold in the trade window, lock trade, and complete trade.",
        "target_packets": ["CSAskTradePacket", "CSPutupTradeItemPacket", "CSTradeLockPacket"],
        "instructions": [
            "Stand facing another player character.",
            "Start trace: `/trace start trade_direct_player`",
            "Target player, right-click frame and select Trade.",
            "Wait for other player to accept trade invitation.",
            "Drag an item or enter 1 silver into the trade window.",
            "Click 'Lock Trade'.",
            "When both players lock, click 'Trade / Accept'.",
            "Verify trade window closes and item transfers.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start trade_direct_player`", "done": False, "required": True},
            {"id": "step2", "text": "Send trade request and open trade frame", "done": False, "required": True},
            {"id": "step3", "text": "Place item or coin in trade slot", "done": False, "required": True},
            {"id": "step4", "text": "Lock trade and confirm transaction", "done": False, "required": True},
            {"id": "step5", "text": "Execute `/trace stop`", "done": False, "required": True}
        ],
        "notes": ""
    },
    # Existing Captured Reference Tasks (Already in Corpus)
    {
        "id": "pick_potato",
        "title": "Pick Mature Potato (Harvest)",
        "category": "Farming",
        "priority": "High",
        "scenario": "pick_potato",
        "status": "Complete",
        "description": "Gather a mature potato crop using standard doodad interaction, cast task, and labor deduction.",
        "target_packets": ["CSStartInteractionPacket", "CSStartSkillPacket", "SCSkillStartedPacket", "SCSkillFiredPacket", "SCDoodadPhaseChangedPacket"],
        "instructions": [
            "Locate a mature, harvestable potato plant.",
            "Start trace: `/trace start pick_potato`",
            "Interact (`F` key) to initiate gathering cast (4.0s).",
            "Allow cast bar to finish; receive potato item and observe labor deduction.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start pick_potato`", "done": True, "required": True},
            {"id": "step2", "text": "Interact with mature potato doodad", "done": True, "required": True},
            {"id": "step3", "text": "Complete 4.0s gathering cast", "done": True, "required": True},
            {"id": "step4", "text": "Receive harvest item and labor change", "done": True, "required": True},
            {"id": "step5", "text": "Execute `/trace stop`", "done": True, "required": True}
        ],
        "notes": "Verified golden human trace in corpus. Cast time: 4.0s, labor delta: -1."
    },
    {
        "id": "place_chick",
        "title": "Place Livestock Chick (World Placement)",
        "category": "Farming",
        "priority": "High",
        "scenario": "place_chick",
        "status": "Complete",
        "description": "Place a baby chick on farming land using direct client doodad placement.",
        "target_packets": ["CSCreateDoodadPacket", "SCDoodadCreatedPacket"],
        "instructions": [
            "Open inventory and select baby chick item.",
            "Start trace: `/trace start place_chick`",
            "Right-click item to enter placement hologram mode.",
            "Click ground to place entity at valid coordinates.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start place_chick`", "done": True, "required": True},
            {"id": "step2", "text": "Place chick entity on terrain", "done": True, "required": True},
            {"id": "step3", "text": "Confirm CSCreateDoodadPacket emission", "done": True, "required": True},
            {"id": "step4", "text": "Execute `/trace stop`", "done": True, "required": True}
        ],
        "notes": "Verified golden human trace. Uses CSCreateDoodadPacket without Skill.Use."
    },
    {
        "id": "harvest_chicken",
        "title": "Harvest Livestock Chicken",
        "category": "Farming",
        "priority": "High",
        "scenario": "harvest_chicken",
        "status": "Complete",
        "description": "Gather eggs/meat from a mature chicken using doodad interaction and cast lifecycle.",
        "target_packets": ["CSStartInteractionPacket", "CSStartSkillPacket", "SCSkillStartedPacket", "SCSkillFiredPacket"],
        "instructions": [
            "Approach mature chicken doodad.",
            "Start trace: `/trace start harvest_chicken`",
            "Interact (`F` key) to initiate 6.5s harvest cast.",
            "Wait for cast completion and labor deduction.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start harvest_chicken`", "done": True, "required": True},
            {"id": "step2", "text": "Interact with mature chicken", "done": True, "required": True},
            {"id": "step3", "text": "Complete 6.5s harvest cast", "done": True, "required": True},
            {"id": "step4", "text": "Execute `/trace stop`", "done": True, "required": True}
        ],
        "notes": "Verified in corpus. Cast time: 6.5s, labor delta: -10."
    },
    {
        "id": "buy_chick",
        "title": "Purchase Item from Vendor NPC",
        "category": "NPC Services",
        "priority": "High",
        "scenario": "buy_chick",
        "status": "Complete",
        "description": "Talk to livestock vendor NPC, browse shop listing, and purchase a chick.",
        "target_packets": ["CSInteractNPCPacket", "CSBuyItemsPacket", "SCItemTaskSuccessPacket"],
        "instructions": [
            "Approach Livestock Merchant NPC.",
            "Start trace: `/trace start buy_chick`",
            "Interact (`F` key) to open vendor shop.",
            "Select Chick, specify count 1, and click Buy.",
            "Verify money deducted and item added to bag.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start buy_chick`", "done": True, "required": True},
            {"id": "step2", "text": "Open merchant interaction", "done": True, "required": True},
            {"id": "step3", "text": "Complete purchase via CSBuyItemsPacket", "done": True, "required": True},
            {"id": "step4", "text": "Execute `/trace stop`", "done": True, "required": True}
        ],
        "notes": "Verified in corpus. Emits CSInteractNPCPacket -> CSBuyItemsPacket."
    },
    {
        "id": "sell_items",
        "title": "Sell Item to Vendor NPC",
        "category": "NPC Services",
        "priority": "Medium",
        "scenario": "sell_items",
        "status": "Complete",
        "description": "Open merchant store dialogue and sell items from bag for gold.",
        "target_packets": ["CSInteractNPCPacket", "CSSellItemsPacket", "SCSoldItemListPacket"],
        "instructions": [
            "Interact with General Merchant NPC.",
            "Start trace: `/trace start sell_items`",
            "Right-click item in bag to place into vendor sell slot.",
            "Confirm sell transaction and money receipt.",
            "Stop trace: `/trace stop`"
        ],
        "checklist": [
            {"id": "step1", "text": "Execute `/trace start sell_items`", "done": True, "required": True},
            {"id": "step2", "text": "Open vendor sell interface", "done": True, "required": True},
            {"id": "step3", "text": "Sell item and record CSSellItemsPacket", "done": True, "required": True},
            {"id": "step4", "text": "Execute `/trace stop`", "done": True, "required": True}
        ],
        "notes": "Verified in corpus."
    }
]


HTML_TEMPLATE = """<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>AAEmu PlayerTrace Task Dashboard</title>
    <style>
        :root {
            --bg: #0d1117;
            --card-bg: #161b22;
            --border: #30363d;
            --text-main: #c9d1d9;
            --text-muted: #8b949e;
            --accent: #58a6ff;
            --accent-hover: #79c0ff;
            --green: #2ea043;
            --green-hover: #3fb950;
            --yellow: #d29922;
            --red: #f85149;
            --purple: #bc8cff;
            --font: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
        }

        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: var(--font);
            background-color: var(--bg);
            color: var(--text-main);
            line-height: 1.5;
            padding-bottom: 60px;
        }

        header {
            background-color: var(--card-bg);
            border-bottom: 1px solid var(--border);
            padding: 16px 32px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            position: sticky;
            top: 0;
            z-index: 100;
        }

        .header-title h1 {
            font-size: 20px;
            font-weight: 600;
            color: #fff;
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .header-title p {
            font-size: 12px;
            color: var(--text-muted);
        }

        .header-actions {
            display: flex;
            gap: 12px;
            align-items: center;
        }

        .btn {
            background-color: #21262d;
            border: 1px solid var(--border);
            color: var(--text-main);
            padding: 6px 14px;
            border-radius: 6px;
            font-size: 13px;
            font-weight: 500;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 6px;
            transition: all 0.15s ease;
        }

        .btn:hover {
            background-color: #30363d;
            border-color: #8b949e;
        }

        .btn-primary {
            background-color: var(--green);
            border-color: rgba(240,246,252,0.1);
            color: #fff;
        }

        .btn-primary:hover {
            background-color: var(--green-hover);
        }

        .btn-accent {
            background-color: #1f6feb;
            border-color: rgba(240,246,252,0.1);
            color: #fff;
        }

        .btn-accent:hover {
            background-color: #388bfd;
        }

        .container {
            max-width: 1200px;
            margin: 24px auto;
            padding: 0 24px;
        }

        /* Stats Bar */
        .stats-bar {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 16px;
            margin-bottom: 24px;
        }

        .stat-card {
            background-color: var(--card-bg);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 16px;
        }

        .stat-label {
            font-size: 12px;
            color: var(--text-muted);
            text-transform: uppercase;
            letter-spacing: 0.5px;
            font-weight: 600;
        }

        .stat-value {
            font-size: 24px;
            font-weight: 700;
            color: #fff;
            margin-top: 4px;
        }

        .progress-bar-container {
            height: 6px;
            background-color: #21262d;
            border-radius: 3px;
            overflow: hidden;
            margin-top: 8px;
        }

        .progress-bar {
            height: 100%;
            background-color: var(--green);
            transition: width 0.3s ease;
        }

        /* Controls / Filters */
        .controls {
            display: flex;
            justify-content: space-between;
            align-items: center;
            flex-wrap: wrap;
            gap: 16px;
            margin-bottom: 24px;
            background-color: var(--card-bg);
            border: 1px solid var(--border);
            padding: 12px 16px;
            border-radius: 8px;
        }

        .filters {
            display: flex;
            gap: 8px;
            flex-wrap: wrap;
        }

        .filter-btn {
            background: none;
            border: 1px solid transparent;
            color: var(--text-muted);
            padding: 4px 10px;
            border-radius: 20px;
            font-size: 13px;
            cursor: pointer;
            transition: all 0.15s ease;
        }

        .filter-btn:hover {
            color: var(--text-main);
            background-color: #21262d;
        }

        .filter-btn.active {
            background-color: #21262d;
            border-color: var(--border);
            color: var(--accent);
            font-weight: 600;
        }

        .search-box {
            background-color: var(--bg);
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 6px 12px;
            color: var(--text-main);
            font-size: 13px;
            min-width: 240px;
        }

        .search-box:focus {
            outline: none;
            border-color: var(--accent);
        }

        /* Task Cards */
        .task-list {
            display: flex;
            flex-direction: column;
            gap: 16px;
        }

        .task-card {
            background-color: var(--card-bg);
            border: 1px solid var(--border);
            border-radius: 8px;
            overflow: hidden;
            transition: border-color 0.15s ease;
        }

        .task-card:hover {
            border-color: #8b949e;
        }

        .task-card.complete {
            border-left: 4px solid var(--green);
        }

        .task-card.in-progress {
            border-left: 4px solid var(--yellow);
        }

        .task-card.pending {
            border-left: 4px solid var(--border);
        }

        .task-header {
            padding: 16px 20px;
            display: flex;
            justify-content: space-between;
            align-items: flex-start;
            cursor: pointer;
            user-select: none;
        }

        .task-title-group {
            display: flex;
            align-items: center;
            gap: 12px;
            flex-wrap: wrap;
        }

        .task-title {
            font-size: 16px;
            font-weight: 600;
            color: #fff;
        }

        .badge {
            font-size: 11px;
            font-weight: 600;
            padding: 2px 8px;
            border-radius: 12px;
            text-transform: uppercase;
        }

        .badge-combat { background: rgba(248, 81, 73, 0.2); color: #f85149; border: 1px solid rgba(248, 81, 73, 0.4); }
        .badge-quests { background: rgba(88, 166, 255, 0.2); color: #58a6ff; border: 1px solid rgba(88, 166, 255, 0.4); }
        .badge-crafting { background: rgba(210, 153, 34, 0.2); color: #d29922; border: 1px solid rgba(210, 153, 34, 0.4); }
        .badge-inventory { background: rgba(188, 140, 255, 0.2); color: #bc8cff; border: 1px solid rgba(188, 140, 255, 0.4); }
        .badge-economy { background: rgba(46, 160, 67, 0.2); color: #3fb950; border: 1px solid rgba(46, 160, 67, 0.4); }
        .badge-farming { background: rgba(56, 139, 253, 0.2); color: #79c0ff; border: 1px solid rgba(56, 139, 253, 0.4); }
        .badge-npc { background: rgba(219, 109, 40, 0.2); color: #db6d28; border: 1px solid rgba(219, 109, 40, 0.4); }

        .badge-priority-high { color: var(--red); }
        .badge-priority-medium { color: var(--yellow); }
        .badge-priority-low { color: var(--text-muted); }

        .task-status-select {
            background-color: #21262d;
            border: 1px solid var(--border);
            color: var(--text-main);
            padding: 4px 8px;
            border-radius: 6px;
            font-size: 12px;
            cursor: pointer;
        }

        .task-body {
            padding: 0 20px 20px 20px;
            border-top: 1px solid var(--border);
            background-color: rgba(22, 27, 34, 0.5);
        }

        .task-desc {
            font-size: 13px;
            color: var(--text-muted);
            margin: 12px 0;
        }

        .scenario-box {
            background-color: var(--bg);
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 8px 12px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            font-family: monospace;
            font-size: 12px;
            margin-bottom: 16px;
        }

        .scenario-cmd {
            color: #79c0ff;
        }

        .copy-btn {
            background: #21262d;
            border: 1px solid var(--border);
            color: var(--text-main);
            padding: 2px 8px;
            border-radius: 4px;
            cursor: pointer;
            font-size: 11px;
        }

        .copy-btn:hover {
            background: #30363d;
        }

        .section-header {
            font-size: 12px;
            font-weight: 600;
            color: var(--text-muted);
            text-transform: uppercase;
            margin: 16px 0 8px 0;
            letter-spacing: 0.5px;
        }

        /* Checklists */
        .checklist-group {
            display: flex;
            flex-direction: column;
            gap: 8px;
        }

        .checklist-item {
            display: flex;
            align-items: center;
            gap: 10px;
            padding: 6px 10px;
            border-radius: 6px;
            background-color: rgba(33, 38, 45, 0.4);
            border: 1px solid rgba(48, 54, 61, 0.5);
            font-size: 13px;
            cursor: pointer;
        }

        .checklist-item:hover {
            background-color: #21262d;
        }

        .checklist-item.done {
            text-decoration: line-through;
            color: var(--text-muted);
        }

        .checklist-item input[type="checkbox"] {
            cursor: pointer;
            width: 16px;
            height: 16px;
            accent-color: var(--green);
        }

        .tag-req {
            font-size: 10px;
            background-color: rgba(248, 81, 73, 0.15);
            color: #f85149;
            padding: 1px 6px;
            border-radius: 10px;
            font-weight: 600;
            margin-left: auto;
        }

        .eval-box {
            background-color: var(--bg);
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 12px;
            margin-top: 14px;
            font-size: 12px;
            display: none;
        }

        .eval-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-weight: 600;
            margin-bottom: 8px;
        }

        .eval-verdict-pass { color: var(--green); font-size: 13px; }
        .eval-verdict-caveat { color: var(--yellow); font-size: 13px; }
        .eval-verdict-fail { color: var(--red); font-size: 13px; }

        .eval-assertions {
            display: flex;
            flex-direction: column;
            gap: 4px;
            margin-top: 6px;
        }

        .notes-area {
            width: 100%;
            background-color: var(--bg);
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 8px 12px;
            color: var(--text-main);
            font-family: inherit;
            font-size: 12px;
            resize: vertical;
            min-height: 44px;
            margin-top: 6px;
        }

        /* Modal */
        .modal-overlay {
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background-color: rgba(0,0,0,0.7);
            display: none;
            justify-content: center;
            align-items: center;
            z-index: 1000;
        }

        .modal {
            background-color: var(--card-bg);
            border: 1px solid var(--border);
            border-radius: 8px;
            width: 90%;
            max-width: 550px;
            padding: 24px;
        }

        .modal h2 { font-size: 18px; margin-bottom: 16px; }
        .form-group { margin-bottom: 14px; }
        .form-group label { display: block; font-size: 12px; color: var(--text-muted); margin-bottom: 4px; }
        .form-control { width: 100%; padding: 8px 12px; background: var(--bg); border: 1px solid var(--border); border-radius: 6px; color: #fff; font-size: 13px; }

        .toast {
            position: fixed;
            bottom: 24px;
            right: 24px;
            background-color: #1f6feb;
            color: #fff;
            padding: 10px 18px;
            border-radius: 6px;
            font-size: 13px;
            box-shadow: 0 4px 12px rgba(0,0,0,0.5);
            display: none;
            z-index: 2000;
        }
    
        /* Top Navigation Tabs */
        .top-nav {
            background-color: var(--card-bg);
            border-bottom: 1px solid var(--border);
            padding: 0 32px;
            position: sticky;
            top: 69px;
            z-index: 90;
        }

        .top-nav-inner {
            max-width: 1200px;
            margin: 0 auto;
            display: flex;
            gap: 12px;
            flex-wrap: wrap;
            overflow-x: auto;
        }

        .nav-tab-btn {
            background: transparent;
            border: none;
            border-bottom: 3px solid transparent;
            color: var(--text-muted);
            padding: 12px 18px;
            font-size: 14px;
            font-weight: 600;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 8px;
            white-space: nowrap;
        }

        .nav-tab-btn:hover {
            color: var(--text-main);
        }
        .nav-tab-btn.active {
            color: var(--accent);
            border-bottom-color: var(--accent);
        }

        .tab-badge {
            background-color: #21262d;
            padding: 2px 8px;
            border-radius: 12px;
            font-size: 11px;
            color: var(--text-main);
            font-weight: 600;
        }

        .tab-pane {
            display: none;
        }

        .tab-pane.active {
            display: block;
        }

        /* World Map & Atlas Styles */
        .map-container-layout {
            display: grid;
            grid-template-columns: minmax(0, 1fr) 340px;
            gap: 16px;
            min-height: 740px;
            height: calc(100vh - 220px);
            margin-top: 12px;
        }

        .map-canvas-card {
            background-color: #070a10;
            border: 1px solid var(--border);
            border-radius: 8px;
            position: relative;
            overflow: hidden;
            display: flex;
            flex-direction: column;
        }

        .map-toolbar {
            background-color: #0d1117;
            border-bottom: 1px solid var(--border);
            padding: 8px 12px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            flex-wrap: wrap;
            gap: 8px;
            z-index: 10;
        }

        .map-toolbar-group {
            display: flex;
            align-items: center;
            gap: 6px;
            flex-wrap: wrap;
        }

        .map-btn {
            background-color: #21262d;
            border: 1px solid var(--border);
            color: var(--text-main);
            padding: 4px 10px;
            border-radius: 6px;
            font-size: 12px;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 4px;
            transition: all 0.15s ease;
        }

        .map-btn:hover {
            background-color: #30363d;
            border-color: #8b949e;
        }

        .map-btn.active {
            background-color: var(--accent);
            border-color: var(--accent);
            color: #fff;
        }

        .map-canvas-wrap {
            flex: 1;
            position: relative;
            width: 100%;
            height: 100%;
            cursor: crosshair;
            background: #080c14;
            touch-action: none;
            min-height: 380px;
        }

        #worldMapCanvas {
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            display: block;
        }

        .map-status-hud {
            background-color: rgba(13, 17, 23, 0.95);
            border-top: 1px solid var(--border);
            padding: 8px 16px;
            font-family: var(--font-mono);
            font-size: 12px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            color: var(--text-muted);
            z-index: 10;
        }

        .map-status-hud span.val {
            color: #58a6ff;
            font-weight: 600;
        }

        .map-sidebar-card {
            background-color: var(--card-bg);
            border: 1px solid var(--border);
            border-radius: 8px;
            display: flex;
            flex-direction: column;
            overflow: hidden;
        }

        .map-sidebar-header {
            padding: 12px 16px;
            border-bottom: 1px solid var(--border);
            background-color: #161b22;
            font-weight: 600;
            font-size: 14px;
            display: flex;
            align-items: center;
            justify-content: space-between;
        }

        .map-sidebar-content {
            padding: 16px;
            flex: 1;
            overflow-y: auto;
            display: flex;
            flex-direction: column;
            gap: 16px;
        }

        .map-note { font-size: 12px; line-height: 1.6; color: var(--text-muted); }
        .map-toolbar label { font-size: 12px; display: flex; gap: 4px; align-items: center; }
        .map-toolbar select { max-width: 240px; }
        .map-coordinate-fields { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 6px; }
        .map-coordinate-fields input { min-width: 0; padding: 8px 5px; }
        .map-actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
        #draftPoints { display: grid; gap: 8px; margin-top: 12px; }
        .draft-point { border: 1px solid var(--border); border-radius: 6px; padding: 8px; }
        .draft-point.active { border-color: #ec4899; }
        .map-btn:disabled, .map-actions button:disabled { opacity: .45; cursor: not-allowed; }
        #mapNotice { color: #f5c66d; min-height: 20px; }
        @media (max-width: 1000px) {
            .map-container-layout { grid-template-columns: minmax(0, 1fr); height: auto; }
            .map-canvas-card { min-height: 640px; }
            .map-status-hud { flex-wrap: wrap; gap: 8px; }
        }

        /* Milestone & Lane Cards */
        .milestone-grid, .lane-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
            gap: 16px;
            margin-top: 16px;
            padding-bottom: 8px;
        }

        .info-card {
            background-color: var(--card-bg);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 20px;
            display: flex;
            flex-direction: column;
            gap: 12px;
            transition: border-color 0.15s ease;
        }

        .info-card:hover {
            border-color: #8b949e;
        }

        .info-card-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .info-card-title {
            font-size: 16px;
            font-weight: 600;
            color: #fff;
        }

        .status-badge {
            font-size: 11px;
            font-weight: 700;
            padding: 3px 8px;
            border-radius: 12px;
            text-transform: uppercase;
        }

        .status-closed { background: rgba(46, 160, 67, 0.2); color: #3fb950; border: 1px solid rgba(46, 160, 67, 0.4); }
        .status-active { background: rgba(210, 153, 34, 0.2); color: #d29922; border: 1px solid rgba(210, 153, 34, 0.4); }
        .status-queued { background: rgba(188, 140, 255, 0.2); color: #bc8cff; border: 1px solid rgba(188, 140, 255, 0.4); }

        .meta-row {
            display: flex;
            justify-content: space-between;
            font-size: 12px;
            color: var(--text-muted);
            border-bottom: 1px solid rgba(48, 54, 61, 0.5);
            padding-bottom: 6px;
        }

        .meta-val {
            color: var(--text-main);
            font-weight: 500;
        }

        .code-box {
            background-color: var(--bg);
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 8px 12px;
            font-family: monospace;
            font-size: 12px;
            color: #79c0ff;
            word-break: break-all;
        }

        .summary-banner {
            background: linear-gradient(135deg, rgba(31, 111, 235, 0.15), rgba(46, 160, 67, 0.15));
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 20px 24px;
            margin-bottom: 20px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            flex-wrap: wrap;
            gap: 16px;
        }

        .banner-stat h3 {
            font-size: 24px;
            color: #fff;
            margin-bottom: 2px;
        }

        .banner-stat p {
            font-size: 12px;
            color: var(--text-muted);
            text-transform: uppercase;
            font-weight: 600;
        }

        .banner-stat { flex: 1 1 160px; }

        .vision-card .task-desc {
            max-width: 75ch;
            color: var(--text-main);
            font-size: 14px;
            line-height: 1.65;
        }

    </style>
</head>
<body>

    <header>
        <div class="header-title">
            <h1><span>🎯</span> AAEmu PlayerTrace Task Dashboard</h1>
            <p>Interactive Human Trace Instructions, Verification Checklists & Coverage Progression</p>
        </div>
        <div class="header-actions">
            <button class="btn" onclick="openNewTaskModal()">+ Add Custom Task</button>
            <button class="btn btn-accent" id="analyzeBtn" onclick="runCoverageAnalysis()">⚡ Run Coverage Mapper</button>
        </div>
    </header>

    <!-- Top Navigation Bar -->
    <div class="top-nav">
        <div class="top-nav-inner">
            <button class="nav-tab-btn active" id="tabBtn-tasks" onclick="switchMainTab('tasks')">📋 Trace Tasks <span class="tab-badge" id="tasksTabBadge">31</span></button>
            <button class="nav-tab-btn" id="tabBtn-milestones" onclick="switchMainTab('milestones')">🎯 Roadmap & Scorecard <span class="tab-badge">Outcomes</span></button>
            <button class="nav-tab-btn" id="tabBtn-lanes" onclick="switchMainTab('lanes')">🧭 Domain Lanes & Router</button>
            <button class="nav-tab-btn" id="tabBtn-coverage" onclick="switchMainTab('coverage')">📊 Coverage & Telemetry</button>
            <button class="nav-tab-btn" id="tabBtn-map" onclick="switchMainTab('map')">🗺️ World Map & Atlas <span class="tab-badge" style="background:rgba(16,185,129,0.2);color:#34d399;">136 Hubs</span></button>
        </div>
    </div>


    
    <div class="container">

        <!-- ==================== TAB 1: TRACE TASKS ==================== -->
        <div class="tab-pane active" id="tabPane-tasks">
            <!-- Stats Bar -->
            <div class="stats-bar">
                <div class="stat-card">
                    <div class="stat-label">Tasks Completed</div>
                    <div class="stat-value"><span id="completedCount">0</span> / <span id="totalCount">0</span></div>
                    <div class="progress-bar-container">
                        <div class="progress-bar" id="tasksProgressBar" style="width: 0%"></div>
                    </div>
                </div>
                <div class="stat-card">
                    <div class="stat-label">Observed Packets</div>
                    <div class="stat-value" id="observedPacketsStat">--</div>
                    <div style="font-size: 11px; color: var(--text-muted); margin-top: 4px;">Known AAEmu classes captured</div>
                </div>
                <div class="stat-card">
                    <div class="stat-label">Trace Corpus Records</div>
                    <div class="stat-value" id="totalRecordsStat">--</div>
                    <div style="font-size: 11px; color: var(--text-muted); margin-top: 4px;">Ingested human trace events</div>
                </div>
                <div class="stat-card">
                    <div class="stat-label">Identified Action Families</div>
                    <div class="stat-value" id="actionFamiliesStat">--</div>
                    <div style="font-size: 11px; color: var(--text-muted); margin-top: 4px;">Non-equivalent structural sets</div>
                </div>
            </div>

            <!-- Filter & Search Controls -->
            <div class="controls">
                <div class="filters">
                    <button class="filter-btn active" onclick="setFilter('all')">All Tasks</button>
                    <button class="filter-btn" onclick="setFilter('Pending')">Pending</button>
                    <button class="filter-btn" onclick="setFilter('In Progress')">In Progress</button>
                    <button class="filter-btn" onclick="setFilter('Complete')">Completed</button>
                    <button class="filter-btn" onclick="setFilter('Combat')">Combat</button>
                    <button class="filter-btn" onclick="setFilter('Quests')">Quests</button>
                    <button class="filter-btn" onclick="setFilter('Crafting')">Crafting</button>
                    <button class="filter-btn" onclick="setFilter('Farming')">Farming</button>
                    <button class="filter-btn" onclick="setFilter('NPC Services')">NPC Services</button>
                    <button class="filter-btn" onclick="setFilter('PlayerBots')">PlayerBots</button>
                </div>
                <input type="text" class="search-box" id="searchBox" placeholder="Search tasks, packets, keywords..." oninput="renderTasks()">
            </div>

            <!-- Tasks Container -->
            <div class="task-list" id="taskList"></div>
        </div>

        <!-- ==================== TAB 2: ROADMAP & SCORECARD ==================== -->
        <div class="tab-pane" id="tabPane-milestones">
            <div class="info-card" style="margin-bottom:20px">
                <div class="info-card-header">
                <div class="info-card-title">One dependable game. A living world built on it.</div>
                </div>
                <p class="task-desc">Target: ArcheAge 1.2.4.13 / r208022. Human playability and autonomous residents are separate commitments sharing ordinary gameplay. No overall completion percentage is claimed.</p>
                <div class="meta-row">
                <span>Planning freshness</span>
                <span class="meta-val">2026-09-15 · source d0d58e5848829b7a85da3847d03da00a90cb7c68 + dirty GOAP WIP</span>
                </div>
                <p class="task-desc">Documentation reconciliation only: no fresh engine, live, or human verdict. Sources: VISION.md, PROJECT-CONTROL.md#delivery-contract, ROADMAP.md#current-delivery-direction, SCORECARD.md and STATUS.md. This is a planning snapshot, not live acceptance telemetry.</p>
                </div>
            <div class="info-card" style="margin-bottom:20px">
                <div class="info-card-header">
                <div class="info-card-title">Broad view — three delivery lanes</div>
                </div>
                <div class="meta-row">
                <span>Human-playable 1.2.4</span>
                <span class="meta-val">Safe operations → everyday play → progression corridors → livelihood → group/world content</span>
                </div>
                <div class="meta-row">
                <span>PlayerBot parity and autonomy</span>
                <span class="meta-val">Shared actions → seeded loop → self-acquisition → recovery/repetition → cooperation</span>
                </div>
                <div class="meta-row">
                <span>Population and society</span>
                <span class="meta-val">Reliable residents → real producer/consumer dependence → mixed village → emergent systems</span>
                </div>
                <p class="task-desc">These lanes can advance together. A slice proves one outcome, not its whole parent milestone.</p>
                </div>
            <div class="info-card" style="margin-bottom:20px">
                <div class="info-card-header">
                <div class="info-card-title">Zoom in — next slices and weakest links</div>
                </div>
                <p class="task-desc">Expand a slice for acceptance and dependencies. Full briefs: ROADMAP.md#near-term-slice-queue. Assign an implementer and independent verifier before dispatch.</p>
                <details style="padding:10px 0">
                <summary>1. Schema delivery safety — ready to scope</summary>
                <p class="task-desc">Core operations: ship or explicitly mount migration files. Prove isolated fresh/existing DB startup, exactly-once application and restart persistence. Backup and table verification precede separately authorized deployment.</p>
                </details>
                <details style="padding:10px 0">
                <summary>2. Human corridor baseline — ready for a bounded test packet</summary>
                <p class="task-desc">M1/M2: name start, route, level range, ordinary actions and destination. Record each step pass/fail/unknown; dispatch the first blocker. Solzreed is a starting scope, not all-zone completion. Josh owns the human verdict.</p>
                </details>
                <details style="padding:10px 0">
                <summary>3. Harvest parity and Cast legality — separate bounded fixes</summary>
                <p class="task-desc">M5: reproduce Q-harvest-seam and Q-gcd-shape against current source. Prove authoritative range/timing, interruption, labor/output and refusal. Packet imitation is not parity. Split independent fixes.</p>
                </details>
                <details style="padding:10px 0">
                <summary>4. Seeded two-harvest loop → unseeded livelihood</summary>
                <p class="task-desc">M7/M8 farming: first prove travel, interaction, completion and recovery with disclosed fixtures. Then acquire inputs, find a legal site, grow/harvest and repeat without intervention, conserving money/labor/items. Depends on parity and merchant/plant correctness.</p>
                </details>
                <details style="padding:10px 0">
                <summary>5. GOAP correctness → observation/targets → runtime integration</summary>
                <p class="task-desc">M6/M7: retain committed foundations and review dirty WIP. Prove resource-aware search identity, then authoritative observations and a real eligible target, then one chain through the existing scheduler/request lifecycle. Planned effects are not success. Measure before adding caches.</p>
                </details>
                <details style="padding:10px 0">
                <summary>6. Real producer–consumer village — dependent</summary>
                <p class="task-desc">M8: one resident consumes goods another actually produced. Shortages change behavior; inventory/currency reconcile; repeat/restart retain obligations. Requires a reliable livelihood. Mixed-client experience has a separate human gate.</p>
                </details>
                </div>
            <div class="info-card" style="margin-bottom:20px">
                <div class="info-card-header">
                <div class="info-card-title">Milestone horizon — scoped evidence, not percentages</div>
                </div>
                <div class="meta-row">
                <span>M0 — Foundation</span>
                <span class="meta-val">Historical foundation retained; operational safety continues</span>
                </div>
                <div class="meta-row">
                <span>M1/M2 — Progression / golden path</span>
                <span class="meta-val">Scoped automated/rig evidence; human journeys and broad coverage separate</span>
                </div>
                <div class="meta-row">
                <span>M3 / M3a — Property / farming</span>
                <span class="meta-val">Scoped implementation/persistence retained; verify the named ordinary loop</span>
                </div>
                <div class="meta-row">
                <span>M4 — Trade/craft/transport</span>
                <span class="meta-val">Chain evidence is not autonomous acquisition or full human-route closure</span>
                </div>
                <div class="meta-row">
                <span>M5 — Gameplay Actor</span>
                <span class="meta-val">Surface achievements retained; semantic parity questions remain</span>
                </div>
                <div class="meta-row">
                <span>M6 — PlayerBot framework</span>
                <span class="meta-val">Lifecycle evidence retained; A5 alone does not close full M6</span>
                </div>
                <div class="meta-row">
                <span>M7 — Adventurer/party</span>
                <span class="meta-val">Bounded loop evidence; broader autonomy/cooperation remains open</span>
                </div>
                <div class="meta-row">
                <span>M8 — Living Village</span>
                <span class="meta-val">09-08 QUALIFIED runtime exit; not general autonomous village or human acceptance</span>
                </div>
                <div class="meta-row">
                <span>M8.5 / M9 / M9.5 / M10</span>
                <span class="meta-val">Social/guide · Emergent world systems · Activities · Territory/siege</span>
                </div>
                <p class="task-desc">A5 historical shape: 1,000 registered / 50 embodied, not 1,000 autonomous full-simulation residents. M8 C5 recorded 24/24 cycles and four kill-9 restarts; accepted 1112 ms transient, DB-volume INVALID, human acceptance UNKNOWN. Original dated artifacts remain authoritative.</p>
                </div>
            <div class="info-card" style="margin-bottom:20px">
                <div class="info-card-header">
                <div class="info-card-title">Scorecard discipline</div>
                </div>
                <p class="task-desc">Mechanic dimensions: Canonical (C), Wired (W), Human (H), Automated (A), Restart (R), Soak (S), per SCORECARD.md. Separately name evidence layers: contract/fake mapping, deterministic rig, live authenticated scenario, human/client. Never reuse bare letters across vocabularies.</p>
                <p class="task-desc">Delivery states: Ready; In progress; Implementation complete / verification pending; Verified at a named layer. Deployment and human acceptance are separately recorded. Unknown does not mean broken. Wiring/test counts are not completion percentages.</p>
                <p class="task-desc">Missing external artifacts remain OUT-OF-REPO, not freshly verified. No evidence grades changed in this reconciliation.</p>
                </div>
            <div class="info-card" style="margin-bottom:20px">
                <div class="info-card-header">
                <div class="info-card-title">Human acceptance — Josh-owned, separate from automation</div>
                </div>
                <p class="task-desc">Prepare one reproducible ordinary-client corridor with short expected-result checks. Record the actual build, scenario and verdict. Traces, bots and tests expose defects but cannot award human feel. An open human gate does not freeze unrelated safe engineering.</p>
                </div>
            <div class="info-card" style="margin-bottom:20px">
                <div class="info-card-header">
                <div class="info-card-title">Agent handoff</div>
                </div>
                <p class="task-desc">Parent/outcome · SHA + dirty baseline · scope/non-goals · initial state/seed disclosure · acceptance/evidence layer · dependencies · owner/verifier · stop/next action. One active slice per worker. No second gameplay path or scheduler. No implied push or deployment authority.</p>
                </div>
        </div>

        <!-- ==================== TAB 3: DOMAIN LANES ==================== -->
        <div class="tab-pane" id="tabPane-lanes">
            <div class="summary-banner">
                <div class="banner-stat">
                    <h3>5 Domain Lanes</h3>
                    <p>Specialized Onboarding & Work Dispatcher</p>
                </div>
                <div class="banner-stat">
                    <h3>Master Router</h3>
                    <p>START-HERE.md & AGENTS.md</p>
                </div>
                <div class="banner-stat">
                    <h3>Rule #9 & #10</h3>
                    <p>No parallel gameplay systems</p>
                </div>
            </div>

            <div class="lane-grid">
                <!-- Lane A -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">Lane A · Core AAEmu Engine</div>
                        <span class="status-badge status-active">Core Engine</span>
                    </div>
                    <div class="task-desc">Packets (C2G/G2C), combat math, NPCs, doodads, quests, housing, trade packs, netcode, and MySQL schemas.</div>
                    <div class="meta-row"><span>Primary Docs</span><span class="meta-val">Components.md, Development-Conventions.md</span></div>
                    <div class="meta-row"><span>Where Backlog Lives</span><span class="meta-val">zero-wired-domains.md, partial-domains.md</span></div>
                    <div class="meta-row"><span>Code Scope</span><span class="meta-val">AAEmu.Game/Core/{Packets,Managers}/, GameData/</span></div>
                    <div class="code-box">./scripts/gate.sh</div>
                </div>

                <!-- Lane B -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">Lane B · Living World & PlayerBots</div>
                        <span class="status-badge status-closed">Autonomous AI</span>
                    </div>
                    <div class="task-desc">Autonomous bot decision trees, combat rotations, leveling loop, dormancy/restoration, scheduling, and bot GM commands.</div>
                    <div class="meta-row"><span>Primary Docs</span><span class="meta-val">PLAYERBOT_TARGET_ARCHITECTURE_V1.md, LIVING-WORLD.md</span></div>
                    <div class="meta-row"><span>Where Backlog Lives</span><span class="meta-val">playerbot-blockers.md, deferred-not-now.md (Sec 12-18)</span></div>
                    <div class="meta-row"><span>Code Scope</span><span class="meta-val">AAEmu.Game/Core/Managers/Bots/, Scripts/Commands/BotCmd.cs</span></div>
                    <div class="code-box">./scripts/gate.sh BotActionControllerRouteTests</div>
                </div>

                <!-- Lane C -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">Lane C · Human Traces & Dashboard</div>
                        <span class="status-badge status-active">Tooling</span>
                    </div>
                    <div class="task-desc">Task specifications, empirical ground-truth telemetry, packet sequence analysis, similarity mapping, and dashboard server.</div>
                    <div class="meta-row"><span>Primary Docs</span><span class="meta-val">TASK_SPEC_AND_EVALUATION_GUIDE.md, README.md</span></div>
                    <div class="meta-row"><span>Where Backlog Lives</span><span class="meta-val">playertrace-coverage/dashboard_tasks.json</span></div>
                    <div class="meta-row"><span>Active Tasks</span><span class="meta-val">31 tasks total (6 Complete, 25 Pending)</span></div>
                    <div class="code-box">python3 Scripts/playertrace-coverage/task_evaluator.py --task-id &lt;id&gt;</div>
                </div>

                <!-- Lane D -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">Lane D · Reference Archaeology (MCP)</div>
                        <span class="status-badge status-closed">Read-Only</span>
                    </div>
                    <div class="task-desc">Querying canonical 1.2 reference data (compact.sqlite3, 679 tables, md5 78b3bdbf) to corroborate formulas before writing code.</div>
                    <div class="meta-row"><span>Primary Docs</span><span class="meta-val">AAEmu.ArchaeologyMcp/README.md, archaeology-data-source-inventory.md</span></div>
                    <div class="meta-row"><span>Canonical DB</span><span class="meta-val">679 tables (md5 78b3bdbf038db3b927056106efdf91af)</span></div>
                    <div class="meta-row"><span>MCP Tools</span><span class="meta-val">24 read-only tools (query_sql, trace_*, describe_table)</span></div>
                    <div class="code-box">./scripts/archaeology-cycle.sh</div>
                </div>

                <!-- Lane E -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">Lane E · Devops, Testing & Deployment</div>
                        <span class="status-badge status-active">Operations</span>
                    </div>
                    <div class="task-desc">Developer onboarding, .NET Aspire AppHost vs host MySQL, soak test gates, and staging production deploys to .165.</div>
                    <div class="meta-row"><span>Primary Docs</span><span class="meta-val">aaemu-setup/SKILL.md, Human-Gate-Field-Guide.md</span></div>
                    <div class="meta-row"><span>Deployment Target</span><span class="meta-val">CT 133 (.165) via Docker Compose</span></div>
                    <div class="meta-row"><span>Human Gate (H)</span><span class="meta-val">Josh-only acceptance verdict</span></div>
                    <div class="code-box">./scripts/gate.sh</div>
                </div>
            </div>
        </div>

        <!-- ==================== TAB 4: COVERAGE & TELEMETRY ==================== -->
        <div class="tab-pane" id="tabPane-coverage">
            <div class="summary-banner">
                <div class="banner-stat">
                    <h3 id="covUniquePackets">--</h3>
                    <p>Unique Packets Observed</p>
                </div>
                <div class="banner-stat">
                    <h3 id="covTotalRecords">--</h3>
                    <p>Total Human Trace Records</p>
                </div>
                <div class="banner-stat">
                    <h3 id="covActionFamilies">--</h3>
                    <p>Candidate Action Families</p>
                </div>
                <div class="banner-stat">
                    <h3 id="covTracesParsed">--</h3>
                    <p>Human Traces Parsed</p>
                </div>
            </div>
            <div class="info-card" style="margin-bottom: 20px;">
                <div class="info-card-header">
                    <div class="info-card-title">Corpus Telemetry</div>
                    <span class="meta-val" id="covGeneratedAt">generated: unknown</span>
                </div>
                <div class="task-desc">Record-type split across the ingested human trace corpus (from <code>summary.json</code> corpus_summary):</div>
                <div style="display: flex; gap: 24px; flex-wrap: wrap; margin-top: 8px; font-size: 13px;">
                    <div>Movement records: <strong id="covMovement">--</strong></div>
                    <div>Event records: <strong id="covEvents">--</strong></div>
                    <div>Malformed records: <strong id="covMalformed">--</strong></div>
                    <div>Open coverage gaps: <strong id="covGaps">--</strong></div>
                </div>
            </div>

            <div class="info-card" style="margin-bottom: 20px;">
                <div class="info-card-header">
                    <div class="info-card-title">Recommended Next Traces to Capture</div>
                </div>
                <div class="task-desc">High-value in-game actions that will unlock the highest new packet and event coverage across the server protocol:</div>
                <ul id="recommendedTracesList" style="margin-left: 20px; font-size: 13px; color: var(--text-main); display: flex; flex-direction: column; gap: 8px; margin-top: 8px;">
                    <li>Loading coverage data...</li>
                </ul>
            </div>

            <div class="info-card">
                <div class="info-card-header">
                    <div class="info-card-title">Top Observed Packets Across Corpus</div>
                </div>
                <div id="topPacketsTableContainer" style="overflow-x: auto; margin-top: 12px;">
                    <table style="width: 100%; border-collapse: collapse; font-size: 13px;">
                        <thead>
                            <tr style="border-bottom: 1px solid var(--border); text-align: left; color: var(--text-muted);">
                                <th style="padding: 8px;">Packet Class</th>
                                <th style="padding: 8px;">Total Occurrences</th>
                                <th style="padding: 8px;">Scenarios Observed</th>
                            </tr>
                        </thead>
                        <tbody id="topPacketsTableBody">
                            <tr><td colspan="3" style="padding: 12px; text-align: center; color: var(--text-muted);">Click "⚡ Run Coverage Mapper" or wait for background load...</td></tr>
                        </tbody>
                    </table>
                </div>
            </div>
        </div>

        <!-- ==================== WORLD MAP & ROUTE AUTHORING ==================== -->
        <div class="tab-pane" id="tabPane-map">
            <div class="map-container-layout">
                <div class="map-canvas-card">
                    <div class="map-toolbar">
                        <div class="map-toolbar-group">
                            <button class="map-btn" onclick="chooseMap('world')">World</button>
                            <button class="map-btn" onclick="chooseMap('west')">Nuia</button>
                            <button class="map-btn" onclick="chooseMap('east')">Haranya</button>
                            <button class="map-btn" onclick="chooseMap('origin')">Auroria</button>
                            <label>Map <select id="mapPicker" class="form-control" onchange="chooseMap(this.value)" aria-label="Map view"></select></label>
                        </div>
                        <div class="map-toolbar-group">
                            <button class="map-btn" onclick="mapZoom(1.4)" aria-label="Zoom in">+</button>
                            <button class="map-btn" onclick="mapZoom(1/1.4)" aria-label="Zoom out">−</button>
                            <button class="map-btn" onclick="mapResetView()">Fit map</button>
                            <button class="map-btn" onclick="fitDraftRoute()">Fit route</button>
                        </div>
                        <div class="map-toolbar-group">
                            <label><input type="checkbox" id="layerArt" checked onchange="redrawMap()"> Client art</label>
                            <label><input type="checkbox" id="layerRoads" checked onchange="redrawMap()"> Reference hubs</label>
                            <label title="Straight candidate connections, not recorded road geometry"><input type="checkbox" id="layerLinks" onchange="redrawMap()"> Candidate links</label>
                            <label title="Map image extents, not physical zone borders"><input type="checkbox" id="layerZones" onchange="redrawMap()"> Map extents</label>
                            <label><input type="checkbox" id="layerGrid" onchange="redrawMap()"> Meter grid</label>
                        </div>
                        <div class="map-toolbar-group">
                            <button class="map-btn active" id="mapModeInspectBtn" onclick="setMapMode('inspect')">Inspect</button>
                            <button class="map-btn" id="mapModeDraftBtn" onclick="setMapMode('draft')">Draw route</button>
                            <label title="Use a hub's full XYZ when clicked within 10 screen pixels"><input type="checkbox" id="snapHubs"> Snap to hubs</label>
                            <button class="map-btn" id="liveRadarBtn" onclick="toggleLiveRadar()" title="Toggle live player & bot radar polling against Game WebApi" style="border-color:#388bfd;color:#58a6ff;font-weight:600;">📡 Live Radar: Off</button>
                        </div>
                    </div>
                    <div class="map-canvas-wrap" id="mapCanvasWrap">
                        <canvas id="worldMapCanvas" tabindex="0" aria-label="World-coordinate route map. Drag to pan, scroll to zoom, click to inspect or add a waypoint."></canvas>
                    </div>
                    <div class="map-status-hud">
                        <div><span class="val" id="hudCoords">X — · Y —</span> | <span id="hudElev">Z unknown</span></div>
                        <div id="hudMap">Loading client map…</div>
                    </div>
                </div>
                <div class="map-sidebar-card">
                    <div class="map-sidebar-header">
                        <span id="sidebarHeaderTitle">Map inspector</span>
                        <span class="badge">World 0</span>
                    </div>
                    <div class="map-sidebar-content">
                        <div id="mapNotice" class="map-note" role="status"></div>
                        <div id="sidebarInspectPanel">
                            <strong id="inspectName">Select a point or enter /position coordinates</strong>
                            <p class="map-note" id="inspectSub">North is +Y; east is +X. All coordinates are in-game world meters.</p>
                            <div class="map-coordinate-fields">
                                <label>X<input class="form-control" id="inspectX" type="number" step="any" aria-label="World X"></label>
                                <label>Y<input class="form-control" id="inspectY" type="number" step="any" aria-label="World Y"></label>
                                <label>Z<input class="form-control" id="inspectZ" type="number" step="any" placeholder="Unknown" aria-label="World Z"></label>
                            </div>
                            <div class="map-actions">
                                <button class="btn" onclick="focusEnteredPoint()">Locate XYZ</button>
                                <button class="btn" onclick="addEnteredPoint()">Add to route</button>
                                <button class="btn" onclick="copySelectedPosition()">Copy position</button>
                                <button class="btn" onclick="copyMoveCommand()">Copy /move</button>
                            </div>
                            <p class="map-note">No height is inferred from map art. Enter the measured Z from <code>/position</code> (clear your target first), or snap to a reference hub. <code>/move X Y Z</code> teleports a GM; it does not test walkability.</p>
                        </div>
                        <div id="sidebarDraftPanel" style="display:none">
                            <label for="routeName">Route name</label>
                            <input class="form-control" id="routeName" value="my_route" maxlength="80" oninput="saveDraft()" pattern="[A-Za-z0-9_-]+" aria-label="Route name">
                            <p class="map-note">Click to place waypoints; drag to pan. Edit XYZ below. Pink lines are your planned route, not a pathfinding result.</p>
                            <strong id="draftCount">0 waypoints</strong>
                            <div class="map-note" id="draftDistance">0 m horizontal</div>
                            <div class="map-actions">
                                <button class="btn" onclick="undoDraftPoint()">Undo point</button>
                                <button class="btn btn-danger" onclick="clearDraftRoute()">Clear</button>
                                <button class="btn" id="autoFillHeightsBtn" onclick="autoResolveRouteHeights()" style="border-color:#10b981;color:#34d399;" title="Automatically resolve Z elevation for all waypoints from atlas road topology">⚡ Auto-Fill Heights</button>
                                <button class="btn" onclick="downloadDraft()">Save draft</button>
                                <button class="btn" onclick="document.getElementById('routeImport').click()">Open route / draft</button>
                                <input type="file" id="routeImport" accept=".json,application/json" hidden onchange="importRoute(this)">
                                <button class="btn btn-primary" id="exportMapperBtn" onclick="exportMapperRoute()" disabled>Export game route</button>
                                <button class="btn" id="uploadServerBtn" onclick="uploadRouteToServer()" style="border-color:#388bfd;background:rgba(56,189,248,0.15);color:#38bdf8;font-weight:600;" disabled title="Upload route directly to Game server Data/Routes for instant /mapper play">🚀 Upload to Server</button>
                            </div>
                            <div class="map-note" id="draftValidation">Add at least two points.</div>
                            <div id="draftPoints"></div>
                            <details style="margin-top:12px"><summary>Use this route in-game</summary>
                                <p class="map-note">Fill every Z before exporting. Put the downloaded JSON in the running Game server's <code>Data/Routes/</code>. The bot must already be in <strong>main_world (world 0)</strong>, near the first point.</p>
                                <code id="mapperPlayHint">/mapper play &lt;bot_name&gt; my_route</code>
                                <p class="map-note">Replay uses ordinary <code>NavigateTo</code>; each leg has a 15-second timeout. Place close waypoints, especially on bends. Obstacles, bridges, caves, and Z still need an in-game check.</p>
                                <p class="map-note">For measured routes: <code>/mapper walk name</code>, walk the route, then <code>/mapper stop</code>. Open its waypoint-only JSON here to compare it to the map. Files containing interactions are refused rather than silently stripped.</p>
                            </details>
                        </div>
                        <details>
                            <summary>Coordinate sources &amp; limits</summary>
                            <p class="map-note" id="mapProvenance"></p>
                            <p class="map-note">Client DDS artwork is decoded at its native 1856 × 1112 size. World/continent bounds come from <code>world_groups</code>; detail bounds from <code>zone_groups</code>. Rectangles are artwork extents, not zone borders.</p>
                            <p class="map-note">Reference hubs and straight candidate links come from the existing <a href="https://www.aaplayerbots.com/atlas/" target="_blank" rel="noopener">AAPlayerBots atlas</a> dataset. They are not proof of movement validity. Sextant-only wreck pins and other runtime worlds are not overlaid.</p>
                        </details>
                        <label class="map-note">Copy fallback / export preview<textarea id="mapOutput" class="notes-area" rows="4" readonly aria-label="Map export preview"></textarea></label>
                    </div>
                </div>
            </div>
        </div>
    </div>


    <!-- Modal for Adding Custom Task -->
    <div class="modal-overlay" id="taskModal">
        <div class="modal">
            <h2>Add New Human Trace Task</h2>
            <div class="form-group">
                <label>Task Title</label>
                <input type="text" class="form-control" id="newTaskTitle" placeholder="e.g. glider_launch_and_flight">
            </div>
            <div class="form-group">
                <label>Category</label>
                <select class="form-control" id="newTaskCategory">
                    <option value="Combat">Combat</option>
                    <option value="Quests">Quests</option>
                    <option value="Crafting">Crafting</option>
                    <option value="Inventory">Inventory</option>
                    <option value="Economy">Economy</option>
                    <option value="NPC Services">NPC Services</option>
                    <option value="Farming">Farming</option>
                    <option value="Vehicles">Vehicles</option>
                </select>
            </div>
            <div class="form-group">
                <label>Scenario Identifier (for /trace start)</label>
                <input type="text" class="form-control" id="newTaskScenario" placeholder="e.g. glider_flight">
            </div>
            <div class="form-group">
                <label>Target Packet Classes (comma-separated)</label>
                <input type="text" class="form-control" id="newTaskPackets" placeholder="e.g. CSGliderFlightPacket, SCGliderStatePacket">
            </div>
            <div class="form-group">
                <label>Priority</label>
                <select class="form-control" id="newTaskPriority">
                    <option value="High">High</option>
                    <option value="Medium">Medium</option>
                    <option value="Low">Low</option>
                </select>
            </div>
            <div class="form-group">
                <label>Description & In-Game Instructions</label>
                <textarea class="form-control" id="newTaskInstructions" rows="3" placeholder="Step-by-step instructions for what to do in game..."></textarea>
            </div>
            <div style="display: flex; justify-content: flex-end; gap: 8px; margin-top: 20px;">
                <button class="btn" onclick="closeNewTaskModal()">Cancel</button>
                <button class="btn btn-primary" onclick="saveNewTask()">Add Task</button>
            </div>
        </div>
    </div>

    <div class="toast" id="toast">Command copied to clipboard!</div>

    <script>
        let tasks = [];
        let activeFilter = 'all';

        
        function switchMainTab(tabName) {
            localStorage.setItem('aaemu_active_tab', tabName);
            document.querySelectorAll('.nav-tab-btn').forEach(btn => btn.classList.remove('active'));
            document.querySelectorAll('.tab-pane').forEach(pane => pane.classList.remove('active'));

            const targetBtn = document.getElementById('tabBtn-' + tabName);
            const targetPane = document.getElementById('tabPane-' + tabName);

            if (targetBtn) targetBtn.classList.add('active');
            if (targetPane) targetPane.classList.add('active');

            if (tabName === 'coverage') {
                renderCoverageTab();
            } else if (tabName === 'map') {
                initWorldMap();
            }
        }

        let coverageDataCache = null;

        async function renderCoverageTab() {
            if (!coverageDataCache) {
                try {
                    const res = await fetch('/api/coverage');
                    if (!res.ok) throw new Error('/api/coverage HTTP ' + res.status);
                    coverageDataCache = await res.json();
                } catch(e) {
                    console.error("Coverage load error:", e);
                    document.getElementById('recommendedTracesList').innerHTML = '<li>Coverage unavailable: ' + e.message + ' — run ⚡ Run Coverage Mapper.</li>';
                    return;
                }
            }

            if (!coverageDataCache || !coverageDataCache.corpus_summary) return;
            const cs = coverageDataCache.corpus_summary;

            document.getElementById('covUniquePackets').innerText = cs.unique_packets ?? '--';
            document.getElementById('covTotalRecords').innerText = Number(cs.total_records || 0).toLocaleString();
            document.getElementById('covActionFamilies').innerText = (coverageDataCache.candidate_families || []).length;
            document.getElementById('covTracesParsed').innerText = cs.total_files ?? '--';
            // Telemetry strip: movement/event/malformed split + corpus freshness.
            const setText = (id, v) => { const el = document.getElementById(id); if (el) el.innerText = v; };
            setText('covMovement', Number(cs.movement_records || 0).toLocaleString());
            setText('covEvents', Number(cs.event_records || 0).toLocaleString());
            setText('covMalformed', Number(cs.malformed_records || 0).toLocaleString());
            setText('covGeneratedAt', coverageDataCache.generated_at || 'unknown');
            setText('covGaps', (coverageDataCache.gaps || []).length);

            // Next traces: summary.json `recommendations[]` (scenario_name/rationale).
            const nextList = document.getElementById('recommendedTracesList');
            const recs = coverageDataCache.recommendations || coverageDataCache.recommended_next_traces || [];
            if (recs.length > 0) {
                nextList.innerHTML = recs.slice(0, 5).map(t =>
                    `<li><strong>${t.scenario_name || t.action_or_scenario}</strong> [${t.priority || '—'}] &mdash; ${t.rationale || 'Unlocks new packets'}</li>`
                ).join('');
            } else {
                nextList.innerHTML = '<li>No recommendations in summary.json.</li>';
            }

            // Top packets: summary.json `packet_coverage.items[]` by observed_count.
            const tbody = document.getElementById('topPacketsTableBody');
            const items = (coverageDataCache.packet_coverage && coverageDataCache.packet_coverage.items)
                || coverageDataCache.top_observed_packets || [];
            if (items.length > 0) {
                const top = [...items].sort((a, b) => (b.observed_count ?? b.count ?? 0) - (a.observed_count ?? a.count ?? 0)).slice(0, 15);
                tbody.innerHTML = top.map(p =>
                    `<tr style="border-bottom: 1px solid rgba(48,54,61,0.5);">
                        <td style="padding: 8px; font-family: monospace; color: #79c0ff;">${p.packet || p.packet_class}</td>
                        <td style="padding: 8px; font-weight: 600;">${Number(p.observed_count ?? p.count ?? 0).toLocaleString()}</td>
                        <td style="padding: 8px; color: var(--text-muted);">${p.scenarios_seen ?? (p.scenarios ? p.scenarios.join(', ') : 'Various')}</td>
                    </tr>`
                ).join('');
            } else {
                tbody.innerHTML = '<tr><td colspan="3" style="padding: 12px; text-align: center; color: var(--text-muted);">No packet data in summary.json.</td></tr>';
            }
        }

        async function init() {
            try {
                const res = await fetch('/api/tasks');
                tasks = await res.json();
            } catch (err) {
                console.warn("API load failed, fallback to localStorage:", err);
                const local = localStorage.getItem('aaemu_trace_tasks');
                tasks = local ? JSON.parse(local) : [];
            }
            renderTasks();
            updateStats();
            fetchCoverageStats();

            const savedTab = localStorage.getItem('aaemu_active_tab') || 'tasks';
            switchMainTab(savedTab);

        }

        function saveTasksState() {
            localStorage.setItem('aaemu_trace_tasks', JSON.stringify(tasks));
            fetch('/api/tasks/update', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(tasks)
            }).catch(e => console.error("Auto-sync error:", e));
            updateStats();
        }

        async function fetchCoverageStats() {
            try {
                const res = await fetch('/api/coverage');
                if (!res.ok) return;
                const data = await res.json();
                if (data && data.corpus_summary) {
                    document.getElementById('observedPacketsStat').innerText = data.corpus_summary.unique_packets ?? '--';
                    document.getElementById('totalRecordsStat').innerText = Number(data.corpus_summary.total_records || 0).toLocaleString();
                    document.getElementById('actionFamiliesStat').innerText = (data.candidate_families || []).length;
                }
            } catch (e) {
                console.log("No coverage stats available yet.");
            }
        }

        async function runCoverageAnalysis() {
            const btn = document.getElementById('analyzeBtn');
            const orig = btn.innerText;
            btn.innerText = 'Analyzing...';
            btn.disabled = true;
            try {
                const res = await fetch('/api/analyze', { method: 'POST' });
                const result = await res.json();
                showToast(result.message || 'Coverage analysis complete!');
                fetchCoverageStats();
            } catch (e) {
                showToast('Failed to run coverage mapper: ' + e);
            } finally {
                btn.innerText = orig;
                btn.disabled = false;
            }
        }

        async function evaluateTaskTrace(taskId) {
            const btn = document.getElementById('eval-btn-' + taskId);
            const box = document.getElementById('eval-box-' + taskId);
            if (!btn || !box) return;

            const orig = btn.innerText;
            btn.innerText = 'Evaluating...';
            btn.disabled = true;

            try {
                const res = await fetch('/api/tasks/evaluate', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ task_id: taskId })
                });
                const report = await res.json();

                if (!report || report.error) {
                    box.style.display = 'block';
                    box.innerHTML = `<span style="color: var(--red);">Error: ${report.error || 'No trace found to evaluate.'}</span>`;
                    return;
                }

                box.style.display = 'block';
                const vClass = report.verdict === 'PASS' ? 'eval-verdict-pass' : (report.verdict === 'CAVEAT' ? 'eval-verdict-caveat' : 'eval-verdict-fail');
                
                let assertionsHtml = (report.packet_assertions || []).map(pa => `
                    <div style="color: ${pa.passed ? 'var(--green)' : 'var(--red)'}">
                        ${pa.passed ? '✓' : '✗'} <code>${pa.packet_name}</code>: ${pa.details}
                    </div>
                `).join('');

                let caveatsHtml = (report.caveat_notes || []).map(c => `<div style="color: var(--yellow)">⚠ ${c}</div>`).join('');
                let failsHtml = (report.failure_reasons || []).map(f => `<div style="color: var(--red)">✕ ${f}</div>`).join('');

                box.innerHTML = `
                    <div class="eval-header">
                        <span>Evaluation Verdict: <b class="${vClass}">[${report.verdict}]</b></span>
                        <span style="color: var(--text-muted)">Trace: ${report.trace_file.split('/').pop()} (${report.total_records} records, ${report.elapsed_seconds.toFixed(1)}s)</span>
                    </div>
                    <div class="eval-assertions">
                        ${assertionsHtml}
                        ${caveatsHtml}
                        ${failsHtml}
                    </div>
                `;

                if (report.verdict === 'PASS') {
                    showToast('Trace verified PASS! Target packets confirmed.');
                }
            } catch (err) {
                box.style.display = 'block';
                box.innerHTML = `<span style="color: var(--red);">Evaluation failed: ${err}</span>`;
            } finally {
                btn.innerText = orig;
                btn.disabled = false;
            }
        }

        function updateStats() {
            const total = tasks.length;
            const completed = tasks.filter(t => t.status === 'Complete').length;
            document.getElementById('totalCount').innerText = total;
            document.getElementById('completedCount').innerText = completed;
            const pct = total > 0 ? Math.round((completed / total) * 100) : 0;
            document.getElementById('tasksProgressBar').style.width = pct + '%';
        }

        function setFilter(f) {
            activeFilter = f;
            document.querySelectorAll('.filter-btn').forEach(btn => {
                btn.classList.toggle('active', btn.innerText.toLowerCase().includes(f.toLowerCase()) || (f === 'all' && btn.innerText === 'All Tasks'));
            });
            renderTasks();
        }

        function getCategoryBadgeClass(cat) {
            const c = (cat || '').toLowerCase();
            if (c.includes('combat')) return 'badge-combat';
            if (c.includes('quest')) return 'badge-quests';
            if (c.includes('craft')) return 'badge-crafting';
            if (c.includes('inv')) return 'badge-inventory';
            if (c.includes('econ')) return 'badge-economy';
            if (c.includes('farm')) return 'badge-farming';
            return 'badge-npc';
        }

        function renderTasks() {
            const list = document.getElementById('taskList');
            const search = (document.getElementById('searchBox').value || '').toLowerCase();
            list.innerHTML = '';

            const filtered = tasks.filter(t => {
                if (activeFilter !== 'all') {
                    if (['Pending', 'In Progress', 'Complete'].includes(activeFilter)) {
                        if (t.status !== activeFilter) return false;
                    } else if (t.category !== activeFilter) {
                        return false;
                    }
                }
                if (search) {
                    const matchText = (t.title + ' ' + t.scenario + ' ' + t.description + ' ' + (t.target_packets || []).join(' ')).toLowerCase();
                    if (!matchText.includes(search)) return false;
                }
                return true;
            });

            if (filtered.length === 0) {
                list.innerHTML = '<div style="text-align: center; color: var(--text-muted); padding: 48px;">No tasks match the active filter.</div>';
                return;
            }

            filtered.forEach(task => {
                const card = document.createElement('div');
                card.className = `task-card ${task.status.toLowerCase().replace(' ', '-')}`;

                const isComplete = task.status === 'Complete';
                const requiredChecklist = task.checklist || [];

                card.innerHTML = `
                    <div class="task-header" onclick="toggleTaskBody('${task.id}')">
                        <div>
                            <div class="task-title-group">
                                <span class="task-title">${task.title}</span>
                                <span class="badge ${getCategoryBadgeClass(task.category)}">${task.category}</span>
                                <span class="badge badge-priority-${task.priority.toLowerCase()}">Priority: ${task.priority}</span>
                            </div>
                            <div style="font-size: 12px; color: var(--text-muted); margin-top: 4px;">
                                Scenario: <code>${task.scenario}</code> &bull; Target: ${(task.target_packets || []).join(', ') || 'General Trace'}
                            </div>
                        </div>
                        <div style="display: flex; align-items: center; gap: 12px;" onclick="event.stopPropagation()">
                            <select class="task-status-select" onchange="updateTaskStatus('${task.id}', this.value)">
                                <option value="Pending" ${task.status === 'Pending' ? 'selected' : ''}>⏳ Pending</option>
                                <option value="In Progress" ${task.status === 'In Progress' ? 'selected' : ''}>🔄 In Progress</option>
                                <option value="Complete" ${task.status === 'Complete' ? 'selected' : ''}>✅ Complete</option>
                            </select>
                        </div>
                    </div>

                    <div class="task-body" id="body-${task.id}">
                        <p class="task-desc">${task.description || ''}</p>

                        <div class="scenario-box">
                            <div>
                                <span style="color: var(--text-muted)">In-Game Start:</span>
                                <span class="scenario-cmd">/trace start ${task.scenario}</span>
                            </div>
                            <button class="copy-btn" onclick="copyToClipboard('/trace start ${task.scenario}')">Copy Command</button>
                        </div>

                        <div class="section-header">Step-by-Step In-Game Instructions</div>
                        <ol style="margin-left: 20px; font-size: 13px; color: var(--text-main); margin-bottom: 16px;">
                            ${(task.instructions || []).map(inst => `<li style="margin-bottom: 4px;">${inst}</li>`).join('')}
                        </ol>

                        <div class="section-header">Required Completion Checklist</div>
                        <div class="checklist-group">
                            ${requiredChecklist.map((item, idx) => `
                                <label class="checklist-item ${item.done ? 'done' : ''}">
                                    <input type="checkbox" ${item.done ? 'checked' : ''} onchange="toggleChecklistItem('${task.id}', '${item.id}')">
                                    <span>${item.text}</span>
                                    ${item.required ? '<span class="tag-req">REQUIRED</span>' : ''}
                                </label>
                            `).join('')}
                        </div>

                        <!-- Trace Evaluation Widget -->
                        <div class="eval-box" id="eval-box-${task.id}"></div>

                        <div style="margin-top: 16px;">
                            <div class="section-header">Trace Notes & Coordinates</div>
                            <textarea class="notes-area" placeholder="Add character name, coordinates, or observations..." onchange="updateTaskNotes('${task.id}', this.value)">${task.notes || ''}</textarea>
                        </div>

                        <div style="display: flex; justify-content: space-between; align-items: center; margin-top: 16px; flex-wrap: wrap; gap: 8px;">
                            <div style="display: flex; gap: 8px;">
                                <button class="btn" id="eval-btn-${task.id}" onclick="evaluateTaskTrace('${task.id}')">🔍 Evaluate Trace</button>
                                <button class="copy-btn" onclick="copyToClipboard('/trace stop')">Copy /trace stop</button>
                            </div>
                            <button class="btn ${isComplete ? '' : 'btn-primary'}" onclick="markTaskComplete('${task.id}', ${!isComplete})">
                                ${isComplete ? '↩ Mark Incomplete' : '✓ Mark Task Complete'}
                            </button>
                        </div>
                    </div>
                `;
                list.appendChild(card);
            });
        }

        function toggleTaskBody(taskId) {
            const el = document.getElementById('body-' + taskId);
            if (el) {
                el.style.display = el.style.display === 'none' ? 'block' : 'none';
            }
        }

        function updateTaskStatus(taskId, newStatus) {
            const t = tasks.find(x => x.id === taskId);
            if (t) {
                t.status = newStatus;
                saveTasksState();
                renderTasks();
            }
        }

        function markTaskComplete(taskId, complete) {
            const t = tasks.find(x => x.id === taskId);
            if (t) {
                t.status = complete ? 'Complete' : 'Pending';
                if (complete && t.checklist) {
                    t.checklist.forEach(c => c.done = true);
                }
                saveTasksState();
                renderTasks();
            }
        }

        function toggleChecklistItem(taskId, itemId) {
            const t = tasks.find(x => x.id === taskId);
            if (t && t.checklist) {
                const item = t.checklist.find(c => c.id === itemId);
                if (item) {
                    item.done = !item.done;
                    const allDone = t.checklist.filter(c => c.required).every(c => c.done);
                    if (allDone && t.status !== 'Complete') {
                        t.status = 'Complete';
                    }
                    saveTasksState();
                    renderTasks();
                }
            }
        }

        function updateTaskNotes(taskId, notes) {
            const t = tasks.find(x => x.id === taskId);
            if (t) {
                t.notes = notes;
                saveTasksState();
            }
        }

        function copyToClipboard(text) {
            navigator.clipboard.writeText(text).then(() => {
                showToast('Copied: ' + text);
            }).catch(() => {
                showToast('Copied: ' + text);
            });
        }

        function showToast(msg) {
            const t = document.getElementById('toast');
            t.innerText = msg;
            t.style.display = 'block';
            setTimeout(() => { t.style.display = 'none'; }, 2500);
        }

        function openNewTaskModal() {
            document.getElementById('taskModal').style.display = 'flex';
        }

        function closeNewTaskModal() {
            document.getElementById('taskModal').style.display = 'none';
        }

        function saveNewTask() {
            const title = document.getElementById('newTaskTitle').value.trim();
            const category = document.getElementById('newTaskCategory').value;
            const scenario = document.getElementById('newTaskScenario').value.trim() || title.toLowerCase().replace(/\\s+/g, '_');
            const packetsRaw = document.getElementById('newTaskPackets').value.trim();
            const priority = document.getElementById('newTaskPriority').value;
            const instructionsText = document.getElementById('newTaskInstructions').value.trim();

            if (!title) {
                alert('Task title is required');
                return;
            }

            const targetPackets = packetsRaw ? packetsRaw.split(',').map(x => x.trim()).filter(Boolean) : [];
            const instructions = instructionsText ? instructionsText.split('\\n').filter(x => x.trim()) : [
                `Start trace: /trace start ${scenario}`,
                'Perform required action in game',
                'Stop trace: /trace stop'
            ];

            const newTask = {
                id: 'custom_' + Date.now(),
                title: title,
                category: category,
                priority: priority,
                scenario: scenario,
                status: 'Pending',
                description: instructionsText,
                target_packets: targetPackets,
                instructions: instructions,
                checklist: [
                    { id: 'step1', text: `Execute /trace start ${scenario}`, done: false, required: true },
                    { id: 'step2', text: 'Perform gameplay interaction in world', done: false, required: true },
                    { id: 'step3', text: 'Execute /trace stop', done: false, required: true }
                ],
                notes: ''
            };

            tasks.unshift(newTask);
            saveTasksState();
            closeNewTaskModal();
            renderTasks();
            showToast('New task created: ' + title);
        }

        // Map authoring is loaded below from the same source for both dashboards.

        window.onload = init;
    </script>
    <script src="/api/map/script"></script>
</body>
</html>
"""


class DashboardRequestHandler(BaseHTTPRequestHandler):
    tasks_file: str = "playertrace-coverage/dashboard_tasks.json"
    repo_root: str = "."

    def _set_headers(self, status: int = 200, content_type: str = "application/json"):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()

    def do_OPTIONS(self):
        self._set_headers(204)

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path

        if path == "/" or path == "/index.html":
            self._set_headers(200, "text/html; charset=utf-8")
            self.wfile.write(HTML_TEMPLATE.encode("utf-8"))
            return

        if path == "/api/map/script":
            script_path = os.path.join(os.path.dirname(__file__), "dashboard_map.js")
            with open(script_path, "rb") as stream:
                self._set_headers(200, "text/javascript; charset=utf-8")
                self.wfile.write(stream.read())
            return

        if path == "/api/map/image":
            key = urllib.parse.parse_qs(parsed.query).get("key", ["world"])[0]
            manifest = self._load_map_manifest()
            entry = next((m for m in manifest.get("maps", []) if m["key"] == key), None)
            image = os.path.join(self.repo_root, ".client_files", "dashboard-map",
                                 os.path.basename(entry["image"])) if entry else ""
            if image and os.path.isfile(image):
                with open(image, "rb") as stream:
                    self._set_headers(200, "image/jpeg")
                    self.wfile.write(stream.read())
            else:
                self._set_headers(404)
                self.wfile.write(json.dumps({"error": "Client map not prepared; run prepare_map_assets.py"}).encode("utf-8"))
            return

        if path == "/api/map/data":
            map_data_path = os.path.join(self.repo_root, "playertrace-coverage", "map_atlas_data.json")
            if os.path.exists(map_data_path):
                with open(map_data_path, "r", encoding="utf-8") as f:
                    content = json.load(f)
                content["map_manifest"] = self._load_map_manifest()
                self._set_headers(200, "application/json")
                self.wfile.write(json.dumps(content).encode("utf-8"))
            else:
                self._set_headers(404, "application/json")
                self.wfile.write(json.dumps({"error": "No map_atlas_data.json found"}).encode("utf-8"))
            return

        if path == "/api/map/nav-telemetry":
            heatmap_path = os.path.join(self.repo_root, "playertrace-coverage", "bot_nav_heatmaps.json")
            if os.path.exists(heatmap_path):
                with open(heatmap_path, "r", encoding="utf-8") as f:
                    content = json.load(f)
                self._set_headers(200, "application/json")
                self.wfile.write(json.dumps(content).encode("utf-8"))
            else:
                self._set_headers(404, "application/json")
                self.wfile.write(json.dumps({"error": "No nav telemetry heatmap found"}).encode("utf-8"))
            return

        if path == "/api/map/live":
            # Live bridge target: local loopback by default; point at the
            # tester (e.g. AAEMU_GAME_API_BASE=http://192.168.0.165:1280)
            # when the dashboard runs off-box. Never commit a host here.
            api_base = os.environ.get("AAEMU_GAME_API_BASE", "http://127.0.0.1:1280").rstrip("/")
            entities = []
            server_online = False
            bot_map = {}

            # 1. Query bots from BotControl API (if available)
            try:
                token = os.environ.get("AAEMU_BOT_CTRL_TOKEN", "aaemu-tester-token")
                req = urlreq.Request(api_base + "/api/bots", headers={"User-Agent": "AAEmu-Dashboard", "X-Auth-Token": token})
                with urlreq.urlopen(req, timeout=0.8) as resp:
                    if resp.status == 200:
                        server_online = True
                        bot_resp = json.loads(resp.read().decode("utf-8"))
                        bot_data = bot_resp.get("Bots") or bot_resp.get("Data") or bot_resp.get("data") or []
                        for b in bot_data:
                            name = b.get("Name") or b.get("name")
                            if name:
                                bot_map[name] = b
            except Exception:
                pass

            # 2. Query logged characters from AAEmu Game WebApi
            seen_names = set()
            try:
                req = urlreq.Request(api_base + "/api/world/logged-characters", headers={"User-Agent": "AAEmu-Dashboard"})
                with urlreq.urlopen(req, timeout=0.8) as resp:
                    if resp.status == 200:
                        server_online = True
                        chars = json.loads(resp.read().decode("utf-8"))
                        for c in chars:
                            if c.get("IsOnline", False) or c.get("isOnline", False):
                                name = c.get("Name") or c.get("name") or "Unknown"
                                char_id = c.get("Id") or c.get("id") or 0
                                is_bot = (name in bot_map) or name.startswith("Citizen") or "bot" in name.lower()
                                b_info = bot_map.get(name, {})
                                state = b_info.get("State") or b_info.get("state") or ("Active" if is_bot else "In World")

                                x = float(c.get("X") if c.get("X") is not None else (c.get("x") or 0))
                                y = float(c.get("Y") if c.get("Y") is not None else (c.get("y") or 0))
                                z = float(c.get("Z") if c.get("Z") is not None else (c.get("z") or 0))

                                seen_names.add(name)
                                entities.append({
                                    "id": f"{'bot' if is_bot else 'player'}_{char_id}",
                                    "name": name,
                                    "type": "bot" if is_bot else "player",
                                    "x": x,
                                    "y": y,
                                    "z": z,
                                    "level": c.get("Level") or c.get("level") or 1,
                                    "state": state
                                })
            except Exception:
                pass

            # 3. Add any active bots not reported in logged-characters
            for name, b in bot_map.items():
                if name not in seen_names:
                    state = b.get("State") or b.get("state") or "Active"
                    if state != "Dormant":
                        bx = float(b.get("X") if b.get("X") is not None else (b.get("x") or 0))
                        by = float(b.get("Y") if b.get("Y") is not None else (b.get("y") or 0))
                        bz = float(b.get("Z") if b.get("Z") is not None else (b.get("z") or 0))
                        if bx != 0 or by != 0:
                            entities.append({
                                "id": f"bot_{b.get('CharacterId') or b.get('characterId') or name}",
                                "name": name,
                                "type": "bot",
                                "x": bx,
                                "y": by,
                                "z": bz,
                                "level": 1,
                                "state": state
                            })

            self._set_headers(200, "application/json")
            self.wfile.write(json.dumps({"ok": True, "server_online": server_online, "entities": entities}).encode("utf-8"))
            return

        if path == "/api/map/routes":
            routes = self._get_routes_summary()
            self._set_headers(200, "application/json")
            self.wfile.write(json.dumps({"ok": True, "routes": routes}).encode("utf-8"))
            return

        if path.startswith("/api/map/route/"):
            rname = path[len("/api/map/route/"):].strip()
            if not rname.endswith(".json"):
                rname += ".json"
            safe_name = os.path.basename(rname)
            rpath = os.path.join(self.repo_root, "AAEmu.Game", "Data", "Routes", safe_name)
            if os.path.exists(rpath):
                with open(rpath, "r", encoding="utf-8") as f:
                    content = f.read()
                self._set_headers(200, "application/json")
                self.wfile.write(content.encode("utf-8"))
            else:
                self._set_headers(404, "application/json")
                self.wfile.write(json.dumps({"error": f"Route {safe_name} not found"}).encode("utf-8"))
            return

        if path == "/api/tasks":
            tasks = self._load_tasks()
            self._set_headers(200, "application/json")
            self.wfile.write(json.dumps(tasks, indent=2).encode("utf-8"))
            return

        if path == "/api/coverage":
            summary_json_path = os.path.join(self.repo_root, "playertrace-coverage", "summary.json")
            if os.path.exists(summary_json_path):
                with open(summary_json_path, "r", encoding="utf-8") as f:
                    content = f.read()
                self._set_headers(200, "application/json")
                self.wfile.write(content.encode("utf-8"))
            else:
                self._set_headers(404, "application/json")
                self.wfile.write(json.dumps({"error": "No summary.json found"}).encode("utf-8"))
            return

        self._set_headers(404, "text/plain")
        self.wfile.write(b"Not Found")

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path

        if path == "/api/tasks/update":
            content_length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(content_length)
            try:
                tasks_data = json.loads(body)
                self._save_tasks(tasks_data)
                self._set_headers(200, "application/json")
                self.wfile.write(json.dumps({"ok": True}).encode("utf-8"))
            except Exception as ex:
                self._set_headers(500, "application/json")
                self.wfile.write(json.dumps({"ok": False, "error": str(ex)}).encode("utf-8"))
            return

        if path == "/api/tasks/evaluate":
            content_length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(content_length)
            try:
                data = json.loads(body)
                task_id = data.get("task_id")
                tasks = self._load_tasks()
                task_spec = next((t for t in tasks if t.get("id") == task_id), None)
                if not task_spec:
                    self._set_headers(404, "application/json")
                    self.wfile.write(json.dumps({"error": f"Task '{task_id}' not found"}).encode("utf-8"))
                    return

                scenario = task_spec.get("scenario", task_id)
                traces_dir = os.path.join(self.repo_root, "traces", "player-actions")
                trace_path = find_latest_trace_for_scenario(scenario, traces_dir)
                if not trace_path:
                    self._set_headers(404, "application/json")
                    self.wfile.write(json.dumps({"error": f"No trace file found for scenario '{scenario}' in {traces_dir}"}).encode("utf-8"))
                    return

                report = evaluate_trace_file(trace_path, task_spec)
                self._set_headers(200, "application/json")
                self.wfile.write(json.dumps(asdict(report), indent=2).encode("utf-8"))
            except Exception as ex:
                self._set_headers(500, "application/json")
                self.wfile.write(json.dumps({"error": str(ex)}).encode("utf-8"))
            return

        if path == "/api/analyze":
            cmd = [
                sys.executable,
                os.path.join(self.repo_root, "Scripts", "playertrace-coverage", "playertrace_coverage.py"),
                os.path.join(self.repo_root, "traces", "player-actions"),
                "--repo-root", self.repo_root,
                "--out", os.path.join(self.repo_root, "playertrace-coverage")
            ]
            try:
                res = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
                if res.returncode == 0:
                    self._set_headers(200, "application/json")
                    self.wfile.write(json.dumps({"ok": True, "message": "Coverage analysis completed successfully!"}).encode("utf-8"))
                else:
                    self._set_headers(500, "application/json")
                    self.wfile.write(json.dumps({"ok": False, "error": res.stderr}).encode("utf-8"))
            except Exception as ex:
                self._set_headers(500, "application/json")
                self.wfile.write(json.dumps({"ok": False, "error": str(ex)}).encode("utf-8"))
            return

        if path == "/api/map/upload-route":
            content_length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(content_length)
            try:
                data = json.loads(body)
                route_name = data.get("RouteName") or data.get("name") or data.get("routeName") or "unnamed_route"
                route_name = "".join(c for c in route_name if c.isalnum() or c in ("-", "_")).strip()
                if not route_name:
                    route_name = "unnamed_route"

                # Ensure RouteName is explicitly set
                data["RouteName"] = route_name

                # Save locally to AAEmu.Game/Data/Routes/{route_name}.json
                routes_dir = os.path.join(self.repo_root, "AAEmu.Game", "Data", "Routes")
                os.makedirs(routes_dir, exist_ok=True)
                local_file = os.path.join(routes_dir, f"{route_name}.json")
                with open(local_file, "w", encoding="utf-8") as f:
                    json.dump(data, f, indent=2)

                # Deploy to Game server (.165) via SSH into .server_files/AAEmu.Game/Data/Routes/
                remote_deployed = False
                remote_err = None
                try:
                    payload = json.dumps(data, indent=2)
                    res = subprocess.run(
                        [
                            "ssh", "-o", "ConnectTimeout=3", "-o", "BatchMode=yes", "root@192.168.0.165",
                            f"mkdir -p /root/AAEmu/.server_files/AAEmu.Game/Data/Routes && cat > /root/AAEmu/.server_files/AAEmu.Game/Data/Routes/{route_name}.json"
                        ],
                        input=payload,
                        text=True,
                        capture_output=True,
                        timeout=6
                    )
                    if res.returncode == 0:
                        remote_deployed = True
                    else:
                        remote_err = res.stderr.strip()
                except Exception as ssh_ex:
                    remote_err = str(ssh_ex)

                waypoint_count = len(data.get("Actions") or data.get("points") or data.get("waypoints") or [])
                total_distance = data.get("TotalDistance") or data.get("totalDistance") or data.get("distance") or 0

                self._set_headers(200, "application/json")
                self.wfile.write(json.dumps({
                    "ok": True,
                    "routeName": route_name,
                    "localPath": local_file,
                    "remoteDeployed": remote_deployed,
                    "remoteError": remote_err,
                    "waypointCount": waypoint_count,
                    "totalDistance": total_distance
                }).encode("utf-8"))
            except Exception as ex:
                self._set_headers(500, "application/json")
                self.wfile.write(json.dumps({"ok": False, "error": str(ex)}).encode("utf-8"))
            return

        if path == "/api/map/generate-candidate":
            content_length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(content_length)
            try:
                req = json.loads(body)
                start_id = req.get("start_id")
                end_id = req.get("end_id")
                name = req.get("name") or f"candidate_{start_id[-4:]}_to_{end_id[-4:]}"
                name = "".join(c for c in name if c.isalnum() or c in ("-", "_")).strip()
                step_meters = float(req.get("step_meters", 25.0))

                sys.path.insert(0, os.path.join(self.repo_root, "Scripts", "playertrace-coverage"))
                import generate_candidate_routes
                junctions, edges = generate_candidate_routes.load_atlas()
                adj = generate_candidate_routes.build_graph(junctions, edges)

                route = generate_candidate_routes.generate_candidate_route(name, start_id, end_id, junctions, adj, step_meters)
                if not route:
                    self._set_headers(400, "application/json")
                    self.wfile.write(json.dumps({"ok": False, "error": f"No connected road path found between {start_id} and {end_id}."}).encode("utf-8"))
                    return

                # Save locally
                routes_dir = os.path.join(self.repo_root, "AAEmu.Game", "Data", "Routes")
                os.makedirs(routes_dir, exist_ok=True)
                out_path = os.path.join(routes_dir, f"{name}.json")
                with open(out_path, "w", encoding="utf-8") as f:
                    json.dump(route, f, indent=2)

                # Deploy to .165 remote server
                remote_deployed = False
                try:
                    payload = json.dumps(route, indent=2)
                    res = subprocess.run(
                        [
                            "ssh", "-o", "ConnectTimeout=3", "-o", "BatchMode=yes", "root@192.168.0.165",
                            f"mkdir -p /root/AAEmu/.server_files/AAEmu.Game/Data/Routes && cat > /root/AAEmu/.server_files/AAEmu.Game/Data/Routes/{name}.json"
                        ],
                        input=payload,
                        text=True,
                        capture_output=True,
                        timeout=6
                    )
                    if res.returncode == 0:
                        remote_deployed = True
                except Exception:
                    pass

                self._set_headers(200, "application/json")
                self.wfile.write(json.dumps({
                    "ok": True,
                    "routeName": name,
                    "localPath": out_path,
                    "remoteDeployed": remote_deployed,
                    "waypointCount": route.get("WaypointCount", 0),
                    "totalDistance": route.get("TotalDistance", 0),
                    "route": route
                }).encode("utf-8"))
            except Exception as ex:
                self._set_headers(500, "application/json")
                self.wfile.write(json.dumps({"ok": False, "error": str(ex)}).encode("utf-8"))
            return

        self._set_headers(404, "text/plain")
        self.wfile.write(b"Not Found")

    def _get_routes_summary(self):
        routes_dir = os.path.join(self.repo_root, "AAEmu.Game", "Data", "Routes")
        routes = []
        if not os.path.exists(routes_dir):
            return routes

        zones = []
        atlas_path = os.path.join(self.repo_root, "playertrace-coverage", "map_atlas_data.json")
        if os.path.exists(atlas_path):
            try:
                with open(atlas_path, "r", encoding="utf-8") as f:
                    atlas = json.load(f)
                    zones = atlas.get("zones", [])
            except Exception:
                pass

        for fpath in sorted(glob.glob(os.path.join(routes_dir, "*.json"))):
            fname = os.path.basename(fpath)
            rname = os.path.splitext(fname)[0]
            try:
                with open(fpath, "r", encoding="utf-8") as f:
                    data = json.load(f)
                actions = data.get("Actions") or []
                wps = [a for a in actions if a.get("ActionType") == "Waypoint"]
                total_dist = data.get("TotalDistance")
                if total_dist is None:
                    dist = 0.0
                    for i in range(len(wps) - 1):
                        p1, p2 = wps[i], wps[i+1]
                        dist += math.hypot(p2.get("X",0) - p1.get("X",0), p2.get("Y",0) - p1.get("Y",0))
                    total_dist = round(dist, 2)

                first_wp = wps[0] if wps else {}
                last_wp = wps[-1] if wps else {}

                xs = [p.get("X", 0) for p in wps]
                ys = [p.get("Y", 0) for p in wps]
                zs = [p.get("Z") for p in wps if p.get("Z") is not None]

                min_x = min(xs) if xs else 0
                max_x = max(xs) if xs else 0
                min_y = min(ys) if ys else 0
                max_y = max(ys) if ys else 0
                min_z = min(zs) if zs else None
                max_z = max(zs) if zs else None

                mid_x = (min_x + max_x) / 2
                mid_y = (min_y + max_y) / 2

                nuia_keywords = ["solzreed", "dewstone", "lilyut", "gweonid", "marianople", "two_crowns", "cinderstone", "halcyona", "hellswamp", "sanddeep", "white_arden"]
                haranya_keywords = ["arcum_iris", "tigerspine", "falcorth", "mahadevi", "solis", "villanelle", "silent_forest", "ynystere", "hasla", "perinoor", "rokhala"]

                route_str = (rname + " " + (data.get("RouteName") or "")).lower()
                if mid_x < 2000 and mid_y < 2000:
                    continent = "Instance"
                elif any(k in route_str for k in nuia_keywords):
                    continent = "Nuia"
                elif any(k in route_str for k in haranya_keywords):
                    continent = "Haranya"
                elif mid_x >= 17000:
                    continent = "Haranya"
                else:
                    continent = "Nuia"

                zone_hint = ""
                for z in zones:
                    zx = z.get("x", 0)
                    zy = z.get("y", 0)
                    zw = z.get("w", 0)
                    zh = z.get("h", 0)
                    if (zx - zw/2 <= mid_x <= zx + zw/2) and (zy - zh/2 <= mid_y <= zy + zh/2):
                        zone_hint = z.get("display_name") or z.get("name")
                        break

                routes.append({
                    "name": data.get("RouteName") or rname,
                    "filename": fname,
                    "author": data.get("Author") or "RedlineTool",
                    "total_distance": round(float(total_dist), 2),
                    "waypoint_count": len(wps),
                    "continent": continent,
                    "zone_hint": zone_hint,
                    "start": {"x": first_wp.get("X"), "y": first_wp.get("Y"), "z": first_wp.get("Z"), "label": first_wp.get("Label")},
                    "end": {"x": last_wp.get("X"), "y": last_wp.get("Y"), "z": last_wp.get("Z"), "label": last_wp.get("Label")},
                    "min_z": min_z,
                    "max_z": max_z,
                    "bounds": {"min_x": min_x, "max_x": max_x, "min_y": min_y, "max_y": max_y}
                })
            except Exception:
                continue

        return routes

    def _load_map_manifest(self):
        path = os.path.join(self.repo_root, ".client_files", "dashboard-map", "manifest.json")
        if not os.path.isfile(path):
            return {"maps": [], "error": "Client map assets missing. Run prepare_map_assets.py."}
        with open(path, encoding="utf-8") as stream:
            return json.load(stream)

    def _load_tasks(self) -> List[Dict[str, Any]]:
        full_path = os.path.join(self.repo_root, self.tasks_file)
        if os.path.exists(full_path):
            try:
                with open(full_path, "r", encoding="utf-8") as f:
                    return json.load(f)
            except Exception:
                pass
        self._save_tasks(DEFAULT_TASKS)
        return DEFAULT_TASKS

    def _save_tasks(self, tasks: List[Dict[str, Any]]) -> None:
        full_path = os.path.join(self.repo_root, self.tasks_file)
        os.makedirs(os.path.dirname(os.path.abspath(full_path)), exist_ok=True)
        with open(full_path, "w", encoding="utf-8") as f:
            json.dump(tasks, f, indent=2)


def run_server(port: int = 8085, host: str = "0.0.0.0", repo_root: str = "."):
    DashboardRequestHandler.repo_root = os.path.abspath(repo_root)
    server_address = (host, port)
    httpd = HTTPServer(server_address, DashboardRequestHandler)
    print(f"================================================================")
    print(f"  AAEmu PlayerTrace Task Dashboard running at:")
    print(f"  Local:   http://localhost:{port}")
    print(f"  Network: http://{host}:{port}")
    print(f"================================================================")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nStopping dashboard server...")
        httpd.server_close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Run AAEmu PlayerTrace Task Dashboard")
    parser.add_argument("--port", type=int, default=8085, help="Port to bind (default: 8085)")
    parser.add_argument("--host", default="0.0.0.0", help="Host address to bind (default: 0.0.0.0)")
    parser.add_argument("--repo-root", default=".", help="AAEmu repository root (default: .)")
    args = parser.parse_args()
    run_server(port=args.port, host=args.host, repo_root=args.repo_root)
