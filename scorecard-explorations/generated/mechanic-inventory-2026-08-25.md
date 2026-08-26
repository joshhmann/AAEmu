# Master Mechanic Inventory — ArcheAge 1.2 completeness census (2026-08-25)

Workstream: completeness census. Reconciles every known player-facing 1.2 mechanic
against the Global mechanic ledger in `SCORECARD.md` across four evidence sources:

1. **Opcodes** — `AAEmu.Game/Core/Packets/C2G/CSOffsets.cs` (all `CS*` constants,
   clustered into system families below) + G2C/`SC*` packet files + Stream (`CT*`) protocol.
2. **Canonical data** — `AAEmu.Game/Data/compact.sqlite3` r208022, 679 tables
   (`SELECT name FROM sqlite_master WHERE type='table'`), grouped by prefix/domain,
   cross-checked against the Domain scorecard + Zero-data-wired list in `SCORECARD.md`.
3. **Code surfaces** — 65 concrete managers in `AAEmu.Game/Core/Managers/` (+ `World/`,
   `Bots/`, Id-managers) via `graphify-out/GRAPH_REPORT.md` community hubs.
4. **Canonical 2014 system knowledge** — the owner's checklist (labor, regrade, gliders,
   music, court, piracy, naval, dominion, …).

**Rules honored:** docs-only; no builds; grades cited only where a cited query/report
proves them; concurrent dossier titles **justice, economy, pvp, dominion, ships, mail**
treated as known-covered (marked `lane:` below, interiors never duplicated);
SCORECARD.md itself NOT edited — §3 contains the NEW-ROW proposals for the later docs pass.

---

## 0. Opcode families found in CSOffsets.cs (clustered)

