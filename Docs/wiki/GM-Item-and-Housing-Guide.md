# GM Item & Housing Guide (ArcheAge 1.2, AAEmu)

Player-facing cheat sheet. All IDs verified against `compact.sqlite3` r208022.
GM commands use the `/` prefix and need GM access.

## Spawning items

```
/item add <self|target|playerName> <templateId> [amount 1-1000] [grade 0-8]
```

> ID lookup trap: in `localized_texts` the item's numeric ID is the **`idx`**
> column, NOT `id`. (`id` is the text key — those numbers don't exist as
> items and the server will reject them.)

## Trade-pack materials

| What | ID | Command |
|---|---|---|
| Stone Brick | 8343 | `/item add self 8343 100` |
| Lumber | 8337 | `/item add self 8337 100` |
| Log (raw) | 8017 | `/item add self 8017 100` |
| Iron Ore (raw) | 8022 | `/item add self 8022 100` |
| Fabric | 8256 | `/item add self 8256 100` |
| Stone Pack (finished) | 17684 | `/item add self 17684 5` |
| Lumber Pack (finished) | 17683 | `/item add self 17683 5` |

There is no raw "Stone" item — stone comes as **Stone Brick** (mining),
the same way logs become lumber. Packs aren't in the standard
`crafts`/`craft_products` tables (1.2 specialty stations); if a workbench
won't take your mats, spawn the finished packs directly.

## Plotting a house

1. **Deed** (no NPC sells these — pack 139 is orphaned; GM-grant or
   auction house):
   - `21166` — Cozy Nuian House (small, 10g base tax, recommended)
   - `21191` — Harani equivalent
   - `13712` — barracks/tent, cheapest, no tax (throwaway test plot)
2. **Tax certs** `31891` (bound `31892` eaten first) for the placement
   deposit, plus **labor** (`/labor self 30000`, repeat as needed).
3. **Land** — any housing zone (Marianople, Golden Plains…), open ground
   with neighbor spacing.
4. Stand on the plot → use the deed → pay deposit → frame appears →
   target it → `/build` → `/house settaxpaid` (fresh 21-day clock).

Useful: `/house info` (tax state), `/house forcedemo` (instant demolish),
`/house setforsale <money> [buyer]`.

## Furnishing

No merchant sells furniture — craft it (256 recipes) or grant it:

| What | ID | Command |
|---|---|---|
| Wooden armchair | 8219 | `/item add self 8219 1` |
| Simple wooden chair | 8211 | `/item add self 8211 1` |
| Square paper lamp frame | 25689 | `/item add self 25689 1` |

Place via the client's house-decoration mode inside your finished house.
(`/doodad spawn` drops world doodads, not house-parented décor.)

## Known gaps (emulator, 1.2 DB)

- Deeds and furniture have no merchant source (orphan pack 139, empty
  decor/merchant join) — grant, craft, or auction only.
- No command skips the placement deposit — place normally, then
  `settaxpaid`.
- `Build()` ignores client money/cert counts (TODOs in
  `HousingManager.cs`); gold-tax branch untested.
