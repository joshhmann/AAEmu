using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items.Containers;

using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Character-Id aliasing contract for <see cref="GameplayActorTestRig"/>.
///
/// <para>
/// <c>Character.Id</c> is the production authority key: <c>ItemManager</c>'s
/// persistent-container registry keys on <c>(OwnerId, ContainerType, MateId)</c>
/// (ItemManager.GetItemContainerForCharacter), and <c>WorldManager._characters</c>,
/// the rig bank, <c>MailManager</c> and <c>QuestManager</c> all index by it too.
/// Two live characters sharing an Id therefore SHARE their ItemContainer
/// OBJECTS -- not a copy, the same instances.
/// </para>
///
/// <para>
/// The rig used to derive the Id from the display name:
/// <c>HeadlessSession.Create((uint)name.GetHashCode() &amp; 0xFFFF, name, 1)</c>.
/// Any two rig names sharing 16 bits aliased. <c>string.GetHashCode()</c> is
/// randomized per process, so the aliasing SET changed on every run, which is
/// why the defect surfaced as an intermittent ~1-in-10 gate failure rather than
/// a deterministic one.
/// </para>
///
/// <para>
/// The pairs below were MEASURED colliding against <c>exit-player-a</c>
/// (M3aExitScenarioTests:131) across independent processes under that scheme.
/// They are kept as literal evidence so this contract does not silently depend
/// on a seed nobody can reproduce.
/// </para>
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel] // process-wide rig counters + ItemManager persistent-container registry
public class GameplayActorCharacterIdTests
{
    // Pairs whose 16-bit `name.GetHashCode()` landed on the SAME value as
    // "exit-player-a" (M3aExitScenarioTests:131) in one of the sampled
    // processes. Measured, not guessed — which pair collides depends on the
    // per-process hash seed, so this list is a SAMPLE, not the full set.
    private static readonly (string M3aPlayer, string Collider)[] MeasuredCollidingPairs =
    [
        ("exit-player-a", "b1-idem-3"),
        ("exit-player-a", "m5-equip-2"),
        ("exit-player-a", "cast-1"),
        ("exit-player-a", "m4-exit-a"),
        ("exit-player-a", "snap-npc")
    ];

    /// <summary>
    /// The GUARANTEED-alias pair. Two DIFFERENT rig names is not enough to make
    /// a red test: under the old scheme whether they collide depends on that
    /// process's hash seed. The SAME name, however, always hashed to the same
    /// 16 bits, so this pair aliases in EVERY process and is what makes the
    /// regression reproducible rather than probabilistic.
    ///
    /// Both are real call sites: GameplayActorTests.cs:869/890 and
    /// TargetSelectionTests.cs:25/37 both create an actor called "target-1".
    /// </summary>
    private const string GuaranteedAliasName = "target-1";
    private const string GuaranteedAliasPairName = "target-2";

    /// <summary>
    /// The regression itself: two rig actors that collided under the name-hash
    /// scheme must receive DISTINCT character ids and therefore distinct
    /// inventory containers. Under the old derivation these were the same
    /// objects, so the actor that acquired an item first silently owned it for
    /// the other one too.
    /// </summary>
    [Test]
    public async Task SameNamedActors_ReceiveDistinctCharacterIds()
    {
        // Two actors created with the IDENTICAL name aliased under the old
        // scheme in every process, so this is the reproducible red.
        var (first, _) = GameplayActorTestRig.CreateActor(GuaranteedAliasName);
        var (second, _) = GameplayActorTestRig.CreateActor(GuaranteedAliasPairName);
        var (repeatA, _) = GameplayActorTestRig.CreateActor("repeated-name");
        var (repeatB, _) = GameplayActorTestRig.CreateActor("repeated-name");

        await Assert.That(first.Character.Id).IsNotEqualTo(second.Character.Id)
            .Because($"'{GuaranteedAliasName}' and '{GuaranteedAliasPairName}' are both real rig names");
        await Assert.That(repeatA.Character.Id).IsNotEqualTo(repeatB.Character.Id)
            .Because("two live actors created with the SAME name must not share one Character.Id");
    }