| Family | Opcodes (abbreviated `CS…`) |
|---|---|
| Char/session | CreateCharacter, DeleteCharacter, CancelCharacterDelete, ListCharacter, SelectCharacter, SpawnCharacter, LeaveWorld, CancelLeaveWorld, RefreshInCharacterList, EditCharacter, NotifyInGame(Packet/Completed), RequestUIData, SaveUIData, SaveTutorial, RestrictCheck, RequestSecondPasswordKeyTables |
| Move/combat/skill | MoveUnit, IdleStatus, ChangeTarget, SetForceAttack, StartSkill, StopCasting, CreateSkillController, SkillControllerState, RemoveBuff, LearnBuff, LearnSkill, ResetSkills, SwapAbility, ActiveWeaponChanged, ResurrectCharacter, Hang/Unhang (**glider**), ExpressEmotion, TurretState |
| Items/inventory | BuyItems, SellItems, DestroyItem, SwapItems, SplitBagItem, ExpandSlots, ItemSecure/Unsecure, EquipmentsSecure/Unsecure, ChangeItemLook, ConvertItemLook, ThisTimeUnpackItem, UpdateActionSlot, RepairAllEquipments/RepairSingleEquipment/RepairPetItems |
| Quests | StartQuestContext, QuestStartWith, QuestTalkMade, CompleteQuestContext, DropQuestContext, ResetQuestContext, TryQuestCompleteAsLetItDone, RestartMainQuest, AcceptCheatQuestContext |
| Housing/property | CreateHouse, BuyHouse, SellHouse(+Cancel), ChangeHouseName/Pay/Permission, ConstructHouseTax, RequestHouseTax, AllowHousingRecover, DecorateHouse, ChangeDoodadData/Phase, CreateDoodad, UnbondDoodad, CleanupLogicLink, SetLogicDoodad, SaveDoodadUccString |
| Slaves/vehicles | SpawnSlave, DespawnSlave, DestroySlave, DiscardSlave, BindSlave, ChangeSlaveEquipment/Name/Target, RepairSlaveItems |
| Mates | MountMate, UnMountMate, RemoveMate, ChangeMateEquipment/Name/Target/UserState |
| Trade/economy | StartTrade, CanStartTrade, CannotStartTrade, CancelTrade, PutupTradeItem/Money, TakedownTradeItem, TradeLock, TradeOk, DepositMoney, WithdrawMoney, SetCraftingPay |
| Specialty/packs | ListSpecialtyGoods, RequestSpecialtyCurrent, BuySpecialtyItem, SpecialtyRatio, SpecialtyRecordLoad, ListSoldItem, SellBackpackGoods |
| Auction | AuctionPost, AuctionSearch, BidAuction, CancelAuction, AuctionMyBidList, AuctionLowestPrice |
| Mail | SendMail, ReadMail, DeleteMail, ReturnMail *(known 0xfff placeholder — see MAIL-01)*, ListMail(+Continue), TakeAttachmentItem/Money, TakeAllAttachmentItem |
| Social | AddFriend/DeleteFriend, AddBlockedUser/DeleteBlockedUser, Family*(Invite/Reply/Leave/Kick/ChangeOwner/ChangeTitle), ReportSpam, CharDetail, RequestCharBrief, SetOverHeadMarker, SetPingPos |
| Teams/expeditions | InviteToTeam, ReplyToJoinTeam, LeaveTeam, KickTeamMember, MakeTeamOwner, AskRiskyTeamAction, SetTeamMemberRole, SetTeamOfficer, DismissTeam, ConvertToRaidTeam, InviteAreaToTeam, MoveTeamMember, InviteToExpedition, ReplyExpeditionInvitation, LeaveExpedition, KickFromExpedition, DismissExpedition, ChangeExpeditionMemberRole/Owner/RolePolicy/Sponsor, RenameExpedition, ChangeLootingRule |
| Chat | SendChatMessage, JoinUserChatChannel, LeaveChatChannel |
| Justice | ReportCrime, CriminalLocked, ReplyImprisonOrTrial, ReplyInviteJury, JurySummoned, RequestJuryWaitingNumber, JuryEndTestimony, SkipFinalStatement, JuryVerdict, CancelTrial, JoinTrialAudience, LeaveTrialAudience |
| Faction | FactionDeclareHostile, FactionImmigrateToOrigin, FactionImmigrationInvite(+Reply), FactionKickToOrigin |
| Portals/travel | UsePortal, UseTeleport, DeletePortal, NaviTeleport, NaviOpenPortal, NaviOpenBounty, TeleportEnded, BoardingTransfer, NotifySubZone |
| Music | RequestMusicNotes, SaveUserMusicNotes, SendUserMusic, EndMusic |
| Beauty/appearance | BeautyshopData, EnterBeautySalon, ExitBeautySalon |
| Premium/cash shop | ICSPacket family (ICSMenuList, ICSGoodsList, ICSBuyGood, ICSMoneyRequest), BuyCoinItem, PremiumServiceBuy/List/Msg, PayChargeMoney |
| Instant game | ApplyToInstantGame, JoinInstantGame, CancelInstantGame, LeaveInstantGame, EnteredInstantGameWorld, UnknownInstance, InstanceLoaded |
| Priest buff | BuyPriestBuff |
| Tax/dominion | UpdateNationalTaxRate, UpdateDominionTaxRate |
| Looting | LootOpenBag, LootCloseBag, LootItem, LootDice, RollDice |

Notable **G2C-only confirmations**: `SCGradeEnchantResult/Broadcast` (regrade),
`SCItemSocketingLunastone/LunagemResult` (socketing), `SCTowerDef*` (tower defense),
`SCInstantGame*` incl. Kill/Killstreak (arena), `SCAchievement*` ×5, `SCPremiumPoint*`,
`SCICSCashPoint`, `SCHouseTaxInfo/SCNationalTaxRate/SCDominionTaxRate/SCDominionTaxBalanced`,
`SCCharacterPortals`, `SCRankAlarm`, `SCRaceCongestion`, `SCToggleBeautyshopResponse`,
`SCItemUccDataChanged`. Notable **Stream (`CT*`) family**: emblem/UCC upload-download
protocol (`CTStartUploadEmblemStream`, `CTRequestEmblem`, `CTItemUccPacket`,
`CTUccString/Position/Complex…`).

