using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class SaveManagerTests
{
    private static SaveManager CreateManager(IWorldManager world)
    {
        var mockTask = Mock.Of<ITaskManager>();
        var mockHousing = Mock.Of<IHousingManager>();
        var mockMail = Mock.Of<IMailManager>();
        var mockItem = Mock.Of<IItemManager>();
        var mockAuction = Mock.Of<IAuctionManager>();
        var mockCrime = Mock.Of<ICrimeManager>();

        return new SaveManager(
            mockTask.Object,
            mockHousing.Object,
            mockMail.Object,
            mockItem.Object,
            mockAuction.Object,
            mockCrime.Object,
            world);
    }

    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockTask = Mock.Of<ITaskManager>();
        var mockHousing = Mock.Of<IHousingManager>();
        var mockMail = Mock.Of<IMailManager>();
        var mockItem = Mock.Of<IItemManager>();
        var mockAuction = Mock.Of<IAuctionManager>();
        var mockCrime = Mock.Of<ICrimeManager>();
        var mockWorld = Mock.Of<IWorldManager>();

        var manager = new SaveManager(
            mockTask.Object,
            mockHousing.Object,
            mockMail.Object,
            mockItem.Object,
            mockAuction.Object,
            mockCrime.Object,
            mockWorld.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockTask);
        Mock.VerifyNoOtherCalls(mockHousing);
        Mock.VerifyNoOtherCalls(mockMail);
        Mock.VerifyNoOtherCalls(mockItem);
        Mock.VerifyNoOtherCalls(mockAuction);
        Mock.VerifyNoOtherCalls(mockCrime);
        Mock.VerifyNoOtherCalls(mockWorld);
    }

    private static List<Character> MakeCharacters(int count)
    {
        var chars = new List<Character>(count);
        for (var i = 0; i < count; i++)
        {
            chars.Add(new Character(new UnitCustomModelParams())
            {
                Id = (uint)(i + 1),
                Name = $"rig-{i}"
            });
        }

        return chars;
    }

    [Test]
    public async Task GetCharactersToSave_AllClean_ReturnsNothing()
    {
        var world = new FakeWorldManager();
        var all = MakeCharacters(1000);
        // Default IsDirty = true on construction; simulate "already persisted" state
        foreach (var c in all)
            c.IsDirty = false;
        world.Characters = all;

        var manager = CreateManager(world);

        var toSave = manager.GetCharactersToSave(false);
        await Assert.That(toSave).IsEmpty();
    }

    [Test]
    public async Task GetCharactersToSave_OnlyDirty_ReturnsTouchedSubset()
    {
        var world = new FakeWorldManager();
        var all = MakeCharacters(1000);
        foreach (var c in all)
            c.IsDirty = false;
        // Simulate a 1,000-bot load where only a handful were touched this cycle
        all[7].MarkDirty();
        all[42].MarkDirty();
        all[999].MarkDirty();
        world.Characters = all;

        var manager = CreateManager(world);

        var toSave = manager.GetCharactersToSave(false);
        await Assert.That(toSave).HasCount().EqualTo(3);
        await Assert.That(toSave.Select(c => c.Id)).IsEquivalentTo(new uint[] { 8, 43, 1000 });
    }

    [Test]
    public async Task GetCharactersToSave_ForceAll_ReturnsEverything()
    {
        var world = new FakeWorldManager();
        var all = MakeCharacters(1000);
        foreach (var c in all)
            c.IsDirty = false;
        world.Characters = all;

        var manager = CreateManager(world);

        var toSave = manager.GetCharactersToSave(true);
        await Assert.That(toSave).HasCount().EqualTo(1000);
    }

    [Test]
    public async Task NewCharacter_IsDirty_ByDefault()
    {
        var character = new Character(new UnitCustomModelParams());
        await Assert.That(character.IsDirty).IsTrue();
    }

    [Test]
    public async Task MarkDirty_SetsFlag()
    {
        var character = new Character(new UnitCustomModelParams()) { IsDirty = false };
        character.MarkDirty();
        await Assert.That(character.IsDirty).IsTrue();
    }

    [Test]
    public async Task MoneyChange_MarksDirty()
    {
        var character = new Character(new UnitCustomModelParams()) { IsDirty = false };
        character.Money += 100;
        await Assert.That(character.IsDirty).IsTrue();
    }

    [Test]
    public async Task HpChange_MarksDirty()
    {
        var character = new Character(new UnitCustomModelParams()) { IsDirty = false };
        character.Hp += 5;
        await Assert.That(character.IsDirty).IsTrue();
    }

    [Test]
    public async Task HpSameValue_DoesNotMarkDirty()
    {
        var character = new Character(new UnitCustomModelParams()) { Hp = 100 };
        character.IsDirty = false;
        character.Hp = 100;
        await Assert.That(character.IsDirty).IsFalse();
    }

    [Test]
    public async Task SetOption_MarksDirty()
    {
        var character = new Character(new UnitCustomModelParams()) { IsDirty = false };
        character.SetOption(1, "test");
        await Assert.That(character.IsDirty).IsTrue();
    }

    /// <summary>
    /// Minimal IWorldManager stub for the dirty-filter tests. Only GetAllCharacters is
    /// exercised by SaveManager.GetCharactersToSave; the rest are inert.
    /// </summary>
    private sealed class FakeWorldManager : IWorldManager
    {
        public WorldInstance MainWorld { get; set; }

        public List<Character> Characters { get; set; } = [];

        public void CreateStaticInstances()
        {
        }

        public WorldInstance CreateWorldInstance(WorldTemplate worldTemplate, uint channelId, bool overrideInstanceId = false, uint fixedInstanceId = 0, Character notifyPlayer = null)
        {
            return null;
        }

        public WorldTemplate CreateWorldTemplate(string worldName)
        {
            return null;
        }

        public Character GetCharacterByObjId(uint id)
        {
            return null;
        }

        public Character GetCharacterById(uint id)
        {
            return null;
        }

        public Character GetCharacter(string name)
        {
            return null;
        }

        public List<Character> GetAllCharacters()
        {
            return Characters;
        }

        public uint GetZoneId(WorldTemplate worldTemplate, float x, float y)
        {
            return 0;
        }

        public WorldTemplate GetWorldTemplateByName(string worldName)
        {
            return null;
        }

        public WorldTemplate GetWorldTemplateByZoneKey(uint zoneKey)
        {
            return null;
        }

        public WorldInstance[] GetWorlds()
        {
            return [];
        }

        public WorldInstance GetWorld(uint worldInstanceId)
        {
            return null;
        }

        public List<uint> GetZoneKeysByWorldId(uint worldId)
        {
            return [];
        }

        public void BroadcastPacketToServer(GamePacket packet)
        {
        }

        public Character GetTargetOrSelf(Character character, string targetName, out int firstNonNameArgument)
        {
            firstNonNameArgument = 0;
            return character;
        }

        public bool TryRemoveCharacter(uint playerObjId)
        {
            return false;
        }

        public void Initialize()
        {
        }

        public void Load()
        {
        }

        public WorldTemplate[] GetAllWorldTemplates()
        {
            return [];
        }
    }
}
