using System.Collections.Concurrent;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// One quest's static fixture facts, DERIVED from live game data — never
/// hand-typed. The row is the single place a quest-owned production path reads
/// its act ids, item ids, prey template, loot pack, reporter template and
/// reward item from, so a data change (or a different quest) cannot silently
/// diverge from the DB.
///
/// Derivation (every field read off the loaded QuestManager / ItemManager /
/// LootGameData surfaces; any unresolvable field stays zero and the caller's
/// fail-closed predicates name the failure):
///   ObjectivePattern / ObjectiveActType — the quest's objective SHAPE, read
///     off the Progress component's first objective act (CountsAsAnObjective,
///     in (component id, act id) order): an item use derives
///     <see cref="QuestPattern.UseItem"/>; an item gather derives
///     <see cref="QuestPattern.KillX"/> when its item drops from an npc loot
///     pack, else <see cref="QuestPattern.GatherDoodad"/> when its item comes
///     from a doodad loot func, else <see cref="QuestPattern.Deliver"/> when
///     its item rides the quest's own Supply component (the step machine
///     grants it on accept — the basil-delivery path, no loot chain at all);
///     a doodad interaction derives
///     <see cref="QuestPattern.InteractDoodad"/> (no loot-chain condition —
///     the credit rides the skill pipeline's interaction effect, not an item
///     grant). A quest with NO Progress objective act derives
///     the pure-bootstrap shape (advance + turn-in only). A Progress objective
///     act the vocabulary does not serve yet derives
///     <see cref="QuestPattern.Unknown"/> WITH its act type name, which is the
///     caller's fail-closed trigger. The row is derived for EVERY quest; the
///   GatherActId / PreyItem / Need — the Progress component's first
///     QuestActObjItemGather (act id, item id, count), in (component id, act
///     id) order.
///   GatherNpcSources — every loaded NPC→pack pair whose pack grants PreyItem,
///     ordered default-pack first, then NPC id and pack id. PreyTemplate /
///     PreyPack retain the first pair as the legacy representative.
///   UseItemActId / UseItemTemplateId / UseItemNeed — the Progress component's
///     first QuestActObjItemUse (act id, item template, count), the item-use
///     objective's own facts (distinct from the gather act's).
///   InteractActId / InteractDoodadTemplate / InteractNeed / InteractUseSkill —
///     the Progress component's first QuestActObjInteraction (act id, doodad
///     template, count), plus the interaction skill derived from that doodad
///     template's OWN func tables.
///   AutoComplete — true when the quest carries a QuestActConAutoComplete in
///     its Ready or Reward component: the engine completes it on its own
///     evaluation, so the plan needs no NPC turn-in leg to finish it.
///   ReporterTemplate — the Ready component's QuestActConReportNpc npc,
///     falling back to the Start component's QuestActConAcceptNpc.
///   RewardItem — the Reward component's QuestActSupplyItem item, falling back
///     to the Supply component.
///
/// Memoized per quest id (<see cref="FromQuestData"/>): the derivation's loot
/// inversion walks the whole <c>loot_pack_dropping_npcs</c> table (~15k rows)
/// and probes each row's pack, so a per-wake re-derivation is the wake path's
/// dominant cost once it is called per active quest per wake. A miss derives
/// and stores; a hit returns the stored row. Reads stay pure — the memo is a
/// cache of a pure read, never a world mutation. The doodad-source scan reads
/// the loaded <c>DoodadManager</c> func/template surfaces (func groups, funcs,
/// loot-item templates), which are rebuilt wholesale by its <c>Load</c>.
///
/// Invalidation is EXPLICIT, never time- or hit-based, so a row can never
/// outlive the data it was derived from: <see cref="Invalidate"/> /
/// <see cref="InvalidateAll"/> are called from the reload seams that rebuild
/// the four surfaces the row reads — <c>QuestManager.Load</c> (templates),
/// <c>ItemManager.Load</c> (npc→loot-pack rows), <c>LootGameData.Load</c>
/// (loot packs) and <c>DoodadManager.Load</c> (doodad func/template surfaces).
/// Each bumps a generation before the next derivation is allowed
/// to publish, so a row derived across a reload boundary is never stored.
/// Any rig or caller that stages these surfaces outside those loaders must
/// invalidate too, or it will read a row derived from the previous staging.
public sealed record QuestFixtureRow(
    uint QuestId,
    uint GatherActId,
    uint PreyItem,
    int Need,
    uint PreyTemplate,
    uint PreyPack,
    uint ReporterTemplate,
    uint RewardItem)
{
    /// <summary>One native NPC template and its pack that grants the gather item.</summary>
    public readonly record struct NpcDropSource(uint NpcTemplateId, uint LootPackId);

    // Derived facts travel with the existing memoized row. A record `with`
    // copies these private fields, so the identity below prevents a changed
    // scalar item/template/pack from reusing a stale multi-source set.
    private IReadOnlyList<NpcDropSource> DerivedNpcSources { get; init; } = Array.Empty<NpcDropSource>();
    private uint DerivedNpcItem { get; init; }
    private uint DerivedNpcTemplate { get; init; }
    private uint DerivedNpcPack { get; init; }

    /// <summary>
    /// Every loaded NPC→pack link that grants this one gather item, in the
    /// representative's existing default-pack/NPC/pack order. A hand-built
    /// row or a `with` override uses its scalar pair alone. Zero fields mean
    /// no NPC source; callers still validate live source and loot permission.
    /// </summary>
    public IReadOnlyList<NpcDropSource> GatherNpcSources
    {
        get
        {
            if (PreyItem == 0 || PreyTemplate == 0 || PreyPack == 0)
                return Array.Empty<NpcDropSource>();
            // A copied derived row naming another item has no proven NPC link.
            if (DerivedNpcSources.Count != 0 && PreyItem != DerivedNpcItem)
                return Array.Empty<NpcDropSource>();
            if (PreyItem == DerivedNpcItem && PreyTemplate == DerivedNpcTemplate
                && PreyPack == DerivedNpcPack && DerivedNpcSources.Count != 0)
                return DerivedNpcSources;
            return Array.AsReadOnly(new[] { new NpcDropSource(PreyTemplate, PreyPack) });
        }
    }

    /// <summary>
    /// The selected objective shape, or the first Progress objective for a
    /// whole-template row (see the type doc). <see cref="QuestPattern.Unknown"/> with
    /// an empty <see cref="ObjectiveActType"/> is the pure-bootstrap shape (no
    /// Progress objective act at all); <see cref="QuestPattern.Unknown"/> WITH an
    /// act type is an objective this vocabulary cannot serve yet — the caller's
    /// fail-closed trigger.
    /// </summary>
    public QuestPattern ObjectivePattern { get; init; } = QuestPattern.Unknown;

    /// <summary>
    /// The Progress objective act's CLR type name (e.g.
    /// <c>QuestActObjItemGather</c>), or "" when the quest carries no objective
    /// act. Named so an unserved objective's refusal says WHICH act it could
    /// not serve.
    /// </summary>
    public string ObjectiveActType { get; init; } = "";

    /// <summary>The Progress component's first QuestActObjItemUse act id (0 when the quest carries none).</summary>
    public uint UseItemActId { get; init; }

    /// <summary>The item template that act consumes (0 when the quest carries no item-use objective).</summary>
    public uint UseItemTemplateId { get; init; }

    /// <summary>The item-use objective's required count (0 when the quest carries no item-use objective).</summary>
    public int UseItemNeed { get; init; }
    /// <summary>
    /// The Progress component's first QuestActObjInteraction act id (0 when the
    /// quest carries none).
    /// </summary>
    public uint InteractActId { get; init; }

    /// <summary>
    /// The doodad template the interaction objective works (0 when the quest
    /// carries no interaction objective). Read off the act's own
    /// <c>DoodadId</c> — never hand-typed, never a quest id.
    /// </summary>
    public uint InteractDoodadTemplate { get; init; }

    /// <summary>
    /// The interaction objective's required count (0 when the quest carries no
    /// interaction objective).
    /// </summary>
    public int InteractNeed { get; init; }

    /// <summary>
    /// The interaction skill the interact leg enters through, derived from the
    /// interaction target's OWN func tables — never hand-typed. Same precedence
    /// as <see cref="GatherUseSkill"/> (and
    /// <c>GameplayActor.ResolveInteractionSkill</c>): within the template's func
    /// groups in ascending group-id order, the first explicit
    /// <c>func.SkillId</c> binding, else the first <c>DoodadFuncUse</c> /
    /// <c>DoodadFuncFakeUse</c> template skill id. 0 = plain skill-less use. The
    /// interact leg rides this id; the actor gates on template existence only
    /// (no learned-skill requirement). A 0 here means the target's func tables
    /// name no skill — the leg fails closed (a skill-less Use never emits the
    /// quest interaction event, so dispatching it would be a silent no-op the
    /// credit check could never observe).
    /// </summary>
    public uint InteractUseSkill { get; init; }

    /// <summary>
    /// True when the quest carries a <c>QuestActConAutoComplete</c> in its Ready
    /// or Reward component: the engine completes the quest on its own step
    /// evaluation, so the plan needs no NPC turn-in leg to finish it.
    /// </summary>
    public bool AutoComplete { get; init; }
    /// <summary>
    /// The doodad template id whose loot funcs grant the gather item (0 when the
    /// quest carries no gather-from-doodad source). The lowest id of
    /// <see cref="GatherDoodadTemplates"/> — kept so plan gating, diags and
    /// hand-built rows keep a deterministic single id; the legs and the travel
    /// resolver read the full set. Never hand-typed, never a quest id.
    /// </summary>
    public uint GatherDoodadTemplate { get; init; }

    /// <summary>
    /// EVERY doodad template id whose loot funcs grant the gather item
    /// (<see cref="PreyItem"/>), ascending — derived by inverting the loaded
    /// doodad func surfaces, never hand-typed. Empty on a hand-built row
    /// (single-id rows predate the set — the legs fall back to the single id
    /// there) and when no doodad grants the item — the director's
    /// unresolvable-source signal for the gather-from-doodad shape
    /// (fail-closed). The quest's <c>quest_act_obj_alias_id</c> (4415's 1784
    /// "any well") accepts any of these sources, so the legs resolve the
    /// NEAREST perceived member and the travel resolver the nearest spawner
    /// across the whole set.
    /// </summary>
    public IReadOnlyList<uint> GatherDoodadTemplates { get; init; } = Array.Empty<uint>();

    /// <summary>
    /// The interaction skill the gather draw enters through, derived from the
    /// gather source's OWN func tables — never hand-typed. Mirrors the
    /// precedence <c>GameplayActor.ResolveInteractionSkill</c> applies to the
    /// client's skill-targeted use: an explicit <c>func.SkillId</c> binding
    /// first, then <c>DoodadFuncUse</c> / <c>DoodadFuncFakeUse</c> template
    /// skill ids, scanning the template's func groups in ascending group-id
    /// order. 0 = plain skill-less draw (loot / phase funcs). The gather leg
    /// rides this id on its Interact proposal; the actor gates on template
    /// existence only (no learned-skill requirement).
    /// </summary>
    public uint GatherUseSkill { get; init; }
    /// <summary>
    /// The Supply component's first QuestActSupplyItem act id (0 when the quest
    /// carries none). The supplied-gather shape's own source fact: the
    /// step machine grants this item on accept, so the objective credits off
    /// the legitimate supply receipt — no kill, no draw, no injection.
    /// </summary>
    public uint SupplyActId { get; init; }

    /// <summary>
    /// The item template the Supply act grants (0 when the quest carries no
    /// Supply act). The deliver shape matches it against
    /// <see cref="PreyItem"/>: only a gather for the SUPPLIED item classifies
    /// as supplied-gather.
    /// </summary>
    public uint SupplyItem { get; init; }

    /// <summary>
    /// The Supply act's granted count (0 when the quest carries no Supply act).
    /// </summary>
    public int SupplyCount { get; init; }

    /// <summary>
    /// The kill-family objective's act id (0 when the quest carries no kill
    /// objective). The credit check resolves the act by this id off the static
    /// template tables, exactly as the gather/interact/item-use credit helpers
    /// do for their own shapes — the act's live counter, never a bag count (a
    /// kill objective has no item).
    /// </summary>
    public uint HuntActId { get; init; }

    /// <summary>
    /// The kill-family objective's required count (0 when the quest carries no
    /// kill objective). Read off the act's own <c>Count</c>.
    /// </summary>
    public int HuntNeed { get; init; }

    /// <summary>
    /// The npc template a single-template hunt objective names (0 when the act
    /// names a group instead, or names nothing at all). Read off the act's own
    /// npc id — never hand-typed, never a quest id. This is the hunt leg's SOLE
    /// accept criterion: the fight is named, and the engine credits it by
    /// template through <c>OnMonsterHunt</c>.
    /// </summary>
    public uint HuntNpcTemplate { get; init; }

    /// <summary>
    /// The npc group a group-hunt objective names (0 for a single-template
    /// hunt, or for an objective that names no group at all). Read off the
    /// act's own group id; membership is tested live through the engine's own
    /// <c>QuestManager.CheckGroupNpc</c>, so this needs no membership dump.
    /// </summary>
    public uint HuntNpcGroup { get; init; }

    /// <summary>
    /// The talk-family objective's act id (0 when the quest carries no talk
    /// objective). The credit check resolves the act by this id off the static
    /// template tables, exactly as the gather/interact/item-use credit helpers
    /// do for their own shapes.
    /// </summary>
    public uint TalkActId { get; init; }

    /// <summary>
    /// The npc template a single-npc talk objective names (0 when the act names
    /// a group instead, or names none). Read off the act's own npc id — never
    /// hand-typed, never a quest id. This is the talk leg's SOLE accept
    /// criterion: the conversation is named, and the engine credits it by
    /// template through <c>OnTalkMade</c>.
    /// </summary>
    public uint TalkNpcTemplate { get; init; }

    /// <summary>
    /// The npc group a group-talk objective names (0 for a single-npc talk, or
    /// for an objective that names no group). Read off the act's own group id;
    /// membership is tested live through the engine's own
    /// <c>QuestManager.CheckGroupNpc</c>.
    /// </summary>
    public uint TalkNpcGroup { get; init; }

    /// <summary>
    /// Memo of derived rows, per quest and selected objective. Concurrent: the wake path can
    /// derive different quests from different bot threads.
    /// </summary>
    private static readonly ConcurrentDictionary<(uint QuestId, uint ActId), MemoEntry> RowCache = new();

    /// <summary>
    /// Bumped by every invalidation. A derivation that started before a reload
    /// compares generations before publishing, so a row derived against a
    /// half-replaced surface is served to its caller but never memoized.
    /// </summary>
    private static long s_dataGeneration;

    /// <summary>
    /// The four surfaces the derivation reads, WITHOUT constructing any of
    /// them. Captured per derivation and re-checked per lookup.
    /// </summary>
    private readonly record struct DataSources(
        QuestManager? Quest,
        ItemManager? Items,
        LootGameData? Loot,
        DoodadManager? Doodads)
    {
        public static DataSources Snapshot() => new(
            Singleton<QuestManager>.PeekInstance,
            Singleton<ItemManager>.PeekInstance,
            Singleton<LootGameData>.PeekInstance,
            Singleton<DoodadManager>.PeekInstance);

        /// <summary>True when the live surfaces still are the captured ones.</summary>
        public bool Unchanged => ReferenceEquals(Quest, Singleton<QuestManager>.PeekInstance)
                                 && ReferenceEquals(Items, Singleton<ItemManager>.PeekInstance)
                                 && ReferenceEquals(Loot, Singleton<LootGameData>.PeekInstance)
                                 && ReferenceEquals(Doodads, Singleton<DoodadManager>.PeekInstance);
    }

    /// <summary>
    /// A memoized row plus the surfaces it was derived from. A reload rebuilds
    /// those surfaces IN PLACE (same references), so the explicit invalidation
    /// seam is what catches it; a wholesale replacement (a rig swapping a
    /// manager singleton) changes the references, which this entry detects on
    /// lookup — a replaced surface can never be shadowed by a row derived from
    /// its predecessor.
    /// </summary>
    private readonly record struct MemoEntry(QuestFixtureRow Row, DataSources Sources);

    /// <summary>
    /// The derived row for <paramref name="questId"/>: the memoized row on a
    /// hit, else a fresh derivation that is stored. Never throws: an unreadable
    /// surface yields the zero row (or a partly-populated one when only the
    /// loot source is unresolvable).
    /// </summary>
    public static QuestFixtureRow FromQuestData(uint questId) => FromQuestData(questId, null);

    /// <summary>Derives the existing action facts for one objective, without
    /// letting an earlier, already-credited objective choose its shape.</summary>
    public static QuestFixtureRow FromQuestData(uint questId, QuestActTemplate? objective)
    {
        // Preserve combined source facts for the established first gather act.
        // Hunt acts must remain scoped: a later gather cannot change their plan.
        if (objective != null)
        {
            var first = QuestManager.Instance.GetTemplate(questId)?.GetComponents(QuestComponentKind.Progress)
                .SelectMany(c => c.ActTemplates).Where(a => a.CountsAsAnObjective)
                .OrderBy(a => a.ParentComponent.Id).ThenBy(a => a.ActId).FirstOrDefault();
            if (first?.ActId == objective.ActId && objective is QuestActObjItemGather)
                objective = null;
        }
        var key = (questId, objective?.ActId ?? 0);
        if (RowCache.TryGetValue(key, out var cached) && cached.Sources.Unchanged)
            return cached.Row;

        // Snapshot BEFORE deriving so the entry names the surfaces the
        // derivation actually read, never a replacement that landed mid-read.
        var sources = DataSources.Snapshot();
        var generation = Interlocked.Read(ref s_dataGeneration);
        var derived = DeriveFromQuestData(questId, objective);

        if (Interlocked.Read(ref s_dataGeneration) == generation)
            RowCache[key] = new MemoEntry(derived, sources);
        return derived;
    }

    /// <summary>
    /// Drops the memoized row for one quest. Public for the game-data reload
    /// path and for anything else that replaces the row's source surfaces.
    /// </summary>
    public static void Invalidate(uint questId)
    {
        Interlocked.Increment(ref s_dataGeneration);
        foreach (var key in RowCache.Keys.Where(key => key.QuestId == questId))
            RowCache.TryRemove(key, out _);
    }

    /// <summary>
    /// Drops every memoized row — the reload seam's call, because a rebuild of
    /// templates, npc→pack rows or loot packs can change any quest's row.
    /// </summary>
    public static void InvalidateAll()
    {
        Interlocked.Increment(ref s_dataGeneration);
        RowCache.Clear();
    }

    /// <summary>
    /// The derivation itself: pure reads of the loaded tables, no caching and
    /// no world mutation. <see cref="FromQuestData"/> is the only entry point
    /// callers need.
    /// </summary>
    private static QuestFixtureRow DeriveFromQuestData(uint questId, QuestActTemplate? selectedObjective)
    {
        if (questId == 0)
            return Empty(questId);
        uint gatherActId = 0;
        uint preyItem = 0;
        var need = 0;
        uint reporterTemplate = 0;
        uint rewardItem = 0;
        uint useItemActId = 0;
        uint useItemTemplateId = 0;
        var useItemNeed = 0;
        uint talkActId = 0;
        uint talkNpcTemplate = 0;
        uint talkNpcGroup = 0;
        uint interactActId = 0;
        uint interactDoodad = 0;
        var interactNeed = 0;
        uint supplyActId = 0;
        uint supplyItem = 0;
        var supplyCount = 0;
        var autoComplete = false;
        var objectivePattern = QuestPattern.Unknown;
        var objectiveActType = "";
        uint huntActId = 0;
        var huntNeed = 0;
        uint huntNpcTemplate = 0;
        uint huntNpcGroup = 0;
        try
        {
            var template = QuestManager.Instance?.GetTemplate(questId);
            if (template == null)
                return Empty(questId);

            var gather = selectedObjective == null
                ? FirstAct<QuestActObjItemGather>(template, QuestComponentKind.Progress)
                : selectedObjective as QuestActObjItemGather;
            if (gather != null)
            {
                gatherActId = gather.ActId;
                preyItem = gather.ItemId;
                need = gather.Count;
            }

            var useItem = selectedObjective == null
                ? FirstAct<QuestActObjItemUse>(template, QuestComponentKind.Progress)
                : selectedObjective as QuestActObjItemUse;
            if (useItem != null)
            {
                useItemActId = useItem.ActId;
                useItemTemplateId = useItem.ItemId;
                useItemNeed = useItem.Count;
            }
            var interact = selectedObjective == null
                ? FirstAct<QuestActObjInteraction>(template, QuestComponentKind.Progress)
                : selectedObjective as QuestActObjInteraction;
            if (interact != null)
            {
                interactActId = interact.ActId;
                interactDoodad = interact.DoodadId;
                interactNeed = interact.Count;
            }
            var supply = FirstAct<QuestActSupplyItem>(template, QuestComponentKind.Supply);
            if (supply != null)
            {
                supplyActId = supply.ActId;
                supplyItem = supply.ItemId;
                supplyCount = supply.Count;
            }

            // The objective SHAPE is the Progress component's first act that
            // counts as an objective — the act the quest's step machine waits
            // on. A quest with none is the pure-bootstrap shape; one whose act
            // this vocabulary does not serve keeps the act's name so the
            // director can refuse it BY NAME instead of guessing.
            var objective = selectedObjective ?? template.GetComponents(QuestComponentKind.Progress)
                .SelectMany(c => c.ActTemplates)
                .Where(a => a.CountsAsAnObjective)
                .OrderBy(a => a.ParentComponent.Id)
                .ThenBy(a => a.ActId)
                .FirstOrDefault();
            if (objective != null)
            {
                objectiveActType = objective.GetType().Name;
                objectivePattern = ClassifyObjective(objective);
            }

            // The kill-family objective's own identity facts, read off the act
            // that classified as the kill-to-gather shape. A KILL objective has
            // no gather act, so the row's gather facts stay zero and the hunt
            // leg resolves its own target instead of the loot chain.
            if (objectivePattern == QuestPattern.KillX && objective is not QuestActObjItemGather)
            {
                huntActId = objective.ActId;
                huntNeed = objective.Count;
                (huntNpcTemplate, huntNpcGroup) = HuntIdentity(objective);
            }

            // The talk-family objective's own identity facts, read off the act
            // that classified as the talk-chain shape.
            if (objectivePattern == QuestPattern.TalkChain)
            {
                talkActId = objective!.ActId;
                switch (objective)
                {
                    case QuestActObjTalk talk:
                        talkNpcTemplate = talk.NpcId;
                        break;
                    case QuestActObjTalkNpcGroup groupTalk:
                        talkNpcGroup = groupTalk.NpcGroupId;
                        break;
                }
            }

            // Auto-complete: the engine's own completion act, on the Ready or
            // Reward step (252 carries it on Reward; a quest that carries it
            // needs no reporter turn-in leg).
            autoComplete = FirstAct<QuestActConAutoComplete>(template, QuestComponentKind.Ready) != null
                           || FirstAct<QuestActConAutoComplete>(template, QuestComponentKind.Reward) != null;

            // Reporter: the Ready-step turn-in target, else the Start-step
            // acceptor (a quest may carry both; for 251 they agree).
            reporterTemplate = FirstAct<QuestActConReportNpc>(template, QuestComponentKind.Ready)?.NpcId
                               ?? FirstAct<QuestActConAcceptNpc>(template, QuestComponentKind.Start)?.NpcId
                               ?? 0;

            // Reward: the Reward-step supply, else the Supply-step supply.
            rewardItem = (FirstAct<QuestActSupplyItem>(template, QuestComponentKind.Reward)
                          ?? FirstAct<QuestActSupplyItem>(template, QuestComponentKind.Supply))?.ItemId ?? 0;
        }
        catch
        {
            // Unreadable quest surface: keep whatever resolved.
        }

        var npcSources = ResolveDropSources(preyItem);
        var preyTemplate = npcSources.Count == 0 ? 0u : npcSources[0].NpcTemplateId;
        var preyPack = npcSources.Count == 0 ? 0u : npcSources[0].LootPackId;
        var gatherDoodads = ResolveDoodadSources(preyItem);
        var gatherDoodad = gatherDoodads.Count == 0 ? 0 : gatherDoodads[0];

        // The item-chain refinements below read the quest's GATHER item, so they
        // apply to the gather shape alone. A hunt objective carries no gather
        // act at all (its item is 0), and its source is the fight itself — the
        // act's own npc template or group, not a loot pack — so it keeps the
        // kill-to-gather shape and is never rewritten into a doodad/deliver
        // shape it has no item for.
        if (preyItem != 0 && objectivePattern == QuestPattern.KillX
            && preyTemplate == 0 && gatherDoodad != 0)
            objectivePattern = QuestPattern.GatherDoodad;
        // A gather whose item rides the quest's OWN Supply act is the
        // supplied-gather shape (the basil-delivery path): the step machine
        // grants it on accept, so the credit is the legitimate supply receipt
        // — no prey to kill, no doodad to draw, no item to inject. Supply wins
        // over the doodad chain (both may name the item; the engine grant is
        // the authoritative source), and a gather for any OTHER item keeps its
        // prior shape.
        if (preyItem != 0 && objectivePattern is QuestPattern.KillX or QuestPattern.GatherDoodad
            && supplyItem != 0 && preyItem == supplyItem)
            objectivePattern = QuestPattern.Deliver;

        return new QuestFixtureRow(
            questId, gatherActId, preyItem, need, preyTemplate, preyPack, reporterTemplate, rewardItem)
        {
            DerivedNpcSources = npcSources,
            DerivedNpcItem = preyItem,
            DerivedNpcTemplate = preyTemplate,
            DerivedNpcPack = preyPack,
            ObjectivePattern = objectivePattern,
            ObjectiveActType = objectiveActType,
            UseItemActId = useItemActId,
            UseItemTemplateId = useItemTemplateId,
            UseItemNeed = useItemNeed,
            InteractActId = interactActId,
            InteractDoodadTemplate = interactDoodad,
            InteractNeed = interactNeed,
            InteractUseSkill = ResolveDoodadUseSkill(interactDoodad),
            AutoComplete = autoComplete,
            GatherDoodadTemplate = gatherDoodad,
            GatherDoodadTemplates = gatherDoodads,
            GatherUseSkill = ResolveDoodadUseSkill(gatherDoodad),
            SupplyActId = supplyActId,
            SupplyItem = supplyItem,
            SupplyCount = supplyCount,
            HuntActId = huntActId,
            HuntNeed = huntNeed,
            HuntNpcTemplate = huntNpcTemplate,
            HuntNpcGroup = huntNpcGroup,
            TalkActId = talkActId,
            TalkNpcTemplate = talkNpcTemplate,
            TalkNpcGroup = talkNpcGroup
        };
    }
    /// <summary>
    /// A kill-family objective act's own (npc template, npc group) identity,
    /// read off the act's own fields — never hand-typed, never a quest id. A
    /// single-template hunt names an npc template; a group hunt names a quest
    /// monster group and no template (its membership is tested live through
    /// <c>QuestManager.CheckGroupNpc</c>, the engine's own test the talk-group
    /// and express-fire acts already gate on — no enumeration needed here).
    ///
    /// A zone-scoped kill names a zone group and NO npc template and NO group
    /// at all, so it resolves to (0, 0) and stays unserved: the only behavior
    /// it would admit is "attack the nearest arbitrary hostile", which is a
    /// wider capability than any gate has proven, not a reuse of one.
    /// </summary>
    private static (uint NpcTemplate, uint NpcGroup) HuntIdentity(QuestActTemplate act) => act switch
    {
        QuestActObjMonsterHunt hunt => (hunt.NpcId, 0u),
        QuestActObjMonsterGroupHunt groupHunt => (0u, groupHunt.QuestMonsterGroupId),
        _ => (0u, 0u)
    };
    /// <summary>
    /// The objective act's SHAPE, read off the act's own CLR type. The three
    /// shapes the vocabulary has always served (the item gather, the item use,
    /// the doodad interaction) derive directly; the hunt family derives the
    /// kill-to-gather shape and the talk family the talk-chain shape. Anything
    /// else — craft, escort, sphere, express-fire, aggro, level, cinema,
    /// complete-quest, item-group — derives <see cref="QuestPattern.Unknown"/>,
    /// which is the caller's fail-closed trigger: an unserved shape keeps the
    /// bootstrap floor with its act type NAMED, never a guessed verb list.
    ///
    /// THE HUNT FAMILY. <see cref="QuestActObjMonsterHunt"/> (kill N of one npc
    /// template) and <see cref="QuestActObjMonsterGroupHunt"/> (kill N of any
    /// npc template in a quest monster group) are satisfied by ONE engine
    /// fan-out: a single npc death runs
    /// <c>QuestManager.DoOnMonsterHuntEvents</c>, which emits
    /// <c>OnMonsterHunt</c> by victim template AND <c>OnMonsterGroupHunt</c>
    /// for every group that template belongs to
    /// (QuestManagerEvents.cs:187-212). So both shapes ride the AutoAttack +
    /// loot chain the registry already proves; neither needs a verb the
    /// registry does not already carry.
    ///
    /// THE TALK FAMILY. <see cref="QuestActObjTalk"/> (talk to npc template N)
    /// and <see cref="QuestActObjTalkNpcGroup"/> (talk to any member of an npc
    /// group) credit from <c>QuestActObjTalk.OnTalkMade</c> /
    /// <c>QuestActObjTalkNpcGroup.OnTalkNpcGroupMade</c>, both raised by
    /// <c>QuestManager.DoTalkMadeEvents</c> from the real
    /// <c>CSQuestTalkMadePacket</c> path — which is exactly the request
    /// <c>IGameplayActor.Talk</c> drives (GameplayActor.cs:1083-1106). So both
    /// shapes ride the one registry-green <c>Talk</c> verb.
    ///
    /// WHAT IS DELIBERATELY NOT MAPPED.
    /// <see cref="QuestActObjZoneKill"/> and
    /// <see cref="QuestActObjZoneMonsterHunt"/> name a zone group and no npc
    /// template and no group, so a plan could satisfy one only by attacking
    /// arbitrary nearby hostiles — a WIDER behavior than any gate has proven,
    /// not a reuse of a proved one. <see cref="QuestActObjAggro"/> scores kills
    /// by aggro rank, a different criterion from a count. Neither is guessed
    /// at. <see cref="QuestActObjDistance"/> and
    /// <see cref="QuestActObjSphere"/> are the reason
    /// <see cref="QuestPattern.ReachPos"/> stays unserved: distance resolves no
    /// destination at all (its RunAct re-reads a live radius census around the
    /// OWNER and holds no position), and a sphere's centre lives in the
    /// client's <c>quest_sign_sphere.g</c> level-design files, which this fork
    /// does not ship — so there is no reachable coordinate for MoveTo to aim
    /// at. A guess here would be worse than the bootstrap floor it replaces.
    /// </summary>
    private static QuestPattern ClassifyObjective(QuestActTemplate act) => act switch
    {
        QuestActObjItemGather => QuestPattern.KillX,
        QuestActObjItemUse => QuestPattern.UseItem,
        QuestActObjInteraction => QuestPattern.InteractDoodad,

        // Hunt family — one engine fan-out credits both (see above).
        QuestActObjMonsterHunt or QuestActObjMonsterGroupHunt => QuestPattern.KillX,

        // Talk family — the one registry-green Talk verb credits both.
        QuestActObjTalk or QuestActObjTalkNpcGroup => QuestPattern.TalkChain,

        _ => QuestPattern.Unknown
    };

    private static QuestFixtureRow Empty(uint questId)
        => new(questId, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Linear loot-list membership (the lists hold a handful of rows; no LINQ allocation).</summary>
    private static bool HasLoot(List<Loot> loots, uint itemId)
    {
        for (var i = 0; i < loots.Count; i++)
        {
            if (loots[i].ItemId == itemId)
                return true;
        }
        return false;
    }

    /// <summary>First act of <typeparamref name="T"/> in the given component kind, in (component id, act id) order.</summary>
    private static T? FirstAct<T>(QuestTemplate template, QuestComponentKind kind) where T : QuestActTemplate
        => template.GetComponents(kind)
            .SelectMany(c => c.ActTemplates)
            .OfType<T>()
            .OrderBy(a => a.ParentComponent.Id)
            .ThenBy(a => a.ActId)
            .FirstOrDefault();

    /// <summary>
    /// Inverts the loaded NPC→pack→item rows once per derived fixture row.
    /// Preserve the old representative's total order: default packs first,
    /// then NPC id, then pack id. Duplicate links collapse to one pair.
    /// No live-world read, item grant, or per-wake database query occurs here.
    /// </summary>
    private static IReadOnlyList<NpcDropSource> ResolveDropSources(uint itemId)
    {
        if (itemId == 0)
            return Array.Empty<NpcDropSource>();

        try
        {
            var found = new List<(NpcDropSource Source, bool DefaultPack)>();
            foreach (var row in ItemManager.Instance.GetAllLootPackDroppingNpcs())
            {
                if (row.NpcId == 0 || row.LootPackId == 0)
                    continue;
                var pack = LootGameData.Instance.GetPack(row.LootPackId);
                if (pack?.Loots == null || !HasLoot(pack.Loots, itemId))
                    continue;
                found.Add((new NpcDropSource(row.NpcId, row.LootPackId), row.DefaultPack));
            }
            if (found.Count == 0)
                return Array.Empty<NpcDropSource>();
            found.Sort(static (a, b) =>
            {
                var byDefault = b.DefaultPack.CompareTo(a.DefaultPack);
                if (byDefault != 0) return byDefault;
                var byNpc = a.Source.NpcTemplateId.CompareTo(b.Source.NpcTemplateId);
                return byNpc != 0 ? byNpc : a.Source.LootPackId.CompareTo(b.Source.LootPackId);
            });
            return Array.AsReadOnly(found.Select(entry => entry.Source).Distinct().ToArray());
        }
        catch
        {
            return Array.Empty<NpcDropSource>();
        }
    }

    /// <summary>
    /// Inverts the doodad→loot-func→item chain for <paramref name="itemId"/>:
    /// EVERY doodad template id whose func groups carry a loot func granting
    /// the item — a <c>DoodadFuncLootItem</c> naming it directly, or a
    /// <c>DoodadFuncLootPack</c> whose loot pack carries it — ascending. The
    /// scan walks the loaded <c>DoodadManager</c> func surfaces only (groups,
    /// funcs, loot-item and loot-pack templates): never a world scan, never a
    /// hand-typed id. Empty when no doodad grants the item (or the item is 0,
    /// or the doodad surface is unreadable) — the director's
    /// unresolvable-source signal for the gather-from-doodad shape. Never
    /// throws. A hand-built row that sets only
    /// <see cref="GatherDoodadTemplate"/> (single, no set) keeps working: the
    /// legs treat an empty set as "the single id".
    /// </summary>
    private static IReadOnlyList<uint> ResolveDoodadSources(uint itemId)
    {
        if (itemId == 0)
            return Array.Empty<uint>();

        try
        {
            var doodads = Singleton<DoodadManager>.PeekInstance;
            var loot = Singleton<LootGameData>.PeekInstance;
            if (doodads == null || loot == null)
                return Array.Empty<uint>();
            List<uint>? found = null;
            foreach (var template in DoodadTemplates(doodads))
            {
                if (template == null || template.Id == 0)
                    continue;
                if (GrantsItem(doodads, loot, template.Id, itemId))
                    (found ??= new List<uint>()).Add(template.Id);
            }
            if (found == null)
                return Array.Empty<uint>();
            found.Sort();
            return found.ToArray();
        }
        catch
        {
            return Array.Empty<uint>();
        }
    }

    /// <summary>
    /// True when any func group of <paramref name="doodadTemplateId"/> carries
    /// a loot func granting <paramref name="itemId"/>: a
    /// <c>DoodadFuncLootItem</c> naming it, or a <c>DoodadFuncLootPack</c>
    /// whose pack's loot list carries it. Reads the manager's own func/group
    /// surfaces through its public getters — the same tables its
    /// <c>Load</c> fills.
    /// </summary>
    private static bool GrantsItem(
        DoodadManager doodads, LootGameData loot, uint doodadTemplateId, uint itemId)
    {
        foreach (var groupId in doodads.GetDoodadFuncGroupsId(doodadTemplateId))
        {
            foreach (var func in doodads.GetDoodadFuncs(groupId))
            {
                if (func == null)
                    continue;
                if (func.FuncType == "DoodadFuncLootItem"
                    && doodads.GetFuncTemplate(func.FuncId, func.FuncType)
                        is DoodadFuncLootItem lootItem
                    && lootItem.ItemId == itemId)
                    return true;
                if (func.FuncType == "DoodadFuncLootPack"
                    && doodads.GetFuncTemplate(func.FuncId, func.FuncType)
                        is DoodadFuncLootPack lootPack)
                {
                    var pack = loot.GetPack(lootPack.LootPackId);
                    if (pack?.Loots != null && HasLoot(pack.Loots, itemId))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The interaction skill for <paramref name="doodadTemplateId"/>'s draw,
    /// read off the template's OWN func tables — never hand-typed, never a
    /// skill constant. Precedence mirrors
    /// <c>GameplayActor.ResolveInteractionSkill</c> (and the client's
    /// skill-targeted use through <c>DoodadManager.GetFunc</c>): within the
    /// template's func groups in ascending group-id order, the first explicit
    /// <c>func.SkillId</c> binding, else the first <c>DoodadFuncUse</c> /
    /// <c>DoodadFuncFakeUse</c> template skill id. 0 = skill-less draw.
    /// Never throws (an unreadable doodad surface yields 0).
    /// </summary>
    internal static uint ResolveDoodadUseSkill(uint doodadTemplateId)
    {
        if (doodadTemplateId == 0)
            return 0;
        try
        {
            var doodads = Singleton<DoodadManager>.PeekInstance;
            if (doodads == null)
                return 0;
            foreach (var groupId in doodads.GetDoodadFuncGroupsId(doodadTemplateId).OrderBy(g => g))
            {
                foreach (var func in doodads.GetDoodadFuncs(groupId))
                {
                    if (func == null)
                        continue;
                    if (func.SkillId > 0)
                        return func.SkillId;
                    var template = doodads.GetFuncTemplate(func.FuncId, func.FuncType);
                    if (template is DoodadFuncUse { SkillId: > 0 } useTemplate)
                        return useTemplate.SkillId;
                    if (template is DoodadFuncFakeUse { FakeSkillId: > 0 } fakeUseTemplate)
                        return fakeUseTemplate.FakeSkillId;
                }
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Every loaded doodad template, through the manager's own template
    /// surface. The row keeps the lowest granting id, so enumeration order
    /// never decides the result.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<DoodadTemplate> DoodadTemplates(
        DoodadManager doodads)
        => doodads.GetAllTemplates();
}
