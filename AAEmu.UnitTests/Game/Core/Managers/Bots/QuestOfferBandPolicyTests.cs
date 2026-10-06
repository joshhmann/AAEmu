using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Offer-BAND POLICY (default-off): the PlayerBot's quest-offer band no longer
/// hard-codes levels 1-9. With AAEMU_BOT_LEVEL_BAND unset (the default) the
/// band is byte-identical to the frozen 1..9 starter band; with it set, the
/// band tracks the CHARACTER's own level so the visible content set grows as
/// the bot levels.
///
/// The band is a PRE-FILTER on top of <c>GameplayActor.IsDiscoverable</c>,
/// never a replacement — the engine's real requirement gate (Start component
/// unit_reqs, including the MaxLevel kind that vetoes from ABOVE the
/// character's level) stays authoritative and is exercised here directly.
///
/// Fixture ids are canonical 1.2 rows from AAEmu.Game/Data/compact.sqlite3
/// (md5 78b3bdbf038db3b927056106efdf91af), chosen because each one isolates a
/// property the policy must respect:
///   251  / 252  — real rows at LEVEL 2 / 3 (in-band under both policies).
///   2514 — real row at LEVEL 10, zone 1 (the first level today's band hides).
///   6516 — real row at LEVEL 21 carrying unit_reqs MaxLevel(25) on its Start
///          component (kind_id 69), so a level-40 character is vetoed from
///          above: proof template.Level is NOT an upper bound on offerability.
///   6514 also carries the in-band companion cases at LEVEL 10.
/// Fixture COMPONENT/QUEST ids in the 90_7xx range are synthetic and
/// collision-free against canonical data; their LEVEL/requirement values are
/// copied from the canonical rows named above.
///
/// Pure unit tests: seeded headless singletons only, no E2E database is
/// opened or reset.
/// </summary>
[NotInParallel]
public class QuestOfferBandPolicyTests
{
    private const byte DefaultHeadroom = 5;

    // ---- canonical LEVEL values lifted from the canonical DB ----
    private const byte CanonicalLowLevel = 2;    // quest 251 / 252
    private const byte CanonicalTenLevel = 10;   // quest 6514
    private const byte CanonicalTwentyOneLevel = 21; // quest 6516 (MaxLevel 25)

    // ---- synthetic fixture ids (Start component / quest / npc template) ----
    // Block 90_770-90_782, plus the requirement row at 90_787.
    // UNIQUE REPO-WIDE: verified by enumerating every 9xxxx literal under
    // AAEmu.UnitTests/ and grepping each of my ids back across the tree. The
    // 90_7xx block is densely claimed — 90_700-906 the discovery rig,
    // 90_710-729 its v2 quests, 90_730 its level-requirement row,
    // 90_740-765 DoodadTurnInTests (its quest, its three components AND its
    // OfferNpcTemplateId 90_763), 90_798/799 the "no offers" templates. My
    // first attempt used 90_740-763 and collided with DoodadTurnInTests'
    // OfferNpcTemplateId; 90_770-90_787 is the only free run. All are also
    // absent from canonical quest_contexts / quest_components / npcs /
    // unit_reqs in compact.sqlite3.
    //
    // COLLISION LESSON: these ids land in the PROCESS-WIDE QuestManager
    // singleton with no teardown, and [NotInParallel] classes share one
    // process. A shared id does not fail in isolation — it fails only in a
    // full-suite run, when another class has already seeded a real offer at
    // the same template.
    private const uint QuestLow = 90_770;
    private const uint CompLow = 90_771;
    private const uint NpcLow = 90_772;

    private const uint QuestTen = 90_773;
    private const uint CompTen = 90_774;
    private const uint NpcTen = 90_775;

    private const uint QuestVetoed = 90_776;      // canonical 6516 shape
    private const uint CompVetoed = 90_777;
    private const uint NpcVetoed = 90_778;

    private const uint QuestOrphan = 90_779;
    private const uint CompOrphan = 90_780;
    private const uint NpcOrphan = 90_781;

    /// <summary>Offerer with nothing seeded at it (the "no spawner tables" arm).</summary>
    private const uint NpcEmpty = 90_782;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();