---

## 1. Master inventory table

Legend — Ledger: exact row ID, `lane:<name>` = owned by a concurrently-landing dossier
(mark-only here), **NEW** = §3 proposal ID. Grade summaries quote SCORECARD.md as read
2026-08-25 @ develop `214bed834` (= origin/develop head, verified).

### 1a. Tracked — existing Global-ledger coverage (31 canonical systems)

| # | System | Evidence source | Ledger row | Current grade summary (cited) | Dossier status | Proposed next action |
|--:|---|---|---|---|---|---|
| 1 | Character create/login/logout/re-entry/leveling | char/session opcodes; characters+levels tables; EnterWorldManager/ExperienceManager | PROG-01 | All dims U; M2 automated t_c6eb12ec + restart t_cca63225 legs done, human deferred | none | Keep; leveling-loop workstream owns XP curve proof |
| 2 | Quests (incl. daily/weekly reset, repeatables) | quest opcode family; 85 quest tables 82% wired; QuestManager/QuestSanityVerifier | QUEST-01 | C=2 W=1 H=U A=1 R=2; 4573/4573 runnable census | runnability.md | none — deepest-covered system |
| 3 | Movement/targeting/interaction/control recovery | MoveUnit/InteractNPC/ChangeTarget; CTRL family | CTRL-01 | All U; actor-contract spike pending | navigation-domain.md (nav slice) | actor-contract spike per row |
| 4 | PvE combat/death/resurrection | StartSkill/StopCasting/ResurrectCharacter; combat+skills tables | COMBAT-01 | W=1 (SkillManager); rest U | none | combat audit |
| 5 | Abilities/class skills/progression | LearnSkill/ResetSkills/SwapAbility; skill_* tables; SkillManager | ABILITY-01 | W=1; rest U; N/A soak | none | ability audit |
| 6 | Inventory/equipment/stacking/split | item opcode family; 53 item tables 58% wired; ItemManager | ITEM-01 | W=1; conservation audit open; procs seam landed 0482ba3f0 | none | inventory conservation audit |
| 7 | Labor consume/regen/cap/persist | TimedRewardsManager (MaxLabor 2000/premium 5000 regen caps — direct code evidence); SetLpManageCharacter opcode | LABOR-01 | All U; Labor/ActAbility audit pending | none | merge audit scope w/ ACTABILITY-01 proposal |
| 8 | Mounts (+vanity pet summon via item_summon_mates) | MountMate family; mates tables (wired 0482ba3f0); MateManager | MATE-01 | W=2 A=1 H=U (mate equip legality rig) | farm-01 livestock adjacent | live equip E2E per row |
| 9 | Land claim/construct/permit/demolish | housing opcode family; housings 38% wired; HousingManager | HOUSING-01 | C=2 W=2 A=2 H=U | housing-placement.md, m3-canonical-audit.md | none |
| 10 | Crops/livestock/climates/watering/rot | doodad_func_crop_harvests/livestock_growths/climate_*; PlotManager | FARM-01 | C=2 W=2 A=2 | farm-01-livestock-interactions.md | none |
| 11 | Furniture/storage/phase persistence | PROPERTY opcodes; MySQL housings+doodads contract | PROPERTY-01 | C=2 W=2 R=2 H=U A=U | m3-canonical-audit.md | none |
| 12 | Crafting tiers/materials/workstations | craft opcode family; crafts/craft_materials/products; CraftManager | CRAFT-01 | C=2 W=2 A=2 H=U (proxy) | m4 exit records | none |
| 13 | Trade packs/specialty economy | specialty opcode family; specialty_* tables 75%; SpecialtyManager | PACK-01 | C=2 W=2 A=2 R=2 H=U | trade-pack-putdown-canonical.md, m5-core-actions-canonical.md | none |
| 14 | Vehicles (carts/trucks) lifecycle | slave opcodes; slaves tables 60%; SlaveManager | SLAVE-01 | C=2 W=2 A=2 R=2 H=U | m4 exit records | none |
| 15 | Naval: ships/shipbuilding/harpoon towing | CreateShipyard; shipyards/ship_models/item_shipyards tables 67%; ShipyardManager; ShipController/ShipHarpoonTowPhysics (graphify hubs) | SLAVE-01 + **lane:ships** | vehicles proven; naval-specific surfaces ungraded here | ships dossier landing | ships dossier owns grading |
| 16 | Direct 1:1 trade | full trade handshake opcode set; TradeManager | TRADE-01 | W=2 A=1 H=U (rig) | none | live/restart legs per row |
| 17 | NPC vendors buy/sell/refund | BuyItems/SellItems; merchants/merchant_goods 50%; MERCHANT audit open | MERCHANT-01 | All U except W=U; audit pending | **lane:economy** adjacent | economy dossier cross-link |
| 18 | Auction house | auction opcode family; auction categories 0%-wired (data) but AUCTION-01 promoted; AuctionManager | AUCTION-01 | W=A=R=2 (live E2E f3bb787ce); C=U pending market audit | **lane:economy** | economy dossier consumes AUCTION-01 |
| 19 | Currency/conservation | currency_configs, coppers/honor/crime/jury/living-point supply acts | ECON-01 | All U | **lane:economy** | economy dossier owns |
| 20 | Mail send/receive/attach/return/expire | mail opcode family; MailManager; ReturnMail 0xfff placeholder | MAIL-01 | W=1 A=1 H=U | **lane:mail** | mail dossier owns |
| 21 | Fixed-route transport (carriage/airship) | BoardingTransfer; transfers tables 100%; TransferManager | TRANSFER-01 | W=2 A=1 (live board/ride) H=U | none | recover/restart legs per row |
| 22 | Instance dungeons / mirage-island style instances | enter_instances/indun_* tables 100% wired; IndunManager/InstantGameManager | INDUN-01 | W=1; rest U | indun-domain.md | none |
| 23 | Fishing (plot-based + sports stratum) | fishing tables 100%; FishSchoolManager; plot 809 | FISH-01 | W=2 A=1 (live PASS cd5eedf11); sports-fishing orphaned | fishing-domain.md | sports-fishing stratum per dossier |
| 24 | PvP flagging/factions/honor + **piracy** | SetForceAttack; conflict_zones 75%; FactionManager; pirate faction switch | PVP-01 | All U; zone protection landed via ZONE-01 | **lane:pvp** | pvp dossier owns incl. piracy scope |
| 25 | Zone peace/conflict/war machine | ZoneManager; ConflictZones data-wired 0482ba3f0 | ZONE-01 | W=2 A=1 H=U | none | live PvP scenario per row |
| 26 | Duels | duel opcode pair; DuelManager | DUEL-01 | W=2 A=1 H=U (stuck-duel fixed f8252a37b) | none | faction-swap/bounds live geodata per row |
| 27 | Crime evidence/points | ReportCrime; crime tables; CrimeManager | CRIME-01 | W=1 | **lane:justice** (justice-domain.md) | justice dossier owns |
| 28 | Trials/jury | full jury opcode set; TrialManager | TRIAL-01 | W=1 | **lane:justice** | justice dossier owns |
| 29 | Prison | ReplyImprisonOrTrial; no PrisonManager (row documents) | PRISON-01 | W=U | **lane:justice** | justice dossier owns |
| 30 | Party/team/raid roles/loot rules | team opcode set incl. ConvertToRaidTeam, ChangeLootingRule; TeamManager | PARTY-01 | W=2 A=1 (live party spike c98da8a53) | none | raid-conversion + loot-rule legs unproven — note for party owner |
| 31 | Expeditions (guilds) | expedition opcode set; ExpeditionManager | EXPEDITION-01 | W=2 A=1 H=U (lifecycle rig) | none | persistence legs per row |
| 32 | Chat channels/moderation | chat opcodes; ChatManager; chat_spam_rules/replace_chats | CHAT-01 | W=1 | none | social audit |
| — | *Fork capability (not canonical 1.2)* | PlayerBot*/Scheduler/PopulationDirector (Bots/) | ACTOR-01, BOT-01, BOT-02 | BOT-02 soak stage 1 executed | playerbot-capability-matrix.md | excluded from canonical count |

