# Formula Corroboration Report — 2026-08-25

WEB-RESEARCH CORROBORATION pass over the six mechanics dossiers landed today under
`scorecard-explorations/mechanics/` (justice-, economy-, pvp-, ships-domain.md; duel addendum in
pvp-domain.md; merchant/labor sections in economy-domain.md). Method: each dossier formula was
cross-checked against community/wiki knowledge (fandom wikis, ArcheRage wiki DB, Russian official
archeage.ru guides, 1.2-era patch-note mirrors, old forum/Reddit records). **Docs-only**; dossiers
untouched — this report is the input for a later correction pass.

Verdict legend: **CONFIRMED** (community matches implementation) · **CONTESTED** (community
disagrees — both numbers stated) · **UNRECORDED** (community silent or only directional).

Repo state verified before work: branch `develop` @ origin/develop head `0ed6d2257`.

---

## 1. LABOR (economy-domain.md §2)

| # | Formula (implementation) | Implementation value | Community value(s) | Verdict | Sources |
|---|---|---|---|---|---|
| L1 | Hardcoded caps `MaxLabor=2000` / `MaxLaborPremium=5000` | 2000 / 5000 | Free cap **2000**, Patron cap **5000** — "Patrons can have up to 5000 Labor Points at once. Normal users can only store up to 2000 at once." | **CONFIRMED** | https://archeage.fandom.com/wiki/Labor_Points , https://archeage-archive.fandom.com/wiki/Labor_Points |
| L2 | Online regen: 5-min tick (`TickMinutes=5`), `TickAmount` / `TickAmountPremium`, CLR default **0/0**, never scheduled → dead-by-default | tick 5 min, amount 0 | Tick interval **5 min** ✓; amounts: Free **5 per 5 min online**, Patron **10 per 5 min online** ("Free users will regenerate Labor at a rate of 5 points every 5 minutes while online"; "Patrons … 10 points every 5 minutes while online"). Machinery matches; shipped/default amounts do not — retail was never 0. | **CONTESTED** (interval confirmed; default amounts 0 ≠ 5 free / 10 patron) | https://archeage.fandom.com/wiki/Labor_Points , https://archeage-archive.fandom.com/wiki/Labor_Points |
| L3 | Offline regen: `floor(minutesSinceLogin / TickMinutes) × TickAmount`, clamped by same caps | machinery present, config-driven | Patron offline regen exists **at the same rate as online**: "Offline Regeneration: 10 Labor Points every 5 minutes"; F2P: "Offline Regeneration: None". Floor-to-tick model is consistent with the 5-min cadence; free-offline-none matches a 0 offline amount. | **CONFIRMED** (structure + F2P-none; patron value lives in config, see L2) | https://archeage-archive.fandom.com/wiki/Labor_Points |
| L4 | Labor→XP: compact `formulas` id 19 = `((pc_level * 4.5) + 37.5) / 5) * labor_power` | (4.5·L + 37.5)/5 · P = **(7.5 + 0.9·L) per labor** | Korean measured formula: XP = ⌊(225 + 27·L)/30 × P⌋ = **(7.5 + 0.9·L) per labor**, truncated; e.g. L50 → 52.5/labor (1 labor=52, 5=262, 10=525). Algebraically **identical** to the client data formula (differences: community floors per action). | **CONFIRMED** | https://www.inven.co.kr/board/archeage/2641/4728 |

## 2. JUSTICE (justice-domain.md §Behavioral contract)

