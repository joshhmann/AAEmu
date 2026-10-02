using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;

using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Cold-start contract for <see cref="GameplayActorTestRig"/>'s seed surface.
///
/// SkillManager is a DI singleton with no parameterless constructor
/// (SkillManager.cs — primary ctor taking IAnimationManager + IPlotManager),
/// so a bare <c>SkillManager.Instance</c> on a process where nothing has been
/// seeded yet throws from <c>Singleton.OnInit</c> ("SkillManager has no
/// parameterless constructor. Resolve it from DI, or instantiate it with
/// explicit dependencies."). The rig's ONLY producer for that instance is
/// <see cref="GameplayActorTestRig.Seed"/>'s base surface — which is reached
/// solely through an actor or an explicit Seed() call.
///
/// The hazard: a rig entry point that touches SkillManager BEFORE anything
/// reached Seed() dies on a cold process, yet passes in a full-suite run
/// because some sibling test bootstrapped the singleton first. There is no
/// ordering guarantee — [NotInParallel] + ParallelLimiter serialize but do not
/// sequence. PvpFlaggingRigTests.DoDie_MultiParticipantAssists_ComprehensiveDamageHealAndCc
/// was the confirmed instance (its SeedBuffTemplate calls at :425 preceded
/// CreatePair at :437), so it failed 3/3 times when run alone and only passed
/// inside its own class.
///
/// These tests pin the contract on the ACTUAL cold state rather than relying
/// on process start order: the singleton is forced back to null and every
/// other rig bootstrap flag is short-circuited, which is byte-for-byte the
/// state a fresh `dotnet test --treenode-filter` process is in when the rig is
/// the first thing to touch a manager.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel] // process-wide singleton state (SkillManager/WorldManager/ItemManager)
public class GameplayActorRigColdStartTests
{
    private const BindingFlags StaticFlags = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>Disposes back to the exact singleton state captured on entry.</summary>
    private sealed class SingletonScope : IDisposable
    {
        private readonly Dictionary<Type, object?> _previous;

        public SingletonScope(params Type[] singletons)
        {
            _previous = new Dictionary<Type, object?>();
            foreach (var singleton in singletons)
            {
                _previous[singleton] = singleton.GetField("s_instance", StaticFlags)!.GetValue(null);
                singleton.GetField("s_instance", StaticFlags)!.SetValue(null, null);
            }
        }

        public void Dispose()
        {
            foreach (var (singleton, instance) in _previous)
                singleton.GetField("s_instance", StaticFlags)!.SetValue(null, instance);
        }
    }

    /// <summary>
    /// SeedBuffTemplate on a cold process must establish the DI-shaped
    /// SkillManager itself instead of letting Singleton.OnInit throw. This is
    /// the regression: it threw at GameplayActorTestRig.SeedSkillManager
    /// (rig.cs:442) on any cold caller.
    /// </summary>
    [Test]
    public async Task SeedBuffTemplate_ColdProcess_SeedsSkillManagerInsteadOfThrowing()
    {
        using var cold = new SingletonScope(typeof(Singleton<SkillManager>));

        GameplayActorTestRig.SeedBuffTemplate(ColdStartBuffId);

        // Observable through the real manager API, not the private dict.
        var template = SkillManager.Instance.GetBuffTemplate(ColdStartBuffId);
        await Assert.That(template).IsNotNull();
        await Assert.That(template!.Id).IsEqualTo(ColdStartBuffId);
    }

    /// <summary>
    /// The same contract for SeedSkillTemplate — the sibling public entry point
    /// that dereferenced SkillManager.Instance with no bootstrap at all.
    /// </summary>
    [Test]
    public async Task SeedSkillTemplate_ColdProcess_SeedsSkillManagerInsteadOfThrowing()
    {
        using var cold = new SingletonScope(typeof(Singleton<SkillManager>));

        GameplayActorTestRig.SeedSkillTemplate(ColdStartSkillId);

        var template = SkillManager.Instance.GetSkillTemplate(ColdStartSkillId);
        await Assert.That(template).IsNotNull();
        await Assert.That(template!.Id).IsEqualTo(ColdStartSkillId);
    }

    /// <summary>
    /// A cold SeedBuffTemplate must leave the rig's own baseline skill surface
    /// seeded too — the dictionary healing is the whole reason these entry
    /// points route through SeedSkillManager, and the actor paths that follow
    /// (Cast/Buff) dereference those rows.
    /// </summary>
    [Test]
    public async Task SeedBuffTemplate_ColdProcess_AlsoSeedsTheRigBaselineSkillSurface()
    {
        using var cold = new SingletonScope(typeof(Singleton<SkillManager>));

        GameplayActorTestRig.SeedBuffTemplate(ColdStartBuffId);

        var manager = SkillManager.Instance;
        await Assert.That(manager.GetSkillTemplate(GameplayActorTestRig.TestSkillId)).IsNotNull();
        await Assert.That(manager.GetSkillTemplate(GameplayActorTestRig.TestItemUseSkillId)).IsNotNull();
    }

    /// <summary>
    /// Missing-only discipline: the guard must never replace an instance a
    /// sibling rig already established (t_4f11a519 — the established manager
    /// may carry real loaded data). A cold call onto a pre-seeded singleton
    /// must leave that exact object in place.
    /// </summary>
    [Test]
    public async Task SeedBuffTemplate_WarmProcess_DoesNotReplaceEstablishedSkillManager()
    {
        // Establish the baseline first, then capture the instance the guard
        // must preserve — a bare Instance on a cold process is the very throw
        // under repair, so the warm path must be seeded, not assumed.
        GameplayActorTestRig.Seed();
        var established = SkillManager.Instance;
        var markerBuffId = ColdStartBuffId + 1;

        GameplayActorTestRig.SeedBuffTemplate(markerBuffId);

        await Assert.That(SkillManager.Instance).IsSameReferenceAs(established);
        await Assert.That(SkillManager.Instance.GetBuffTemplate(markerBuffId)).IsNotNull();
    }

    /// <summary>
    /// Buff ids must be distinct from the baseline skill ids so the "baseline
    /// surface" assertion above can only pass through the rig's own seeding.
    /// </summary>
    private const uint ColdStartBuffId = 99_901;
    private const uint ColdStartSkillId = 99_902;
}