### 1b. Lane-covered, NO global ledger row (1)

| System | Evidence source | Status |
|---|---|---|
| **Dominion sieges / castle war** (incl. siege tickets, guard towers, siege zones) | siege_* 5 tables **0% wired**; guard_tower_settings/steps; doodad_func_declare_sieges/purchase_siege_tickets/siege_periods; UpdateDominionTaxRate; SCDominion* packet family | **lane:dominion** — dossier landing; mark-only here. Note for that lane: entire siege domain is zero-data-wired today |

### 1c. NEWLY PROPOSED — uncovered player-facing systems (33)

Each gets a §3 proposal. Evidence = what my queries prove today; grades stay C=U until
each has its own exploration dossier.

| # | System | Evidence source (cited queries) | Ledger? |
|--:|---|---|---|
| 1 | Gear regrading / tier enchanting | item_grades, item_grade_buffs/_skills/_enchanting_supports/_distributions, equip_slot_enchanting_costs tables; SCGradeEnchantResult/Broadcast; GradeEnchant refs in ItemManager/IItemManager | none |
| 2 | Gem socketing (lunastone/lunagem) | item_sockets/_chances/_level_limits/_num_limits, item_enchanting_gems; SCItemSocketingLunastone/LunagemResult | none |
| 3 | Gliders (incl. costume wings-as-glider) | CSHang/UnhangPacket; flying_state_change_effects; glider traces in ItemDetailType/BackpackType/UnitRequirementsGameData; no dedicated manager | none |
| 4 | Music compose/perform | music_note_limits **0% wired**; MusicIdManager/MusicManager (SaveSong/UploadSong/CreateSheetMusic/MIDI cache — read); 4 CS + 2 SC music packets | none |
| 5 | In-game cash shop (ICS storefront) | CashShopManager (SKUs/ShopItems/MenuItems/CreditDisperseTick — read); CSICS* ×4; BuyCoinItem; SCICSCashPoint | none |
| 6 | Premium/patron service & points | premium_* 4 tables **zero-wired** (scorecard list); PremiumServiceBuy/List/Msg; PayChargeMoney; SCPremiumPointChanged/UpdatePremiumPoint; premium labor cap in TimedRewardsManager | none |
| 7 | Achievements | achievements + pre_completed_achievements **100% wired** (scorecard domain table); AchievementGameData loader; 5 SC packets | none |
| 8 | Actability / vocation proficiencies / expert limits | actability_categories/groups, deco_/loot_actability_groups; expert_limits, expand_expert_limits; Expand/Upgrade/DowngradeExpertLimit opcodes | none |
| 9 | Appellations (titles) | appellations **100% wired**; CSChangeAppellation; quest_act_supply_appellations | none |
| 10 | Reputation factions / hostility / immigration | system_factions/_relations; FactionManager; DeclareHostile + 4 immigration opcodes | none |
| 11 | Portal book / recall points / teleport network | PortalManager (read); return_points, district_return_points; UsePortal/UseTeleport/DeletePortal/NaviTeleport; SCCharacterPortals/SCPortalInfoSaved | none |
| 12 | UCC custom creativity / emblems | full CT* stream UCC protocol; ucc_applicables; imprint_/cleanup_ucc_effects; CSSaveDoodadUccString; SCItemUccDataChanged; Stream :1250 (AGENTS.md) | none |
| 13 | Buff system (as player-facing layer) | buffs domain 10 tables 70% wired, buff_effects/modifiers/triggers/tolerances/mount_skills; LearnBuff/RemoveBuff; **no ledger row** despite cross-cutting role | none |
| 14 | Loot rolls/dice/group rules | loots/loot_groups/loot_pack_dropping_npcs 100% wired; LootOpenBag/LootItem/LootDice/RollDice; ChangeLootingRule | none |
| 15 | Bank/coffer storage + money deposit | DoodadCoffer model; CSCofferInteraction/SwapCofferItems/SplitCofferItem; doodad_func_bank_uis/coffers/coffer_perms; Deposit/WithdrawMoney | none |
| 16 | Bag/inventory expansion purchases | bag_expands table; CSExpandSlotsPacket; default_inventory_tabs/tab_groups | none |
| 17 | Durability repair (gear/pets/slaves/property) | RepairAllEquipments/RepairSingleEquipment/RepairPetItems/RepairSlaveItems; PropertyRepairService + PropertyRepairScanner (managers list); repairable_slaves | none |
| 18 | Beauty salon (appearance change) | CSBeautyshopData/Enter/ExitBeautySalon; SCToggleBeautyshopResponse; character_customizing_hair_assets, custom_face_presets, hair_colors, skin_colors, face_*_maps, total_character_customs | none |
| 19 | Item look-change (transmog) + dyeing | item_look_converts family ×4 tables; CSChangeItemLook/ConvertItemLook; item_dyeings, dyeable_items, dyeing_colors | none |
| 20 | Friends/family/blocklist/player inspect | FriendMananger + FamilyManager (managers list); friend/family/block opcode sets; CSCharDetail/RequestCharBrief | none |
| 21 | Emotes / express text | express_texts **100% wired**; ExpressTextManager (managers list, incl. stray-space filename); CSExpressEmotionPacket | none |
| 22 | Arena / battlefield instant game | InstantGameManager; battle_fields 50% wired; Apply/Join/Cancel/LeaveInstantGame; SCInstantGameStart/End/Kill/Killstreak/AddPoint; upstream #1323 arena-scoring bug | none |
| 23 | Ranking boards/rewards | ranks/rank_rewards/rank_scope_links/rank_scopes **zero-wired** (scorecard list); SCRankAlarmPacket | none |
| 24 | Tower defense events | tower_def_progs/_defs/prog_kill/prog_spawn_targets **100% wired**; TowerDefGameData; SCTowerDefList/Start/WaveStart/End | none |
| 25 | Race tracks (mount/vehicle races) | race_tracks/race_track_shapes tables; **zero .cs references** (grep -rli racetrack → empty) | none |
| 26 | Readable books | books/book_pages/book_page_contents/book_elems tables present; wiring unverified this pass | none |
| 27 | Cinematics/tutorial playback | cinemas/cinema_captions/subtitles/effects (scorecard: "cinema zero-wired" watch item); Started/CompletedCinema opcodes; CSSaveTutorial | none |
| 28 | Wild-node gathering (log/ore/herb/soil) | doodad_func_ore_mines/rock_mines/fiber_collects/soil_collects/tree_byproducts_collects/seed_collects/spice_collects/medicalingredient_mines/crystal_collects; distinct from FARM-01 planted crops | none |
| 29 | Taxation cycles (house/dominion/national) | taxations **100% wired**, TaxationsManager; ConstructHouseTax/RequestHouseTax/ChangeHousePay; SCHouseTaxInfo; National/DominionTaxRate packets | none |
| 30 | Priest buff purchase | priest_buffs table; CSBuyPriestBuffPacket | none |
| 31 | Public/community farms | common_farms, farm_groups; PublicFarmManager (managers list) | none |
| 32 | UI persistence (action bars/hotkeys/layout) | CSUpdateActionSlot; hotkeys; default_action_bar_actions; Save/RequestUIData | none |
| 33 | Mould/stamp crafting (UCC furniture stamps) | mould_packs/_pack_items/moulds **zero-wired**; doodad_func_stamp_makers/mould_items/moulds | none |

