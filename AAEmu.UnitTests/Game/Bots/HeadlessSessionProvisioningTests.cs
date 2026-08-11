using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items.Containers;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// Hermetic rig for the production HeadlessSession provisioning contract
/// (t_302b67bf). No MySQL: these tests lock the pilot-fixture boundary and
/// the fail-loudly behavior of the production path without a database. The
/// live provision → activate → persist → deactivate round-trip rides the
/// env-gated live rig (HeadlessSessionProvisioningLiveTests).
/// </summary>
[NotInParallel]
public class HeadlessSessionProvisioningTests
{
    // ---------------------------------------------------------------- pilot fixture boundary

    [Test]
    public async Task Create_PilotFixture_IsSyntheticAndDbFree()
    {
        // The M2b-E2E fixture (DB-row-less, synthetic world) MUST keep working
        // as-is — review correction (b): fixture only, NOT the production
        // citizen path. It must never touch MySQL: no Save, no provisioning.
        SeedFixtureSingletons();
        var session = HeadlessSession.Create(4200001u, "PilotFixtureBot", 1);

        await Assert.That(session.Character.Id).IsEqualTo(4200001u);
        await Assert.That(session.Character.Name).IsEqualTo("PilotFixtureBot");
        await Assert.That(session.Character.Connection).IsNull(); // no network session
        await Assert.That(session.World.Template.Name).IsEqualTo("headless_world"); // synthetic world
        await Assert.That(session.ProvisionedAccount).IsNull(); // no managed account row
    }

    // ---------------------------------------------------------------- production path, no DB

    [Test]
    public void Provision_RejectsNonManagedUsername_BeforeAnyDbAccess()
    {
        // A human-style username must fail validation BEFORE the service can
        // touch the database — the provisioning path can never create or
        // adopt a non-bot account even when the DB is up.
        Assert.Throws<ArgumentException>(() => HeadlessSession.Provision("josh", "JoshBot"));
    }

    [Test]
    public void Provision_WithoutMySql_FailsLoudly()
    {
        // Without a database the production path must THROW — never silently
        // fall back to the synthetic fixture (that fallback is exactly the
        // review's correction (b)).
        Assert.Throws<Exception>(() => HeadlessSession.Provision("bot_managed_hermetic_0001", "HermeticBot"));
    }

    [Test]
    public void Provision_RejectsEmptyCharacterName()
    {
        // Character names ride the same NameManager rules as humans; an empty
        // name can never produce a characters row.
        Assert.Throws<ArgumentException>(() => HeadlessSession.Provision("bot_managed_hermetic_0002", ""));
    }

    // ---------------------------------------------------------------- adopt decision (t_db5b2be7)

    [Test]
    public async Task ResolveAdoptable_UnregisteredName_ReturnsZero_CreatePath()
    {
        // A name no one owns is a fresh create — no adoption.
        var nameManager = BuildSeededNameManager([]);

        var result = HeadlessSession.ResolveAdoptableBotCharacterId(nameManager, "citizen01", botAccountId: 14);

        await Assert.That(result).IsEqualTo(0u);
    }

    [Test]
    public async Task ResolveAdoptable_BotOwnedName_ReturnsExistingCharacterId()
    {
        // A prior boot's row: the name is registered under the SAME managed
        // bot account → adopt (reload + re-embody), never a new row.
        var nameManager = BuildSeededNameManager(new Dictionary<uint, (string Name, uint AccountId)>
        {
            [101] = ("citizen01", 14)
        });

        var result = HeadlessSession.ResolveAdoptableBotCharacterId(nameManager, "citizen01", botAccountId: 14);

        await Assert.That(result).IsEqualTo(101u);
    }

    [Test]
    public void ResolveAdoptable_ForeignOwnedName_Throws_SquattingProtection()
    {
        // The name is taken by a HUMAN account (7) — bots never adopt foreign
        // rows; the NameManager duplicate guard stays in force for human
        // names, exactly like the create-only path.
        var nameManager = BuildSeededNameManager(new Dictionary<uint, (string Name, uint AccountId)>
        {
            [202] = ("citizen01", 7)
        });

        Assert.Throws<ArgumentException>(() =>
            HeadlessSession.ResolveAdoptableBotCharacterId(nameManager, "citizen01", botAccountId: 14));
    }

    [Test]
    public void ResolveAdoptable_AnotherBotAccountsName_Throws_NoCrossAdoption()
    {
        // The name is owned by a DIFFERENT managed bot account (15). Same
        // guard as human squatting: adoption is strictly find-by-name+account,
        // so a config change that renames the account prefix surfaces loudly
        // instead of silently re-attaching another bot's character.
        var nameManager = BuildSeededNameManager(new Dictionary<uint, (string Name, uint AccountId)>
        {
            [303] = ("citizen01", 15)
        });

        Assert.Throws<ArgumentException>(() =>
            HeadlessSession.ResolveAdoptableBotCharacterId(nameManager, "citizen01", botAccountId: 14));
    }

    /// <summary>Builds a fresh NameManager seeded from in-memory registries
    /// (the internal Load overload — the same shape NameManager.Load reads
    /// from the characters table at boot).</summary>
    private static NameManager BuildSeededNameManager(Dictionary<uint, (string Name, uint AccountId)> characters)
    {
        var nameManager = new NameManager();
        var ids = new Dictionary<uint, string>();
        var names = new Dictionary<string, uint>();
        var accounts = new Dictionary<uint, uint>();
        foreach (var (id, (characterName, accountId)) in characters)
        {
            ids[id] = characterName;
            names[characterName] = id;
            accounts[id] = accountId;
        }

        nameManager.Load(ids, names, accounts);
        return nameManager;
    }

    // ---------------------------------------------------------------- singleton seeding

    /// <summary>
    /// Seeds exactly the singletons HeadlessSession.Create resolves
    /// (Inventory ctor → ContainerIdManager.Instance.GetNextId +
    /// ItemManager.GetItemContainerForCharacter) with missing-only guards.
    ///
    /// NEVER call PlayerbotPilotRig.SeedPilotSingletons() from a new rig: its
    /// one-shot s_seeded flag flips full-suite ordering (t_4f11a519) — a
    /// scenario rig replacing QuestManager afterwards then NREs later pilot
    /// probes. Seeding is per-singleton, never replaces an established
    /// singleton, and never touches the pilot flag.
    /// </summary>
    private static void SeedFixtureSingletons()
    {
        SetSingletonIfMissing(typeof(Singleton<ItemManager>), BuildFixtureItemManager());
        // Fail-closed on missing MySQL (logged, empty used ids), then serves
        // incrementing ids from its range — same call the pilot rig makes.
        ContainerIdManager.Instance.Initialize(true);
    }

    private static ItemManager BuildFixtureItemManager()
    {
        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);

        // The Inventory ctor resolves ItemManager.GetItemContainerForCharacter,
        // which iterates _allPersistentContainers. Scenario-rig ItemManagers
        // never seed it (their inventory bypasses ItemManager) — a null
        // registry would NRE the ordinary Character construction path.
        var containerField = typeof(ItemManager).GetField("_allPersistentContainers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var existing = containerField?.GetValue(itemManager) as ConcurrentDictionary<ulong, ItemContainer>;
        if (existing == null)
            containerField?.SetValue(itemManager, new ConcurrentDictionary<ulong, ItemContainer>());

        return itemManager;
    }

    private static void SetSingletonIfMissing(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) != null)
            return; // never replace an established singleton (t_4f11a519)
        field.SetValue(null, instance);
    }
}