| # | Formula (implementation) | Implementation value | Community value(s) | Verdict | Sources |
|---|---|---|---|---|---|
| J1 | Wanted threshold CrimePoint ≥ 50 → buff 3710 | 50 | "If a player has 50 or more Crime Points they will be sent to trial upon their next PvP death"; RU official: «Когда вы накопите 50 очков преступлений, у вас появится … „Особо опасный преступник"» | **CONFIRMED** | https://archeage.fandom.com/wiki/Justice_System , https://archeage.ru/game/trial |
| J2 | Pirate conversion InfamyPoint ≥ 3000 → Wanted + Contemptuous + SetFaction(Pirate) | 3000 | "After a character receives 3000 infamy points, they are declared a pirate"; RU: «больше 3000 очков преступной славы → эффект … не рассеивается». (Later versions replaced auto-pirate with exile quest — version caveat, irrelevant to 1.2.) | **CONFIRMED** | https://archeage-archive.fandom.com/wiki/Crime_and_Punishment , https://archeage.ru/game/trial |
| J3a | Sentence base score: murder evidence = 20 | 20 min | RU official 1.2-era rules: «Убийство игрока своей фракции — **20** очков преступлений» (murder = 20). English archive wiki lists murder bloodstain = 10 CP (NA-tuned values); the 20 matches the KR/RU base the fork's data derives from. | **CONFIRMED** (vs RU/KR base; NA archive shows 10 — regional variant, note in dossier) | https://archeage.ru/game/trial , https://archeage-archive.fandom.com/wiki/Crime_and_Punishment |
| J3b | Sentence base score: theft evidence = 8 | 8 min | RU official: theft = **5** очков; EN archive wiki: footprints reportable for **3** CP each. No source found where theft = 8. | **CONTESTED** (impl 8 vs RU 5 / EN 3) | https://archeage.ru/game/trial , https://archeage-archive.fandom.com/wiki/Crime_and_Punishment |
| J3c | Sentence base score: assault evidence = 0 | 0 min | Assault was a *chargeable* crime everywhere: RU official: «Нападение на игроков собственной фракции — **5** очков»; EN archive: small bloodstain = **1** CP. Zero may be right for *jail-minute weight* (assault alone never jailed) but no community source states assault contributes 0 minutes. | **CONTESTED** (impl 0 as sentence weight vs assault being a scored crime 5 RU / 1 EN; plausible-but-unsubstantiated as a minute-weight) | https://archeage.ru/game/trial , https://archeage-archive.fandom.com/wiki/Crime_and_Punishment |
| J4 | Infamy multiplier `(1 + Infamy/1000)` | +100% per 1000 infamy | Direction confirmed, coefficient unrecorded: RU official: «этот показатель влияет на срок заключения в тюрьме» (infamy affects prison term). Exact /1000 divisor nowhere published. | **CONFIRMED** (directional; coefficient UNRECORDED) | https://archeage.ru/game/trial |
| J5 | Verdict tiers ×0.2 / ×0.5 / ×0.8 / ×1.0 / ×1.2 by vote count | 5 tiers | Mechanism confirmed: "each juror votes innocent or selects a prison sentence … The amount of votes determines the amount of time"; 0 guilty votes = innocent. The specific 0.2–1.2 multipliers are not published anywhere found. | **UNRECORDED** (mechanism confirmed, multipliers unrecorded) | https://archeage.fandom.com/wiki/Justice_System , https://forums.craftingworlds.com/threads/archeage-criminal-system-crime-court-prison-pirate.2284/ |
| J6 | Pirates sentenced flat 40 minutes | 40 | "pirates … automatically sent to trial and generally received **at least 40 minutes** of jail time, even with no crime points" (killed on land; sea deaths avoided it). | **CONFIRMED** | https://vaeloc.wixsite.com/archeaegis/piracy |
| J7 | Jury size 5 | 5 | "A jury consisting of 5 other players"; RU: «Присяжными становятся случайные 5 игроков вашей фракции». | **CONFIRMED** | https://archeage-archive.fandom.com/wiki/Crime_and_Punishment , https://archeage.ru/game/trial |
| J8 | Courtroom layout: 5 jury seats + 8/8/9/9 spectator seats per courtroom, judge NPCs | 4 courtrooms as spawned | Courthouses confirmed at Marianople (Nuia) and Austera/Solis Headlands (Haranya) — matches the fork's two real courtrooms + holding sites; seat *counts* unrecorded. | **UNRECORDED** (locations confirmed; seat counts unrecorded) | https://archeage.fandom.com/wiki/Justice_System |
| J9 | Evidence score ×10 when victim level < 30 | ×10 | No community/source trace of any victim-level multiplier on sentencing. | **UNRECORDED** | (none found) |
| J10 | Prison-labor sentence reduction (absent in fork) | not implemented | Retail had labor-based early release: kill rats (50 labor) −3 min, trash piles (150) −15 min, dirt piles (500) −60 min; also escape tunnels (72-h wanted debuff). Dossier already grades this out-of-scope — community data confirms it was real 1.2-era content. | **CONFIRMED-missing** (corroborates dossier's PRISON-01 gap) | https://archeage.ru/game/trial , https://archeage.fandom.com/wiki/Justice_System |

## 3. PVP HONOR (pvp-domain.md §3)

| # | Formula (implementation) | Implementation value | Community value(s) | Verdict | Sources |
|---|---|---|---|---|---|
| P1 | Conflict zone: solo 10 (killer 6 + 4/assist) | 10 solo | Official RU 2.9 notes: kills during **Conflict award 0 honor**; honor flow described as War-only across 2014–2019 community sources ("Honor awarded for faction kills only once the zone reaches War"). No source found awarding conflict-zone kill honor at all, let alone 10. | **CONTESTED** (impl 10 in Conflict vs community Conflict = 0) | https://archeage.ru/updates/28042016/ , https://foros.3dgames.com.ar/threads/852717-archeage-eng-%28us-eu%29-cbt-4-el-22-de-agosto/page28 |
| P2 | War zone: solo 20 (killer 16 + 4/assist) | 20 solo | RU official 2.9: **40** очков чести за убийство на войне (base); later community memory: "up to 40", reduced (~10) for low-value/leech targets. The 1.2 patch notes themselves specify no open-world kill-honor number (they add kill **XP** only). 20 is not attested anywhere. | **CONTESTED** (impl 20 vs official-later 40 base; assist split 16+4 unattested) | https://archeage.ru/updates/28042016/ , https://www.warlegend.net/archeage-patch-notes-1-2/ , https://www.reddit.com/r/archeage/comments/3nwl0x/giant_list_of_every_way_to_make_honor/ |
| P3 | Victim penalty: −10 honor (clamp ≥0) in War | −10 | RU official 2.9: «за смерть во время войны теряется **10 очков чести**». Matches exactly. | **CONFIRMED** | https://archeage.ru/updates/28042016/ |
| P4 | Assist window 30 s (damage/heal/CC capture) | 30 s | Assists exist (1.2 notes give assist shares for kill XP: most damage 30%, most heal 20%, second heal 10%); window length unrecorded. | **UNRECORDED** (assist concept confirmed; 30 s window unrecorded) | https://www.warlegend.net/archeage-patch-notes-1-2/ |
| P5 | No diminishing returns / per-victim cooldown | absent | Retail had **Leech** mode: recently-killed players yield no/reduced rewards — "No experience will be awarded if the target is in Leech mode (recently killed)" (1.2 notes); later versions scale honor down for leech/repeat kills. The fork spawns the `DiedInPvpWarZone` leech marker but awards full honor regardless. | **CONTESTED** (retail demonstrably throttled repeat kills; impl has none) | https://www.warlegend.net/archeage-patch-notes-1-2/ , https://archeage.ru/updates/28042016/ |

## 4. SHIPS (ships-domain.md §2, §5)

| # | Formula (implementation) | Implementation value | Community value(s) | Verdict | Sources |
|---|---|---|---|---|---|
| S1 | Build materials consumed AT PLACEMENT (`RemoveRequiredItems`: design + taxes + all skill reagents up front, e.g. adventure clipper 14879 → lumber×10 + iron×10); plank-pack steps only tick counters | all-at-placement | Retail two-phase: **dock placement** consumed design + 10 lumber + 10 iron + 10 gold (matches the ×10 reagents!), then ship construction consumed **carried trade packs applied to the dock in order — 1× Lumber Pack, 1× Iron Pack, 1× Fabric Pack** (25 labor each application). Fork deviates: pack stage consumes nothing. | **CONTESTED** (placement leg matches; construction-pack consumption missing — dossier's "DEVIATION" flag is correct) | https://archeage-archive.fandom.com/wiki/Harpoon_Clipper , https://wiki.archerage.to/na-en/db/items/17861 |
| S2 | Ezna clipper (slave 21/model 393) binds **8× cannon** slave 10; harpoon clipper 14 binds single harpoon turret slave 48 | 8 / 1 | Eznan Cutter: "comes equipped with 1 sextant, **8 cannons**, 6 oxygen cylinders, and 4 trade pack containers" (4 per side per community summaries). Harpoon Clipper: front harpoon turret + one side cannon + portable harpoon — single turret matches. | **CONFIRMED** | https://archeage-archive.fandom.com/wiki/Eznan_Cutter , https://archeage-archive.fandom.com/wiki/Harpoon_Clipper |
| S3 | Harpoon tow skills Launch 13749 / Cut 13750 | 13749 / 13750 | ArcheRage skill DB: ID **13749 "Launch Harpoon"** ("Fires a harpoon… Pressing W or S after the harpoon lands lengthens or shortens the rope"); companion **13750 "Cut Harpoon Rope"** listed in the same Basic group. | **CONFIRMED** | https://wiki.archerage.to/na-en/db/skills/13749 , https://wiki.archerage.to/na-en/db/skills/group/10000 |

## 5. DUELS (pvp-domain.md §Addendum b)

| # | Formula (implementation) | Implementation value | Community value(s) | Verdict | Sources |
|---|---|---|---|---|---|
| D1 | Duel bound to spawned "Combat Flag" doodad 5014 at midpoint of the two duelists | flag doodad | "A duel flag appears between the participants after the request is accepted" — flag-between-participants corroborated; rendering radius server-side unrecorded (fork also sends none). | **CONFIRMED** (flag concept; midpoint anchor implied) | https://na.archerage.to/forums/threads/archerage-7-5-global-update-akasch-invasion-patch-notes.11219/ |
| D2 | Surrender at ≥ 75 m from flag (`DistanceForSurrender`) | 75 m | "Leaving the duel area causes the participant to be kicked/excluded" — forfeit-on-leave corroborated; exact meter radius never published (later patches only say "+30%" / "50 m→100 m request range"). | **UNRECORDED** (mechanism confirmed, 75 m value unrecorded) | https://na.archerage.to/forums/threads/archerage-7-5-global-update-akasch-invasion-patch-notes.11219/ , https://archeageclassic.com/news/patch-notes---mar-5%2C-2026.214601963 |
| D3 | Flat 5-minute timer → draw | 5 min | "the duel ends in a draw if combat lasts too long, but [notes] do not publish the exact duration." | **UNRECORDED** (draw rule confirmed, 5 min unrecorded) | https://na.archerage.to/forums/threads/archerage-7-5-global-update-akasch-invasion-patch-notes.11219/ |

## 6. MERCHANT (economy-domain.md §1)

| # | Formula (implementation) | Implementation value | Community value(s) | Verdict | Sources |
|---|---|---|---|---|---|
| M1 | Buy price = flat `items.price` × count; no faction modifier, no bulk discount | flat | General merchants are fixed-list shops; no dynamic/discount pricing recorded anywhere for standard vendors (special/event shops excepted). | **CONFIRMED** | https://archeage.fandom.com/wiki/General_Merchant |
| M2 | No stock depletion / restock timers | infinite | "Buying an item does not deplete a shared inventory … There is generally no per-item daily or account purchase limit" for general merchants. Dead `merchant_price_ratios` table + static `AddItemToStock` match retail behavior. | **CONFIRMED** | https://archeage.fandom.com/wiki/General_Merchant |
| M3 | Sell-back = `(int)(template.Refund × grade.RefundMultiplier / 100) × count` | grade-scaled fraction of `refund` column | Vendor buy-back existed (shop UI has a buyback tab; players routinely sold items back), but the exact refund fraction and its grade scaling were never documented in any source found. | **UNRECORDED** (feature confirmed, formula unrecorded) | https://na.archerage.to/forums/threads/sold-wings-to-general-merchant-by-mistake.12145/ |
| M4 | Buyback re-purchase pays exactly the refund credited at sell | exact roundtrip | Buyback window existed in retail; whether repurchase price equals the credited refund is unrecorded. | **UNRECORDED** | (none found beyond buyback-tab existence) |

---

## Summary

**31 checks: 16 CONFIRMED · 7 CONTESTED · 8 UNRECORDED** (J3 split into three sub-rows; some
rows carry split sub-verdicts, counted once by primary verdict).

### CONTESTED — deserves dossier correction or an owner decision

1. **L2 — Labor regen defaults are non-retail (economy-domain.md).** Machinery (5-min tick, premium/non-premium split, offline path, caps) matches retail exactly; but shipped defaults `TickAmount=0/TickAmountPremium=0` mean zero regen, while retail was 5/5-min free-online, 10/5-min patron-online, 10/5-min patron-offline. If LAB-A schedules the tick, the config values should be seeded to those numbers (owner decision: exact offline rate shows minor regional variance in secondary sources).
2. **J3b/J3c — Theft=8 / assault=0 sentence weights (justice-domain.md).** RU official 1.2-era rules score theft 5 and assault 5; the EN archive wiki scores them 3 and 1 (footprint/bloodstain per-report values). Murder=20 is fine. Theft=8 matches no source; assault=0 is defensible only as a jail-minute weight (assault alone never sent anyone to trial) but is unsubstantiated — recommend annotating provenance or re-deriving from `report_crime_effects` data rather than asserting parity.
3. **P1 — Conflict-zone kill honor 10 (pvp-domain.md).** Every community/official record gives **0** honor for conflict-state kills; honor flow was War-gated. The fork paying 10 in Conflict looks like a reconstruction artifact. Candidate correction: gate honor to War only, or keep as deliberate fork policy with a note.
4. **P2 — War-zone kill honor 20 (pvp-domain.md).** Later official value is **40 base per war kill**; community remembers "up to 40, less for leech targets". 20 solo / 16+4 assist is unattested. Note the 1.2 patch notes themselves publish no world-PvP kill-honor number (only arena: Drill Camp win 50/loss 10/kill 3/assist 2; Gladiator win 20/loss 5/kill 30), so 20 remains pure reconstruction. Owner decision: keep fork values or align to 40.
5. **P5 — No diminishing returns contradicts retail Leech (pvp-domain.md).** 1.2 explicitly excluded Leech-mode (recently killed) targets from kill rewards; the fork already carries the leech marker but pays full honor. At minimum a per-victim reward cooldown should be noted as a known deviation in the dossier.
6. **S1 — Ship construction material timing (ships-domain.md).** Placement-leg quantities match retail (design + 10 lumber + 10 iron), but retail then required carrying and donating 1 Lumber Pack + 1 Iron Pack + 1 Fabric Pack (order: lumber→iron→fabric, 25 labor per application) while the fork's pack steps consume nothing. The dossier's DEVIATION flag stands; correction pass should cite the two-phase retail flow explicitly.

### Notable corroborations worth recording in dossiers (currently uncited)

- Labor→XP client formula id 19 is algebraically identical to the community-measured `(225+27·level)/30` XP-per-labor law (inven.co.kr Korean testing thread).
- Pirates-flat-40-minute sentence has direct community attestation (archeaegis piracy guide).
- Eznan-class 8 cannons and harpoon-skill IDs 13749/13750 verified against ArcheRage itemDB/skillDB.
- RU official justice page corroborates 50-CP wanted threshold, 3000-infamy pirate line, 5-player faction juries, infamy-lengthening of sentences, AND documents prison-labor sentence reduction + escape content that the dossier correctly marks absent (PRISON-01 scope decision input).

### Source quality caveats

- Honor kill values: strongest numeric source (war=40, death=−10, conflict=0) is the RU official **2.9** (2016) update page — post-1.2 but pre-Kakao; 1.2-era Western notes publish no world kill-honor number. Treat P1/P2 corrections as "align to best-attested", not "proven 1.2".
- Labor regen: fandom pages describe NA-launch (1.x) rules; KR/RU rates varied slightly by region/era (one RU news page cites patron offline 5/5-min). Caps 2000/5000 are unanimous.
- Duel bounds and assist-window lengths were never officially quantified in any era — UNRECORDED verdicts there are final unless a client capture lands.