### 1d. Internal / non-player-facing surfaces (cataloged, NOT proposed)

AI & spawn simulation (AIManager, AiPathsManager, AiGeodataManager, SpawnManager,
npc_ai_params/ai_commands) · engine services (TickManager, TaskManager2, EffectTaskManager,
SaveManager, ManagerOrchestrator, PhysicsManager, FormulaManager, ManaRegenManager,
AnimationManager) · account/session ops (AccountManager, AccessLevelManager, NameManager,
CharacterLifecycleService/EnterWorldManager, second-password key tables) · GM/dev tooling
(CommandManager, ConsoleCmdUsed, EditorGameMode, Debug packets) · platform (FeaturesManager,
LocalizationManager, SusManager, WebApi/Discord services, GameScheduleManager content
scheduling) · presentation data (fx_* 0%, sounds, models/model_bindings, bubbles,
demo_* preview tables, pcbang_buffs) · sub-zone discovery (SubZoneManager — ZONE-01
adjacency) · gimmicks (GimmickManager 100% — CTRL-01 adjacency) · radar/navi map plumbing
(RadarManager + Navi* opcodes — folded into PORTAL-01 proposal scope) · TimedRewardsManager
(= LABOR-01 regen evidence, not a separate system).

---

## 2. Reconciliation counts

