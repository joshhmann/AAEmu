# AAEmu Living World — Canonical Notes (AzerothCore Inspiration)

> Consolidated 2026-08-03 from Josh + Codex review sessions. This is the
> philosophy + architecture reference for the Living World project. The
> roadmap (ROADMAP.md) operationalizes it; this doc is the why.

## Core Philosophy

**PlayerBots are not the feature. The living world is the feature.**

The objective is not to simulate thousands of online players. The objective
is to recreate the emergent systems that made early ArcheAge memorable:

- Homesteads · Farming · Illegal tree farms · Trade pack runs · Caravans
- Piracy · Crime · Trials · Prison · Villages · Persistent economies

Bots exist to keep those systems alive even when players are offline.

## Lessons from AzerothCore

### PlayerBots

The most important architectural lesson:

- Bots use real accounts
- Bots use real characters
- Bots use normal persistence
- Bots use normal inventory
- Bots use normal quests
- Bots use normal economy

**Only the controller is synthetic.**

```
Managed Bot Account → Headless/Internal Session → Normal Character → PlayerBot Controller
```

AAEmu follows the same philosophy. Recommendation:

- One managed bot account per bot (initially)
- Real login/user row
- Real character row
- Runtime controller attached after login
- No parallel character database

### Headless Sessions

Bots should NOT emulate a network client. Instead:

```
BotManager → Internal Game Session → Normal Character Load → Attach PlayerBot Controller
```

Advantages: reuses gameplay, reuses persistence, easier debugging, easier
maintenance, no fake packet spam.

## Inspiration Modules

1. **PlayerBots** — foundation. Movement, combat, questing, inventory,
   groups, mounts, survival. Everything else builds on this.

2. **Dungeon Clear** — excellent example of a specialized activity module.
   Layers new behaviors on top rather than modifying the core.
   Ideas to adopt: goal modules, deterministic routes, automated regression
   runs, replayable seeds, JSON traces, failure replay, GM harness.
   Equivalent AAEmu modules: Adventure Route, Farm Maintenance, Trade Run,
   Fishing, Dungeon, Caravan, Siege.

3. **AH Bot Plus** — a Market Service, not PlayerBot AI.
   - Bootstrap mode: seed market, prevent empty AH, configurable supply.
   - Living mode: bots list real crafted goods, sell harvested goods;
     synthetic listings reduced over time.
   - Goal: eventually almost all listings originate from actual bot
     production.

4. **Player Bot Level Brackets** — generalize into a **Population
   Director**. Balance levels, zones, factions, professions, classes,
   property ownership, human presence, activity density. This controls
   where bots become active.

5. **LLM Chatter** — LLM is asynchronous:
   `Game Event → Queue → External Bridge → Response → Chat Delivery`.
   Never block gameplay.

6. **LLM Guide** — separate from chatter. Grounds factual answers in live
   server data (nearest housing zone, seed vendors, trade pack recipes,
   nearby workstations, quest chains). Truth comes from the database, not
   model memory.

## Three Layers of AI

1. **Gameplay AI — deterministic.** Combat, farming, crafting, navigation,
   trade packs, crime, prison, economy, schedules. Must work without any LLM.

2. **Social AI — mostly templates.** Greetings, pirate taunts, trade
   chatter, farming complaints, jury comments, town gossip, prison jokes.
   Generated from: event + personality + relationship + location.
   No API required.

3. **Narrative AI — optional.** Longer conversations, memories, rumors,
   storytelling, flavor. Runs through an external API or local model.
   If disabled, the world continues functioning normally.

## Living World Principle

**The server must remain fully playable if every AI service disappears.**

- OpenRouter dies → farming continues, trade runs continue, prison works,
  economy works, combat works, bots continue schedules.
- Only unique conversations disappear.

**LLMs provide flavor — not infrastructure.**

## Population Philosophy

Do NOT simulate 1000 fully active players. Instead:

```
1000 persistent citizens
        ↓
Population Director
        ↓
Fidelity selection
        ↓
Only nearby or relevant bots become expensive
```

Fidelity tiers:

| Tier | Name | Simulation |
|------|------|-----------|
| 1 | Full PlayerBot | Combat, navigation, parties |
| 2 | Reduced simulation | Coarse movement, trade, farming |
| 3 | Scheduled simulation | Harvest timers, crafting, travel progress |
| 4 | Dormant | Loaded only when needed |

## Long-Term Vision

```
AAEmu
├── Core Gameplay
├── Gameplay Actor Contract
├── PlayerBot Runtime
├── Population Director
├── Activity Modules
│   ├── Farming · Trading · Adventure · Dungeon
│   ├── Crime · Siege · Fishing
│   └── (future: Fishing Fleet, Festival Coordinator…)
├── Economy Services
├── Social Services
├── Guide Services
└── Test Harness
```

## Guiding Principle

The goal is not to create smarter bots. The goal is to create systems that
naturally generate stories:

- Illegal tree farms discovered
- Trade caravans ambushed
- Pirates changing routes
- Jury trials
- Prison sentences
- Farmers competing for land
- Villages specializing in professions
- Markets reacting to supply
- Real players joining an already-living world

**If players log in and feel like the world has been alive without them,
the project has succeeded.**