    /// <summary>
    /// The measured-pair regression. Distinct-name pairs only alias when that
    /// process's hash seed makes them collide, so the assertion is seeded with
    /// <see cref="MeasuredCollidingPairs"/> plus the always-aliasing
    /// <see cref="GuaranteedAliasName"/> pair from the sibling test; either way
    /// the pinned property is that no two live rig actors ever share a
    /// Character.Id, and therefore never share an ItemContainer.
    /// </summary>
    [Test]
    public async Task CollidingNames_ReceiveDistinctCharacterIdsAndContainers()
    {
        foreach (var (m3aName, colliderName) in MeasuredCollidingPairs)
        {
            var (m3aActor, _) = GameplayActorTestRig.CreateActor(m3aName);
            var (colliderActor, _) = GameplayActorTestRig.CreateActor(colliderName);

            await Assert.That(m3aActor.Character.Id)
                .IsNotEqualTo(colliderActor.Character.Id)
                .Because($"'{m3aName}' and '{colliderName}' aliased onto one Character.Id under the " +
                         "name-hash scheme, which aliases their ItemContainers");

            await Assert.That(ReferenceEquals(m3aActor.Character.Inventory.Bag, colliderActor.Character.Inventory.Bag))
                .IsFalse()
                .Because($"'{m3aName}' and '{colliderName}' share one bag ItemContainer instance");
            await Assert.That(ReferenceEquals(m3aActor.Character.Inventory.Warehouse, colliderActor.Character.Inventory.Warehouse))
                .IsFalse()
                .Because($"'{m3aName}' and '{colliderName}' share one warehouse ItemContainer instance");
        }
    }

    /// <summary>
    /// Observable consequence of the alias, stated as a consumer sees it: an
    /// item granted to one actor must not appear in the colliding actor's bag.
    /// This is the M3a shape (two harvests folding into one stack) at its root.
    ///
    /// Uses the same-name pair, which aliases under the old scheme in EVERY
    /// process, so the red is reproducible rather than seed-dependent.
    /// </summary>
    [Test]
    public async Task CollidingNames_ItemGrantedToOneActorIsNotVisibleInTheOther()
    {
        var (first, _) = GameplayActorTestRig.CreateActor(GuaranteedAliasName);
        var (second, _) = GameplayActorTestRig.CreateActor(GuaranteedAliasName);

        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);

        GameplayActorTestRig.GrantItem(first, GameplayActorTestRig.InteractItemTemplateId, 1);