- **Canonical player-facing systems enumerated: 65**
  - **32 tracked** (31 existing Global-ledger rows + 1 lane-owned: dominion sieges)
  - **33 newly proposed** (§3)
  - **1 lane-covered overlap double-check**: justice/pvp/mail/economy/ships lanes all map onto
    EXISTING rows (CRIME/TRIAL/PRISON, PVP, MAIL, ECON/AUCTION/MERCHANT, SLAVE-naval) — no
    additional untracked canonical system found behind those lanes.
- **Internal/non-player-facing surface groups: 9 buckets** (~40 managers/table-groups) — cataloged §1d, deliberately NOT proposed as mechanic rows.
- Ledger coverage after adoption of §3: 64/65 canonical systems would hold stable IDs (98%);
  remaining gap = none identified this pass (every canonical checklist item resolved to a row, lane, or proposal).

## 3. NEW-ROW proposals for SCORECARD.md (stable-ID convention; enter C=U pending own dossier)

| Proposed ID | Mechanic (one-line canonical description) |
|---|---|
| REGRADE-01 | Gear regrading: spend regrade charms/scrolls to raise equipment tier with success/downgrade odds (item_grades, grade buffs, enchant supports). |
| SOCKET-01 | Socketing: insert lunastones/lunagems into gear sockets with per-grade chance/level limits. |
| GLIDER-01 | Glider flight: deploy/hang/unhang gliders (incl. costume wings) for controlled aerial traversal. |
| MUSIC-01 | Music: compose MIDI songs in-game, save/share notes, perform via instruments with note limits. |
| CASHSHOP-01 | In-game cash shop (ICS): browse SKU menus, buy goods with credits, credit dispersion. |
| PREMIUM-01 | Premium/patron service: purchase premium status granting benefits (labor cap 5000, point accrual, perks). |
| ACHIEVEMENT-01 | Achievements: track completion objectives, grant rewards/items, notify client. |
| ACTABILITY-01 | Actability (vocations): proficiency gain per activity + expert-limit expansion/upgrade/downgrade purchases. |
| APPELLATION-01 | Appellations: earn titles from quests/achievements and equip one for display/stats. |
| FACTION-01 | Reputation factions: standing with system factions, declare hostility, immigrate between origins. |
| PORTAL-01 | Portal/teleport network: register portal book points, recall, use teleport reagents, nui/navi teleports. |
| UCC-01 | UCC creativity: upload custom emblems/text via stream channel, imprint onto items/doodads/sails. |
| BUFF-01 | Buff framework as player-facing layer: gains, stacking, tolerance, dispel, mount/combat buff interactions. |
| LOOT-01 | Loot: drop-pack rolls, personal/free-for-all/dice group rules, bag opening. |
| STORAGE-01 | Storage: bank/coffer access via doodads, item split/swap in coffers, money deposit/withdraw. |
| BAGEXPAND-01 | Bag expansion: purchase extra inventory/bag slots (bag_expands tiers). |
| REPAIR-01 | Repair: restore durability on worn gear, pet items, slave equipment, and placed property. |
| BEAUTY-01 | Beauty salon: paid appearance edits (face/hair/skin/custom presets) outside character creation. |
| APPEARANCE-01 | Item look-change (transmog drops) and armor dyeing with dye colors. |
| SOCIAL-01 | Social graph: friends, family (shared-cost guild-lite), blocklist, player inspect/detail. |
| EMOTE-01 | Emotes: expressive character animations with express-text templates. |
| INSTANTGAME-01 | Instanced arenas/battlefields: apply/join, scoring, killstreaks, end rewards. |
| RANKS-01 | Rankings: periodic leaderboards (combat/economy scopes) with reward mail-outs. |
| TOWERDEF-01 | Tower-defense events: wave-based base defense with progress targets per zone. |
| RACETRACK-01 | Race tracks: scheduled mount/vehicle races on shaped circuits. |
| BOOK-01 | Readable books: lore pages collected/read in-world. |
| CINEMA-01 | Cinematics/tutorials: quest-triggered camera scenes with captions/subtitles. |
| GATHER-01 | Wild-node gathering: logging/mining/herbalism/soil gathering on natural spawn nodes (labor-gated, distinct from FARM-01 planting). |
| TAX-01 | Taxation: recurring house tax bills, payment windows, national/dominion tax rates. |
| PRIESTBUFF-01 | Priest blessings: purchase temporary priest buffs at shrines/vendors. |
| PUBLICFARM-01 | Public farms: shared community farmland where anyone may plant/harvest with risk. |
| ACTIONBAR-01 | UI persistence: action-slot layouts, hotkeys, and client UI data saved server-side. |
| MOULD-01 | Mould/stamp crafting: craft stamp kits to imprint designs onto furniture/paper (UCC-adjacent). |

