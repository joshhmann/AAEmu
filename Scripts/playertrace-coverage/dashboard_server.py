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
import json
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
            <button class="nav-tab-btn" id="tabBtn-milestones" onclick="switchMainTab('milestones')">🎯 Roadmap & Scorecard (M0–M10) <span class="tab-badge">75%</span></button>
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
            <!-- (1) TARGET BANNER: 1.2 = 1.2.4.13 = r208022 -->
            <div class="info-card" style="margin-bottom: 20px; border-left: 4px solid var(--green);">
                <div class="info-card-header">
                    <div class="info-card-title">Target — ArcheAge 1.2 = 1.2.4.13 = r208022</div>
                    <span class="status-badge status-closed">Pinned</span>
                </div>
                <div class="task-desc">Every <code>*Offsets.cs</code> header (<code>client_12_r208022</code>), <code>Docs/wiki/Client.md</code> ("1.2 (r208022)", names file <code>ArcheAge 1.2.4.13 (r208022)</code> at <code>Docs/wiki/Client.md:18</code>), and the server pak md5 pin the same 1.2 line. r208088 is a different, newer compact-data revision cited only in retired drift notes — opcodes stay pinned to r208022.</div>
                <div class="meta-row"><span>Client build</span><span class="meta-val">r208022 (1.2.4.13 is the marketing label for the same 1.2 line)</span></div>
                <div class="meta-row"><span>Canonical DB</span><span class="meta-val">AAEmu.Game/Data/compact.sqlite3 · md5 78b3bdbf038db3b927056106efdf91af · 679 tables</span></div>
                <div class="meta-row"><span>BUG-005 ruling (opcode-vs-DB parity / r208088 drift)</span><span class="meta-val" style="color: var(--yellow)">UNRULED — confirm r208088 is dead history or re-open the drift</span></div>
                <div class="meta-row"><span>Tab freshness</span><span class="meta-val">Built 2026-09-15 · audit HEAD e93e3df2d · no doc maps md5 to revision today</span></div>
            </div>
            <!-- Evidence-letter scope legend (every letter scoped inline below) -->
            <div class="info-card" style="margin-bottom: 20px;">
                <div class="info-card-header">
                    <div class="info-card-title">Evidence letters — scoped</div>
                </div>
                <div class="task-desc"><strong>ledger-A</strong> = automated gate/soak evidence (test or soak PASS) · <strong>ladder-A</strong> = autonomous bot/actor evidence (proxy function, NEVER human feel) · <strong>G</strong> = society/scale-ladder evidence (G2-A5/A3 population legs) · <strong>H</strong> = Josh-only human-feel verdict (every mechanic H is U until Josh runs it) · <strong>R</strong> = restart-persistence · <strong>S</strong> = soak · <strong>L</strong> = load · <strong>W/C</strong> = wired/canonical. Data living outside the repo renders as <span style="background:rgba(248,81,73,0.2);color:#f85149;border:1px solid rgba(248,81,73,0.4);padding:1px 8px;border-radius:10px;font-size:11px;font-weight:700;">OUT-OF-REPO</span>, never as verified.</div>
            </div>
            <div class="info-card vision-card" style="margin-bottom: 20px; border-left: 4px solid var(--purple);">
                <div class="info-card-header">
                    <div class="info-card-title">Vision — a living world that plays itself, verified by humans</div>
                </div>
                <div class="task-desc">AAEmu bots must grind, farm, trade, sail, and siege through <strong>real engine paths</strong> (never parallel gameplay systems — Lane B composes around ordinary Character records per AGENTS.md rules #9/#10), while human traces supply the ground-truth packet telemetry that proves each slice. Scorecard per milestone: <strong>ledger-A</strong> = automated evidence (gate/soak PASS), <strong>H</strong> = Josh-only human-feel verdict; a milestone is not done until both are recorded.</div>
                <div class="meta-row"><span>Exit bar (every milestone)</span><span class="meta-val">Gate green + soak green + ledger-A evidence linked + H verdict (or explicit H=UNKNOWN)</span></div>
                <div class="meta-row"><span>Current frontier</span><span class="meta-val">M7 party play &rarr; M8 convoys/auction &rarr; M9 siege &rarr; M10 naval</span></div>
            </div>

            <!-- (2) PER-MILESTONE ROWS M0-M10 -->
            <div class="section-header" style="font-size:14px;color:#fff;margin:20px 0 4px 0;">Per-milestone rows M0–M10 — claimed state · evidence (repo path) · freshness (SHA/date)</div>
            <div class="milestone-grid">
                <!-- M0 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M0 · Foundation</div>
                        <span class="status-badge status-closed">Closed (100%)</span>
                    </div>
                    <div class="task-desc">DI host, topological ManagerOrchestrator boot, Login Kestrel TCP, reflection-discovered GameData loaders. The single H=COMPLETE on the books (foundation, not gameplay).</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">CLOSED · H=COMPLETE (foundation only)</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · SCORECARD.md</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M1 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M1 · Solzreed Golden Route & Core Defects</div>
                        <span class="status-badge status-closed">Closed (100%)</span>
                    </div>
                    <div class="task-desc">Solzreed starter line, early game quest progression, and engine defect chain BUG-007 through BUG-013.</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">CLOSED · ledger-A=2 · H=UNKNOWN (Josh verdict open)</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · scorecard-explorations/generated/leveling-loop-2026-08-25.md</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M2 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M2 / G1 · 100% Census Quest Coverage</div>
                        <span class="status-badge status-closed">Closed (100%)</span>
                    </div>
                    <div class="task-desc">Every live context PASS or registered-drop or doc-SKIP with zero unexplained failures. G1 denominator stated three ways (4579 vs 4587 vs 4573+6) — see weakest links.</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">CLOSED · ledger-A=2 (G1 Gate 2026-08-10) · H=UNKNOWN</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">SCORECARD.md · ROADMAP.md</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M3 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M3a/b · Homestead Shell & Persistence</div>
                        <span class="status-badge status-closed">Closed (100%)</span>
                    </div>
                    <div class="task-desc">House placement, multi-step construction, decoration limits (9/9 tables wired), and crash-safe MySQL property persistence.</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">CLOSED · kill -9 asserted mid-save (p95 1301ms) · H=UNKNOWN</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · SCORECARD.md</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M4 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M4 · Trade, Crafting & Transport Integrity</div>
                        <span class="status-badge status-closed">Closed (100%)</span>
                    </div>
                    <div class="task-desc">Harvest crops, craft trade packs, load cargo onto vehicle, drive 3-leg trade routes, and sell to gold traders. Deployed-cell acknowledged stale — see weakest links.</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">CLOSED · M4ExitIntegratedSessionTests PASS · H=UNKNOWN</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · SCORECARD.md</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M5 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M5 · Gameplay Actor Contract Surface</div>
                        <span class="status-badge status-closed">Closed (100%)</span>
                    </div>
                    <div class="task-desc">Seam decoupling bots from network: Move, Stop, Target, Cast, Observe, Interact, Loot, UseItem, Mount/Dismount, Equip.</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">CLOSED · fast gate 3,214/0 · zero engine-file drift</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · scorecard-explorations/generated/m5.3-core-surface-exit.md</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M6 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M6 · Deterministic PlayerBot Framework</div>
                        <span class="status-badge status-closed">Closed (100%)</span>
                    </div>
                    <div class="task-desc">Durable bots across server reboots, coordinate preservation, roll/yaw SQL fix, /bot restore, and 10-bot 6-hour green soak. PB-006 count lacks SHA — see weakest links.</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">CLOSED · PB-006 CLOSED · H=UNKNOWN</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · SCORECARD.md</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M7 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M7 · Adventurer & Party Bots</div>
                        <span class="status-badge status-active">Active (85%)</span>
                    </div>
                    <div class="task-desc">Autonomous mob grinding, combat rotations, sustain retreats, corpse looting, equip upgrades, and party formation. Solo 100% (Quest 250&rarr;330 loop).</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">ACTIVE · next: party follow-leader & assist targeting</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · scorecard-explorations/generated/m7-adventurer-spike.md</span></div>
                    <div class="meta-row"><span>Exit criteria</span><span class="meta-val">Party assist + follow-leader soak green + H party-play verdict</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M8 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M8 · Living World & Economy</div>
                        <span class="status-badge status-active">Active (40%)</span>
                    </div>
                    <div class="task-desc">Autonomous bot farming, specialty crafting, cross-zone cart convoys, merchant restock, and auction house trading. Runtime leg QUALIFIED (accepted transient + INVALID-by-design); full M8 unclaimed; no ledger row (table stops at M7).</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">ACTIVE · runtime leg QUALIFIED · full M8 UNCLAIMED</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · soak-artifacts/smoke-rb/20260912-014234</span></div>
                    <div class="meta-row"><span>Exit criteria</span><span class="meta-val">Convoy run + auction trade loop + economy soak green + H market verdict</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M9 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M9 · Dominion & Castle Sieges</div>
                        <span class="status-badge status-queued">Queued (25%)</span>
                    </div>
                    <div class="task-desc">Auroria territory claims, castle building, siege battle timers, tank/trebuchet war machines, and wall destruction. M8.5 likewise unclaimed. Dominion runtime report is out-of-repo.</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">UNCLAIMED · DominionManager + 3 siege tables wired</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · Dominion runtime: OUT-OF-REPO</span></div>
                    <div class="meta-row"><span>Exit criteria</span><span class="meta-val">Timed siege battle + wall destruction + H siege verdict (+ M9 substrate approval, Josh)</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>

                <!-- M10 -->
                <div class="info-card">
                    <div class="info-card-header">
                        <div class="info-card-title">M10 · High Seas & Naval Warfare</div>
                        <span class="status-badge status-queued">Queued (20%)</span>
                    </div>
                    <div class="task-desc">Naval trade runs to Freedich Island, ship cannon combat, ocean boarding, sinking physics, and the Kraken world boss.</div>
                    <div class="meta-row"><span>Claimed state</span><span class="meta-val">UNCLAIMED · VehicleMovementModel, cargo snapping</span></div>
                    <div class="meta-row"><span>Evidence (repo path)</span><span class="meta-val">ROADMAP.md · SCORECARD.md</span></div>
                    <div class="meta-row"><span>Exit criteria</span><span class="meta-val">Freedich run + cannon sinking + H naval verdict</span></div>
                    <div class="meta-row"><span>Freshness</span><span class="meta-val">2026-09-15 · HEAD e81a18161</span></div>
                </div>
            </div>

            <!-- (3) WEAKEST LINKS -->
            <div class="section-header" style="font-size:14px;color:#fff;margin:20px 0 4px 0;">Weakest links — audit §2 (first-class rows, not footnotes)</div>
            <div class="milestone-grid">
                <div class="info-card" style="border-left: 4px solid var(--green);">
                    <div class="info-card-header"><div class="info-card-title">Understated rows corrected 2026-09-15</div><span class="status-badge status-closed">Fixed</span></div>
                    <div class="task-desc">AGGRO-PACK-01 → W/A=1 (<code>NpcGameData.cs:177</code> + <code>Behavior.cs:411</code> + tests), RESPAWN-LADDER-01 → W/A=1 (<code>ResurrectionGameData.cs:52</code> + <code>CharacterCombat.cs</code> + tests), deferred mount-riding → LANDED (PB-MOUNT 09-03). H stays U on all three.</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--red);">
                    <div class="info-card-header"><div class="info-card-title">Newest runtime evidence lives out of repo</div><span class="status-badge status-active">OUT-OF-REPO</span></div>
                    <div class="task-desc">A5 tier-3, C5 re-soak, 09-13 live-E2E six, Q6, Mail S3, Dominion reports live in <code>/root/aaemu-e2e*</code> lane dirs on dirty trees at old HEADs — the tab cannot link them as verified. Only <code>soak-artifacts/smoke-rb/20260912-014234</code> is in-repo. Rule going forward: tab-cited reports must live under <code>scorecard-explorations/generated/</code>.</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--yellow);">
                    <div class="info-card-header"><div class="info-card-title">Gateless PB-006 count, no SHA</div><span class="status-badge status-active">Freshness</span></div>
                    <div class="task-desc">PB-006 3211/0/1 count cited without a gate SHA — re-run under a pinned SHA or downgrade to proxy. No ledger rows exist for M8/M9/M10/A3/A4/A5/Q-lanes; post-M7 evidence lives only in prose docs.</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--yellow);">
                    <div class="info-card-header"><div class="info-card-title">Navigate counts 5/8/9 unreconciled</div><span class="status-badge status-active">Reconcile</span></div>
                    <div class="task-desc">Navigate evidence counts 5 vs 8 vs 9 across sources — pick one pinned count with SHA/date or list all three as distinct legs (ledger-A vs ladder-A scoped).</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--yellow);">
                    <div class="info-card-header"><div class="info-card-title">COMBAT-01 vs REPAIR-01 grading split</div><span class="status-badge status-active">Needs ruling</span></div>
                    <div class="task-desc">COMBAT-01 W=1 vs REPAIR-01 W=2 grading split needs a ruling before either row can anchor a milestone exit.</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--yellow);">
                    <div class="info-card-header"><div class="info-card-title">Frozen upstream snapshot + minor staleness</div><span class="status-badge status-active">Refresh</span></div>
                    <div class="task-desc">Upstream tracker frozen 2026-08-03. Also: M8 QUALIFIED has no ledger row (table stops at M7); A5 stamp mismatch; mirage-duel report unregistered; M4 deployed-cell acknowledged stale; G1 denominator three ways (4579/4587/4573+6); auction-fix SHA pending-vs-cited. 33 census rows all-U (the 1.2 long tail); R/S columns blank outside M3b/M4/AUCTION/DOMINION; newest A-grades rest on single-machine unversioned reports.</div>
                </div>
            </div>

            <!-- (4) CODE-BUG WORKLIST -->
            <div class="section-header" style="font-size:14px;color:#fff;margin:20px 0 4px 0;">Code-bug worklist — audit §1 (list as work, not claims)</div>
            <div class="milestone-grid">
                <div class="info-card" style="border-left: 4px solid var(--red);">
                    <div class="info-card-header"><div class="info-card-title">CrimeManager registered twice</div><span class="status-badge status-active">Open</span></div>
                    <div class="task-desc">Double DI registration in <code>AAEmu.Game/Program.cs:167-168</code> and <code>:448-449</code> — dedupe to one registration.</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--red);">
                    <div class="info-card-header"><div class="info-card-title">/doodad command collision</div><span class="status-badge status-active">Open</span></div>
                    <div class="task-desc"><code>CrimeCmd.cs:13</code> and <code>DoodadCmd.cs:12</code> both claim <code>["doodad"]</code> — crime subcommands unreachable if DoodadCmd wins registration. Rename one verb.</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--yellow);">
                    <div class="info-card-header"><div class="info-card-title">Possibly-dead managers need verification</div><span class="status-badge status-active">Verify first</span></div>
                    <div class="task-desc"><code>SlaveManager, MateManager, GimmickManager, AiGeodataManager, TransferManager, SpawnManager, PhysicsManager, SphereQuestManager</code> have no DI registration and live <code>.Instance</code> callsites appear only in comments — verification pass required before calling them dead (bots use mounts/slaves through other paths).</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--yellow);">
                    <div class="info-card-header"><div class="info-card-title">Opcode slots: 0x9f + inferred mail opcode</div><span class="status-badge status-active">Verify</span></div>
                    <div class="task-desc"><code>CSOffsets.cs</code>: <code>0x9f</code> claimed unknown AND assigned (<code>CSTakeAttachmentSequentially</code>); <code>CSReturnMailPacket=0x0a2</code> strongly inferred, not client-verified. ~20 opcode slots have neither class nor registration; 13 parked at <code>0xfff</code> by design.</div>
                </div>
                <div class="info-card" style="border-left: 4px solid var(--yellow);">
                    <div class="info-card-header"><div class="info-card-title">Stray 0-byte DB placeholder at repo root</div><span class="status-badge status-queued">Cleanup</span></div>
                    <div class="task-desc">Repo-root <code>compact.sqlite3</code> is a 0-byte placeholder (2026-09-03, ignored, untracked) from a wrong-path tool connect — delete on next cleanup pass; real DB <code>AAEmu.Game/Data/compact.sqlite3</code> untouched.</div>
                </div>
            </div>

            <!-- (5) H-GATE INTAKE — visually separated from A/R/L evidence -->
            <div class="info-card" style="margin: 20px 0; border: 2px dashed var(--purple);">
                <div class="info-card-header">
                    <div class="info-card-title">H-gate intake — Josh-owned, separate from ledger-A / ladder-A / R / L evidence</div>
                    <span class="status-badge status-queued">Human only</span>
                </div>
                <div class="task-desc">Nothing here counts as A/R/L evidence and nothing here closes a milestone by itself. H means an ACTUAL PLAYER completing the curated scenario — never a bot or scripted actor (bot evidence is ladder-A proxy, recorded under ledger-A functional with an explicit proxy label). H stays U (UNKNOWN) until Josh runs it.</div>
                <div class="meta-row"><span>H gates #1–#4 (+ #5 feel)</span><span class="meta-val">Josh-only acceptance verdicts, open</span></div>
                <div class="meta-row"><span>W4-1 – W4-4 (deploy currency)</span><span class="meta-val">Open — Field-Guide worksheets</span></div>
                <div class="meta-row"><span>PB-005 tour</span><span class="meta-val">Open — Josh tour required</span></div>
                <div class="meta-row"><span>Q7 rulings</span><span class="meta-val">Blocked — Josh rulings required (Q8 likewise blocked)</span></div>
                <div class="meta-row"><span>M9 substrate approval</span><span class="meta-val">Open — Josh approval gates M9 start</span></div>
                <div class="meta-row"><span>Per-mestone H verdicts (M1–M10)</span><span class="meta-val">All U except M0 foundation COMPLETE — not gameplay</span></div>
            </div>

            <!-- (6) DOC-FIX QUEUE -->
            <div class="section-header" style="font-size:14px;color:#fff;margin:20px 0 4px 0;">Doc-fix queue — audit §4 (P0 first, file:line pointers)</div>
            <div class="milestone-grid">
                <div class="info-card" style="border-left: 4px solid var(--red);">
                    <div class="info-card-header"><div class="info-card-title">P0: CONTRIBUTING hides the NEVER-push rule</div><span class="status-badge status-active">P0</span></div>
                    <div class="task-desc"><code>CONTRIBUTING.md</code> instructs fork-and-PR with zero mention of the permanent NEVER-push-to-upstream rule — following it violates the top-line rule.</div>
                </div>
                <div class="info-card">
                    <div class="info-card-header"><div class="info-card-title">Clone URLs point at upstream, not the fork</div><span class="status-badge status-queued">Queued</span></div>
                    <div class="task-desc">Installation (×2), Docker guide, Dependencies clone <code>AAEmu/AAEmu</code> instead of the fork.</div>
                </div>
                <div class="info-card">
                    <div class="info-card-header"><div class="info-card-title">4 dangling internal refs</div><span class="status-badge status-queued">Queued</span></div>
                    <div class="task-desc"><code>Docs/networking.md</code> (real: <code>AAEmu.Login/Docs/networking.md</code>) · <code>tools/quest-graph</code> + <code>tools/gamedata-graph</code> (absent) · root <code>scorecard-explorations/*</code> links (content lives in a worktree) · <code>Docs/JOSH-QAT-WAVE4.md</code> case/path.</div>
                </div>
                <div class="info-card">
                    <div class="info-card-header"><div class="info-card-title">Stale currency + census conflicts</div><span class="status-badge status-queued">Queued</span></div>
                    <div class="task-desc">~26 wiki pages stamped 2026-08-05 (incl. skill-blessed setup pages) · census 153/153 vs 86/97 vs 88/97 · <code>Docs/wiki/Project-Status.md</code> presents 08-05 snapshot as current · <code>AAEmu.Login/README.md</code> boot-failing config (no <code>GameServers</code>) · Track-B sample omits SecretKey/internal ports &rarr; guaranteed Maintenance.</div>
                </div>
                <div class="info-card">
                    <div class="info-card-header"><div class="info-card-title">Port tables + repetition + absolute links</div><span class="status-badge status-queued">Queued</span></div>
                    <div class="task-desc">Four overlapping port tables, none canonical (recommend <code>REFERENCE.md</code> as home) · FAQ omits 1234 · Aspire omits 1234/1280/15133 · config-precedence and GameServers-not-MySQL worded 5–7× · help pages ×4 overlap · absolute <code>file:///root/aaemu-dev</code> links (START-HERE, World page) are dev-box-only.</div>
                </div>
            </div>
            <div class="info-card" style="margin-top: 20px;">
                <div class="task-desc">In-repo evidence rule going forward: reports cited by this tab must live under <code>scorecard-explorations/generated/</code>. 09-05 M3a H-vs-proxy correction on record — H discipline held.</div>
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
                                <button class="btn" onclick="downloadDraft()">Save draft</button>
                                <button class="btn" onclick="document.getElementById('routeImport').click()">Open route / draft</button>
                                <input type="file" id="routeImport" accept=".json,application/json" hidden onchange="importRoute(this)">
                                <button class="btn btn-primary" id="exportMapperBtn" onclick="exportMapperRoute()" disabled>Export game route</button>
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

            # 1. Query logged characters from AAEmu Game WebApi
            try:
                req = urlreq.Request(api_base + "/api/world/logged-characters", headers={"User-Agent": "AAEmu-Dashboard"})
                with urlreq.urlopen(req, timeout=0.8) as resp:
                    if resp.status == 200:
                        server_online = True
                        chars = json.loads(resp.read().decode("utf-8"))
                        for c in chars:
                            if c.get("IsOnline", False) or c.get("isOnline", False):
                                entities.append({
                                    "id": f"player_{c.get('Id') or c.get('id')}",
                                    "name": c.get("Name") or c.get("name"),
                                    "type": "player",
                                    "x": float(c.get("X") or c.get("x") or 0),
                                    "y": float(c.get("Y") or c.get("y") or 0),
                                    "z": float(c.get("Z") or c.get("z") or 0),
                                    "level": c.get("Level") or c.get("level") or 1,
                                    "state": "In World"
                                })
            except Exception:
                pass

            # 2. Query bots from BotControl API
            try:
                token = os.environ.get("AAEMU_BOT_CTRL_TOKEN", "")
                req = urlreq.Request(api_base + "/api/bots", headers={"User-Agent": "AAEmu-Dashboard", "X-Auth-Token": token})
                with urlreq.urlopen(req, timeout=0.8) as resp:
                    if resp.status == 200:
                        server_online = True
                        bot_resp = json.loads(resp.read().decode("utf-8"))
                        bot_data = bot_resp.get("Bots") or bot_resp.get("Data") or bot_resp.get("data") or []
                        for b in bot_data:
                            state = b.get("State") or b.get("state") or "Active"
                            if state != "Dormant":
                                entities.append({
                                    "id": f"bot_{b.get('CharacterId') or b.get('characterId')}",
                                    "name": b.get("Name") or b.get("name"),
                                    "type": "bot",
                                    "x": float(b.get("X") or b.get("x") or 0),
                                    "y": float(b.get("Y") or b.get("y") or 0),
                                    "z": float(b.get("Z") or b.get("z") or 0),
                                    "state": state
                                })
            except Exception:
                pass

            self._set_headers(200, "application/json")
            self.wfile.write(json.dumps({"ok": True, "server_online": server_online, "entities": entities}).encode("utf-8"))
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

        self._set_headers(404, "text/plain")
        self.wfile.write(b"Not Found")

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