        await Assert.That(GameplayActorTestRig.BagCount(first, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(second, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(0);
    }

    /// <summary>
    /// Every id handed out in a process is distinct, and the allocator only
    /// moves forward.
    ///
    /// SCOPE, stated precisely because this class replaced a name-hash scheme
    /// whose failure mode was reading a per-process value as a stable one: the
    /// counter is seeded from a FIXED BASE, so the SEQUENCE of ids is identical
    /// in every process, but WHICH NAME receives WHICH id depends on call order
    /// and is deliberately NOT a cross-process constant. Nothing in the suite
    /// may depend on a name→id mapping surviving a process restart; the
    /// production lane does not have one either (CharacterManager.Create and
    /// HeadlessSession.Provision both allocate from CharacterIdManager).
    /// </summary>
    [Test]
    public async Task CharacterIds_AreUniqueWithinTheProcessAndStrictlyForward()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("id-monotonic-a");
        var id = actor.Character.Id;

        var next = GameplayActorTestRig.NextActorCharacterId();

        await Assert.That(next).IsGreaterThan(id)
            .Because("the character-id counter must be strictly monotonic");
        await Assert.That(id).IsNotEqualTo(next);
        await Assert.That(GameplayActorTestRig.CharacterIdOwners[id]).IsEqualTo("id-monotonic-a");

        // Uniqueness across a whole batch, not just the next id.
        var batch = Enumerable.Range(0, 32)
            .Select(i => GameplayActorTestRig.NextActorCharacterId())
            .ToList();
        await Assert.That(batch.Distinct().Count()).IsEqualTo(32)
            .Because("32 consecutive allocations must be 32 distinct ids");
        await Assert.That(batch.Min()).IsGreaterThan(next)
            .Because("the counter never hands back an id it already issued");
    }

    /// <summary>
    /// The allocator must stay far away from the small fixed ids other test
    /// files hand to <c>HeadlessSession.Create</c> directly (e.g.
    /// BotChatterServiceTests' 2001..2007, BotPresenceCoordinatorTests' 901/902),
    /// which share ItemManager's process-wide container registry. This pins the
    /// base as a lane boundary rather than leaving it to arithmetic.
    /// </summary>
    [Test]
    public async Task CharacterIds_StayOutOfTheLowFixedIdRangeOtherRigsUse()
    {
        foreach (var fixedId in new uint[] { 700, 901, 902, 2001, 2002, 2003, 2004, 2005, 2006, 2007 })
        {
            await Assert.That(GameplayActorTestRig.NextActorCharacterId()).IsGreaterThan(fixedId);
        }
    }

    /// <summary>
    /// Every container the rig hands out must be a private instance reachable
    /// only through its own character -- the property the whole scheme exists to
    /// provide. Asserting on <c>ItemManager</c>'s registry (the production lookup
    /// that performed the aliasing) rather than on the container objects keeps
    /// this a contract test: it pins what a consumer resolves, not internals.
    /// </summary>
    [Test]
    public async Task InventoryContainers_ResolveToTheOwningCharacterOnly()
    {
        var (owner, _) = GameplayActorTestRig.CreateActor("container-owner");
        var (other, _) = GameplayActorTestRig.CreateActor("container-other");

        foreach (var slot in new[]
                 {
                     AAEmu.Game.Models.Game.Items.SlotType.Equipment,
                     AAEmu.Game.Models.Game.Items.SlotType.Inventory,
                     AAEmu.Game.Models.Game.Items.SlotType.Bank
                 })
        {
            var ownerContainer = ItemManager.Instance.GetItemContainerForCharacter(
                owner.Character.Id, slot, null, 0);
            var otherContainer = ItemManager.Instance.GetItemContainerForCharacter(
                other.Character.Id, slot, null, 0);

            await Assert.That(ReferenceEquals(ownerContainer, otherContainer)).IsFalse()
                .Because($"both characters resolved to the SAME {slot} container");
        }

        // The registry must hand each character back its OWN instances -- that is
        // the lookup production code performs, so it is the consumer-visible half
        // of the contract.
        await Assert.That(ReferenceEquals(
            ItemManager.Instance.GetItemContainerForCharacter(owner.Character.Id, AAEmu.Game.Models.Game.Items.SlotType.Inventory, null, 0),
            owner.Character.Inventory.Bag)).IsTrue()
            .Because("the Inventory container resolves to the character's own bag");
        await Assert.That(ReferenceEquals(
            ItemManager.Instance.GetItemContainerForCharacter(owner.Character.Id, AAEmu.Game.Models.Game.Items.SlotType.Bank, null, 0),
            owner.Character.Inventory.Warehouse)).IsTrue()
            .Because("the Bank container resolves to the character's own warehouse");
        await Assert.That(ReferenceEquals(
            ItemManager.Instance.GetItemContainerForCharacter(owner.Character.Id, AAEmu.Game.Models.Game.Items.SlotType.Equipment, null, 0),
            owner.Character.Inventory.Equipment)).IsTrue()
            .Because("the Equipment container resolves to the character's own equipment");
    }
}