---

## 4. Top-5 most surprising uncovered systems

1. **Achievements** — the only canonical system whose tables are already 100% data-wired
   with a dedicated loader (`AchievementGameData`) and five G2C packets, yet it has never
   entered the ledger.
2. **Music compose/perform** — a complete manager surface (song save/upload, sheet-music
   creation, per-player MIDI cache) plus four client opcodes, while `music_note_limits`
   sits at 0% wiring and nothing tracks it.
3. **Gear regrade + gem socketing** — the defining 1.2 gear-progression sinks; both have
   canonical table families and server→client *result* packets (`SCGradeEnchantResult`,
   `SCItemSocketingLunastone/LunagemResult`) but zero ledger presence.
4. **Cash shop + premium** — `CashShopManager` is fully built out (SKU/menu/credit-disperse
   tick, enable/disable shop) and the premium labor cap is hard-coded in
   `TimedRewardsManager`, yet all four `premium_*` tables remain on the zero-wired list.
5. **Tower defense events** — silently the best-wired "missing" system: all four
   `tower_def_*` table groups at 100% wiring, `TowerDefGameData` loader, and a full
   SCTowerDef packet family, with no row anywhere.

*(Honorable mention: race tracks — zero `.cs` references at all, the purest
canonical-data-without-code gap found.)*