        // Quest templates mirroring canonical rows. SeedQuestOffer creates the
        // Start component + QuestActConAcceptNpc linkage exactly as the loader
        // does (quest_act_con_accept_npcs), which is what
        // QuestManager.GetQuestsOfferedByNpc walks.
        GameplayActorTestRig.SeedQuestOffer(QuestLow, CompLow, NpcLow, level: CanonicalLowLevel);
        GameplayActorTestRig.SeedQuestOffer(QuestTen, CompTen, NpcTen, level: CanonicalTenLevel);
        GameplayActorTestRig.SeedQuestOffer(QuestVetoed, CompVetoed, NpcVetoed, level: CanonicalTwentyOneLevel);

        // Canonical 6516 carries unit_reqs MaxLevel(25) on its Start
        // component: a character above 25 is refused. A level-40 character must
        // therefore NOT see this LEVEL-21 quest even though 21 is deep inside
        // its band — the band must never substitute for IsDiscoverable.
        GameplayActorTestRig.SeedQuestComponentMaxLevelRequirement(CompVetoed, maxLevel: 25);
    }

    /// <summary>Executor + a fresh headless actor at the requested level.</summary>
    private static (BotRoamStepExecutor Executor, GameplayActor Actor) Rig(byte characterLevel, string name)
    {
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = characterLevel;
        // Rig gotcha (GameplayActorQuestDiscoveryTests): MaxHp derives from
        // Level, so Hp must be re-seated or the actor reads as dead.
        actor.Character.Hp = actor.Character.MaxHp;
        return (new BotRoamStepExecutor(), actor);
    }

    // ================================================================
    // (1) Gate OFF — byte-identical to today's 1..9 band.
    // ================================================================
    [Test]
    public async Task GateOff_BandIsExactlyTodaysFrozen1To9_AtEveryCharacterLevel()
    {
        // Today's behaviour does not consult the character's level at all.
        // Proving that at a LOW level (where a level-relative policy would
        // coincide with 1..9) AND at a HIGH level (where it would differ) is
        // what makes "gate off = unchanged" a real claim, not a tautology.
        foreach (var characterLevel in new byte[] { 1, 10, 25, 40 })
        {
            var off = BotRoamStepExecutor.ResolveOfferBand(levelBand: false, DefaultHeadroom, characterLevel);

            await Assert.That(off.BandMin).IsEqualTo((byte)1);
            await Assert.That(off.BandMax).IsEqualTo((byte)9);

            // The default QuestOptions today (what OffersWalkableQuest built
            // inline) is the same record — flag-off must not perturb it.
            var legacy = new QuestDecisionScenario.QuestOptions();
            await Assert.That(off).IsEqualTo(legacy);
        }
    }

    [Test]
    public async Task GateOff_LevelTenQuestStaysInvisible_EvenForALevelFortyCharacter()
    {
        // Canonical 6514 sits at LEVEL 10: one level above today's ceiling.
        // With the gate off it must remain invisible to a level-40 character
        // — the starter-band confinement this change exists to lift.
        var (executor, actor) = Rig(40, "band-off-lv40");

        var band = BotRoamStepExecutor.ResolveOfferBand(
            executor.EnableLevelBand, executor.LevelBandHeadroom, actor.Character.Level);

        await Assert.That(executor.EnableLevelBand).IsFalse(); // env unset ⇒ default off
        await Assert.That(BandAdmit(band, CanonicalTenLevel)).IsFalse();
        await Assert.That(BandAdmit(band, CanonicalLowLevel)).IsTrue();
    }

    // ================================================================
    // (2) Gate ON, low-level character — visible set is the low band.
    // ================================================================
    [Test]
    public async Task GateOn_LevelOneCharacter_SeesTheLowBand_AndNothingAboveLevelSix()
    {
        // Worked example, character level 1: max = clamp(1 + 5, 1, 49) = 6.
        var band = BotRoamStepExecutor.ResolveOfferBand(levelBand: true, DefaultHeadroom, 1);

        await Assert.That(band.BandMin).IsEqualTo((byte)1);
        await Assert.That(band.BandMax).IsEqualTo((byte)6);

        await Assert.That(BandAdmit(band, CanonicalLowLevel)).IsTrue();   // 2  — visible
        await Assert.That(BandAdmit(band, 6)).IsTrue();                    // band edge
        await Assert.That(BandAdmit(band, 7)).IsFalse();                   // above headroom
        await Assert.That(BandAdmit(band, CanonicalTenLevel)).IsFalse();   // 10 — still hidden
    }

    [Test]
    public async Task GateOn_LowLevelCharacter_NeverSeesContentAboveTheFrozenCeiling()
    {
        // A level-5 character: max = 10. It DOES gain level-10 content, but
        // never level-11+ — the widening is bounded by headroom, not open-ended.
        var band = BotRoamStepExecutor.ResolveOfferBand(levelBand: true, DefaultHeadroom, 5);

        await Assert.That(band.BandMax).IsEqualTo((byte)10);
        await Assert.That(BandAdmit(band, 10)).IsTrue();
        await Assert.That(BandAdmit(band, 11)).IsFalse();
    }

    // ================================================================
    // (3) Gate ON, higher-level character — expands upward, but the
    //     engine's MaxLevel veto still excludes what the engine refuses.
    // ================================================================
    [Test]
    public async Task GateOn_LevelFifteenCharacter_AdmitSetExpandsAboveNine()
    {
        // Worked example, character level 15: max = clamp(15 + 5, 1, 49) = 20.
        var band = BotRoamStepExecutor.ResolveOfferBand(levelBand: true, DefaultHeadroom, 15);

        await Assert.That(band.BandMin).IsEqualTo((byte)1);
        await Assert.That(band.BandMax).IsEqualTo((byte)20);

        // Floor never rises: still-completable one-time low content stays
        // visible (20-29 holds ZERO repeatables, so a rising floor would
        // present only already-completed non-repeatables).
        await Assert.That(BandAdmit(band, CanonicalLowLevel)).IsTrue();
        // Content previously invisible at ANY character level is now in view.
        await Assert.That(BandAdmit(band, CanonicalTenLevel)).IsTrue();
        await Assert.That(BandAdmit(band, CanonicalTwentyOneLevel)).IsFalse(); // 21 > 20
    }

    [Test]
    public async Task GateOn_LevelFortyCharacter_EngineMaxLevelVetoStillExcludesTheOffer()
    {
        // Canonical 6516: LEVEL 21, Start-component MaxLevel(25). At character
        // level 40 the band (max = 45) ADMITS it — and the ENGINE REFUSES it.
        // This is the whole point of keeping the band a pre-filter: the wider
        // band must not become a licence to walk at an illegal offer.
        var (executor, actor) = Rig(40, "maxlevel-veto-lv40");

        var band = BotRoamStepExecutor.ResolveOfferBand(
            executor.EnableLevelBand = true, executor.LevelBandHeadroom, actor.Character.Level);

        // The band admits 21 …
        await Assert.That(BandAdmit(band, CanonicalTwentyOneLevel)).IsTrue();

        // … but the PRODUCTION pre-filter still refuses to call that offerer
        // walkable, because IsDiscoverable vetoes it from ABOVE the
        // character's level. This is asserted through the real
        // OffersWalkableQuest, not against the gate directly: a band that had
        // quietly replaced IsDiscoverable would leave the veto invisible here
        // and send the bot walking at an illegal offer.
        await Assert.That(OffersWalkable(executor, actor, NpcVetoed, band)).IsFalse();

        // And the engine's own diagnostic names the real requirement, proving
        // the refusal is the MaxLevel Start-component gate and not a band
        // artefact.
        await Assert.That(actor.IsDiscoverable(QuestVetoed)).IsFalse();
        await Assert.That(actor.DiscoverRejectReason(QuestVetoed)).IsEqualTo($"REQ_FAIL_START_{CompVetoed}");

        // A non-gated in-band offerer IS walkable, so the veto above is
        // quest-specific and not a blanket refusal by the filter.
        await Assert.That(OffersWalkable(executor, actor, NpcTen, band)).IsTrue();
        await Assert.That(actor.IsDiscoverable(QuestTen)).IsTrue();
        await Assert.That(actor.DiscoverRejectReason(QuestTen)).IsEqualTo("");
    }

    [Test]
    public async Task GateOn_LevelFortyCharacter_AdmitsContentThatWasNeverVisibleBefore()
    {
        var band = BotRoamStepExecutor.ResolveOfferBand(levelBand: true, DefaultHeadroom, 40);

        await Assert.That(band.BandMax).IsEqualTo((byte)45);
        // 10 and 21 are both admitted where the frozen 1..9 band hid both.
        await Assert.That(BandAdmit(band, CanonicalTenLevel)).IsTrue();
        await Assert.That(BandAdmit(band, CanonicalTwentyOneLevel)).IsTrue();
    }

    // ================================================================
    // (4) LEVEL = 50 "ungated" sentinel — deliberate, not accidental.
    // ================================================================
    [Test]
    public async Task SentinelLevel50_IsExcludedAtEveryCharacterLevel_ByDesign()
    {
        // quest_contexts.LEVEL = 50 means "ungated", not "level 50": 1086
        // rows sit there and 642 carry no requirement row at all. A window
        // that simply followed the character would clamp onto 50 around
        // character level 45 and admit all ~1086 by accident. This policy
        // caps at 49 so they are never admitted by a band accident.
        foreach (var characterLevel in new byte[] { 45, 49, 55, 80, 255 })
        {
            var band = BotRoamStepExecutor.ResolveOfferBand(levelBand: true, DefaultHeadroom, characterLevel);

            await Assert.That(band.BandMax).IsLessThanOrEqualTo(
                BotRoamStepExecutor.LevelBandSentinelExclusiveCeiling);
            await Assert.That(BandAdmit(band, 50)).IsFalse();
        }
    }

    [Test]
    public async Task SentinelLevel50_StaysExcludedEvenWithALargeHeadroom()
    {
        // The clamp is structural, not a property of the default headroom:
        // a deliberate wide override still refuses to sweep the sentinel in.
        var band = BotRoamStepExecutor.ResolveOfferBand(levelBand: true, headroom: 200, 40);

        await Assert.That(BandAdmit(band, 50)).IsFalse();
        await Assert.That(band.BandMax).IsEqualTo(BotRoamStepExecutor.LevelBandSentinelExclusiveCeiling);
    }

    // ================================================================
    // (5) Fail-closed when the level cannot be read.
    // ================================================================
    [Test]
    public async Task UnreadableLevel_FallsBackToTheFrozen1To9Band_NeverWidens()
    {
        var closed = BotRoamStepExecutor.ResolveOfferBand(levelBand: true, DefaultHeadroom, null);

        await Assert.That(closed.BandMin).IsEqualTo((byte)1);
        await Assert.That(closed.BandMax).IsEqualTo((byte)9);
        await Assert.That(closed).IsEqualTo(new QuestDecisionScenario.QuestOptions());
    }

    [Test]
    public async Task UnreadableLevel_WithTheGateOff_StillReturnsTheFrozenBand()
    {
        var closed = BotRoamStepExecutor.ResolveOfferBand(levelBand: false, DefaultHeadroom, null);

        await Assert.That(closed.BandMax).IsEqualTo((byte)9);
    }

    // ================================================================
    // (6) The template == null orphan case still returns without throwing.
    // ================================================================
    [Test]
    public async Task OrphanQuestComponent_WithNoQuestContext_OffersWalkableReturnsFalseWithoutThrowing()
    {
        // 116 quest_components rows have no matching quest_contexts row. The
        // pre-filter must dereference nothing and must not throw on one.
        // The offerer here advertises ONLY the orphan component, so the guard
        // is the single thing standing between this call and a null deref.
        SeedOrphanOfferOnly();

        var (executor, actor) = Rig(12, "orphan-only-lv12");
        var band = BotRoamStepExecutor.ResolveOfferBand(
            executor.EnableLevelBand = true, executor.LevelBandHeadroom, actor.Character.Level);

        await Assert.That(OffersWalkable(executor, actor, NpcOrphan, band)).IsFalse();

        // Same call with the gate off, and on an empty offerer: still no throw.
        var off = BotRoamStepExecutor.ResolveOfferBand(false, DefaultHeadroom, 12);
        await Assert.That(OffersWalkable(executor, actor, NpcOrphan, off)).IsFalse();
        await Assert.That(OffersWalkable(executor, actor, NpcEmpty, off)).IsFalse();
    }

    [Test]
    public async Task OrphanOfferer_DoesNotMaskARealDiscoverableOfferOnAnotherOfferer()
    {
        // The guard skips the orphan and keeps scanning: a broken row on one
        // offerer must not suppress a legitimate one elsewhere.
        SeedOrphanOfferOnly();
        var (executor, actor) = Rig(12, "orphan-scan-lv12");
        var band = BotRoamStepExecutor.ResolveOfferBand(
            executor.EnableLevelBand = true, executor.LevelBandHeadroom, actor.Character.Level);

        await Assert.That(OffersWalkable(executor, actor, NpcOrphan, band)).IsFalse();
        await Assert.That(OffersWalkable(executor, actor, NpcTen, band)).IsTrue();
    }

    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>The band's inclusive admit predicate, mirroring the production
    /// comparison at the travel pre-filter.</summary>
    private static bool BandAdmit(QuestDecisionScenario.QuestOptions band, byte questLevel)
        => questLevel >= band.BandMin && questLevel <= band.BandMax;

    private static bool OffersWalkable(BotRoamStepExecutor executor, GameplayActor actor,
        uint npcTemplateId, QuestDecisionScenario.QuestOptions band)
    {
        var method = typeof(BotRoamStepExecutor).GetMethod("OffersWalkableQuest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            return (bool)method.Invoke(null, [actor, npcTemplateId, band])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// Reproduces the canonical orphan shape: a Start component that carries an
    /// accept act (so QuestManager.GetQuestsOfferedByNpc reports it) while its
    /// PARENT QUEST TEMPLATE is absent, so GetTemplate returns null. Mirrors
    /// the 116 quest_components rows whose quest_context_id resolves to nothing.
    /// </summary>
    private static void SeedOrphanOfferOnly()
    {
        var manager = QuestManager.Instance;
        var componentTemplates =
            (Dictionary<uint, AAEmu.Game.Models.Game.Quests.QuestComponentTemplate>)
            GameplayActorTestRig.GetField(manager, "_componentTemplates");

        // ParentQuestTemplate is a ctor argument on QuestComponentTemplate and
        // is the only path to the quest id; a template id no manager holds
        // reproduces "quest_contexts row missing" without touching the shared
        // template registry.
        var ghostParent = new AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate
        {
            Id = QuestOrphan,
            Level = CanonicalLowLevel
        };
        var templates = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate>)
            GameplayActorTestRig.GetField(manager, "_questTemplates");
        // Register then remove: the component keeps the parent reference the
        // loader gives it, while GetTemplate(QuestOrphan) resolves to null.
        templates[QuestOrphan] = ghostParent;
        var component = new AAEmu.Game.Models.Game.Quests.QuestComponentTemplate(ghostParent)
        {
            Id = CompOrphan,
            KindId = QuestComponentKind.Start
        };
        componentTemplates[CompOrphan] = component;

        var actsByType =
            (Dictionary<string, Dictionary<uint, AAEmu.Game.Models.Game.Quests.Templates.QuestActTemplate>>)
            GameplayActorTestRig.GetField(manager, "_actTemplatesByDetailType");
        var detailType = nameof(AAEmu.Game.Models.Game.Quests.Acts.QuestActConAcceptNpc);
        if (!actsByType.TryGetValue(detailType, out var acts))
        {
            acts = [];
            actsByType[detailType] = acts;
        }
        acts[CompOrphan] = new AAEmu.Game.Models.Game.Quests.Acts.QuestActConAcceptNpc(component)
        {
            DetailId = CompOrphan,
            NpcId = NpcOrphan
        };

        templates.Remove(QuestOrphan);
    }
}
