using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// P1 t_61814965 — BotAppearanceFactory: player-like randomized appearance +
/// starting equipment for bot citizens. The factory must mirror the HUMAN
/// create-path shape (CSCreateCharacterPacket → CharacterManager.Create):
/// race/gender-canonical model id, type=Face params within the canonical 1.2
/// catalogs, per-class equipment, NameManager-valid names — and be
/// deterministic per seed (the template-system + rig hook).
/// </summary>
public class BotAppearanceFactoryTests
{
    private static readonly uint[] Model10Hair = [1, 2, 3, 4, 733];
    private static readonly uint[] Model10Skin = [1, 2, 3];
    private static readonly uint[] Model10NormalMaps = [28, 29, 30];
    private static readonly uint[] Model10DecalCat2 = [312, 313];
    private static readonly uint[] Model10DecalCat4 = [560, 561, 562];
    private static readonly uint[] Model10DecalCat5 = [680, 681, 682];
    private static readonly BotMovableDecal[] Model10Movable = [new(900, 50, 60), new(901, -10, 20)];
    private static readonly byte[][] Model10Presets = [Fill(0x11), Fill(0x22), Fill(0x33)];

    private static readonly uint[] Model11Hair = [36, 37, 38];
    private static readonly uint[] Model11Skin = [3, 4];

    /// <summary>Fight pack: headgear + mainhand + 2 supplies (like the real 1.2 data).</summary>
    private static AbilityItems FightPack()
        => new()
        {
            Ability = (byte)AbilityType.Fight,
            Items = new EquipItemsTemplate
            {
                Headgear = 1001, HeadgearGrade = 1,
                Mainhand = 2001, MainhandGrade = 1,
                Shirt = 1002, ShirtGrade = 1,
                Pants = 1003, PantsGrade = 1,
                Shoes = 1004, ShoesGrade = 1
            },
            Supplies = [new AbilitySupplyItem { Id = 3001, Amount = 5, Grade = 1 }]
        };

    private static AbilityItems MagicPack()
        => new()
        {
            Ability = (byte)AbilityType.Magic,
            Items = new EquipItemsTemplate { Mainhand = 2002, MainhandGrade = 1 },
            Supplies = [new AbilitySupplyItem { Id = 3002, Amount = 3, Grade = 1 }]
        };

    private static readonly uint[] NuianMaleBody = [42, 51, 128, 132, 133, 134, 148];
    private static readonly uint[] NuianFemaleBody = [70, 71, 0, 0, 0, 0, 0];

    private static readonly StaticBotAppearanceColorSource StaticColors = new(new Dictionary<uint,
        (uint[] Hair, uint[] Skin, uint[] NormalMaps, List<byte[]> Presets,
        Dictionary<byte, uint[]> FixedDecals, BotMovableDecal[] Movable)>
    {
        [10] = (Model10Hair, Model10Skin, Model10NormalMaps,
            [.. Model10Presets],
            new Dictionary<byte, uint[]> { [2] = Model10DecalCat2, [4] = Model10DecalCat4, [5] = Model10DecalCat5 },
            Model10Movable),
        [11] = (Model11Hair, Model11Skin, [9, 10], [Fill(0x44)],
            new Dictionary<byte, uint[]> { [4] = [135] }, [])
    });

    private static readonly StaticBotStartingEquipmentSource StaticEquipment = new(
        new Dictionary<byte, AbilityItems>
        {
            [(byte)AbilityType.Fight] = FightPack(),
            [(byte)AbilityType.Magic] = MagicPack()
        },
        new Dictionary<(Race, Gender), uint[]>
        {
            [(Race.Nuian, Gender.Male)] = NuianMaleBody,
            [(Race.Nuian, Gender.Female)] = NuianFemaleBody
        });

    private static BotAppearanceFactory NewFactory()
        => new(StaticColors, StaticEquipment);

    private static byte[] Fill(byte value)
    {
        var bytes = new byte[128];
        Array.Fill(bytes, value);
        return bytes;
    }

    // --------------------------------------------------------------- determinism

    [Test]
    public async Task SameSeed_SameName_ProducesByteIdenticalAppearance()
    {
        var factory = NewFactory();
        var spec = new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: 42, Name: "Aurelio");

        var first = factory.Generate(spec);
        var second = factory.Generate(spec);

        await Assert.That(second.Blob.SequenceEqual(first.Blob)).IsTrue();
        await Assert.That(second.Name).IsEqualTo(first.Name);
        await Assert.That(second.ClassAbility).IsEqualTo(first.ClassAbility);
        await Assert.That(second.Equipment.Select(e => e.TemplateId))
            .IsEquivalentTo(first.Equipment.Select(e => e.TemplateId));
    }

    [Test]
    public async Task SameName_NoExplicitSeed_IsReproducible()
    {
        // The name hashes to the seed (FNV-1a) — the template-system hook:
        // a template pinning a name pins the whole look.
        var factory = NewFactory();
        var spec = new BotAppearanceSpec(Race.Nuian, Gender.Male, Name: "Cassian");

        var first = factory.Generate(spec);
        var second = factory.Generate(spec);

        await Assert.That(second.Blob.SequenceEqual(first.Blob)).IsTrue();
        await Assert.That(second.Name).IsEqualTo("Cassian");
    }

    [Test]
    public async Task DifferentSeeds_ProduceDistinctBlobs()
    {
        var factory = NewFactory();
        var seen = new HashSet<string>();

        for (var seed = 0u; seed < 20; seed++)
        {
            var appearance = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: seed));
            seen.Add(Convert.ToHexString(appearance.Blob));
        }

        await Assert.That(seen.Count).IsGreaterThanOrEqualTo(18);
    }

    // --------------------------------------------------------------- validity

    [Test]
    public async Task AllEightRaceGenderCombos_ProduceValidFaceBlob()
    {
        var factory = NewFactory();
        var expectedModels = new Dictionary<(Race, Gender), uint>
        {
            [(Race.Nuian, Gender.Male)] = 10,
            [(Race.Nuian, Gender.Female)] = 11,
            [(Race.Elf, Gender.Male)] = 16,
            [(Race.Elf, Gender.Female)] = 17,
            [(Race.Dwarf, Gender.Male)] = 18,
            [(Race.Dwarf, Gender.Female)] = 19,
            [(Race.Hariharan, Gender.Male)] = 20,
            [(Race.Hariharan, Gender.Female)] = 21
        };

        foreach (var ((race, gender), modelId) in expectedModels)
        {
            var appearance = factory.Generate(new BotAppearanceSpec(race, gender, Seed: 7));

            // Human create-path shape: type=Face + hair/skin/model + full
            // FaceModel = the same 231-byte structure as a real player blob.
            await Assert.That(appearance.Blob.Length).IsEqualTo(231);
            await Assert.That(appearance.Blob[0]).IsEqualTo((byte)UnitCustomModelType.Face);
            await Assert.That(appearance.ModelId).IsEqualTo(modelId);

            // Round-trip: the DB load path (UnitCustomModelParams.Read) parses it.
            var read = new UnitCustomModelParams();
            read.Read((PacketStream)appearance.Blob);
            await Assert.That(read.Write(new PacketStream()).GetBytes().SequenceEqual(appearance.Blob)).IsTrue();
        }
    }

    [Test]
    public async Task HairAndSkinIds_AlwaysWithinValidCatalog()
    {
        var factory = NewFactory();

        for (var seed = 0u; seed < 10; seed++)
        {
            var appearance = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: seed));
            var blob = appearance.Blob;

            // Layout: [type][hair u32][skin u32][model u32][FaceModel...]
            var hair = BitConverter.ToUInt32(blob, 1);
            var skin = BitConverter.ToUInt32(blob, 5);

            await Assert.That(Model10Hair).Contains(hair);
            await Assert.That(Model10Skin).Contains(skin);
        }
    }

    [Test]
    public async Task FacePresetModifier_AlwaysFromValidPresets()
    {
        var factory = NewFactory();

        for (var seed = 0u; seed < 10; seed++)
        {
            var appearance = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: seed));
            var read = new UnitCustomModelParams();
            read.Read((PacketStream)appearance.Blob);

            var matched = Model10Presets.Any(p => p.SequenceEqual(read.Face.Modifier));
            await Assert.That(matched).IsTrue();
        }
    }

    [Test]
    public async Task FixedDecalAssetIds_AlwaysWithinCatalog()
    {
        var factory = NewFactory();
        var valid = Model10DecalCat2.Concat(Model10DecalCat4).Concat(Model10DecalCat5).ToHashSet();

        for (var seed = 0u; seed < 10; seed++)
        {
            var appearance = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: seed));
            var read = new UnitCustomModelParams();
            read.Read((PacketStream)appearance.Blob);

            foreach (var decal in FixedDecalsOf(read.Face))
            {
                if (decal.AssetId == 0)
                    continue; // slot left empty for variety
                await Assert.That(valid.Contains(decal.AssetId)).IsTrue();
            }
        }
    }

    [Test]
    public async Task NormalMapId_AlwaysWithinCatalog_OrZero()
    {
        var factory = NewFactory();

        for (var seed = 0u; seed < 10; seed++)
        {
            var appearance = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: seed));
            var read = new UnitCustomModelParams();
            read.Read((PacketStream)appearance.Blob);

            await Assert.That(read.Face.NormalMapId == 0 || Model10NormalMaps.Contains(read.Face.NormalMapId)).IsTrue();
        }
    }

    // --------------------------------------------------------------- equipment

    [Test]
    public async Task ClassAbility_PinnedBySpec_Respected()
    {
        var factory = NewFactory();
        var appearance = factory.Generate(
            new BotAppearanceSpec(Race.Nuian, Gender.Male, ClassAbility: AbilityType.Magic, Seed: 1));

        await Assert.That(appearance.ClassAbility).IsEqualTo(AbilityType.Magic);
        // Magic pack in the static source carries a mainhand only.
        await Assert.That(appearance.Equipment.Select(e => e.TemplateId)).Contains(2002u);
        await Assert.That(appearance.Equipment.Any(e => e.TemplateId == 1001)).IsFalse();
    }

    [Test]
    public async Task PackBearingClass_HasEquipment_NonPackClass_EmptyCanonical()
    {
        var factory = NewFactory();

        var fight = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: 1));
        await Assert.That(fight.Equipment.Count).IsGreaterThanOrEqualTo(5);
        await Assert.That(fight.Supplies.Count).IsGreaterThanOrEqualTo(1);

        // Will has NO newbie pack in 1.2 — a human of that class starts
        // naked too; the factory must mirror that (canonical, not invented).
        var will = factory.Generate(
            new BotAppearanceSpec(Race.Nuian, Gender.Male, ClassAbility: AbilityType.Will, Seed: 1));
        await Assert.That(will.Equipment).IsEmpty();
    }

    [Test]
    public async Task BodyItems_MirrorRaceTemplate()
    {
        var factory = NewFactory();

        var male = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: 1));
        var female = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Female, Seed: 1));

        await Assert.That(male.BodyItems).IsEquivalentTo(NuianMaleBody);
        await Assert.That(female.BodyItems).IsEquivalentTo(NuianFemaleBody);
    }

    // --------------------------------------------------------------- names

    [Test]
    public async Task NamePool_AllNamesMatchNameManagerRegex()
    {
        // NameManager default: ^[a-zA-Z0-9а-яА-Я]{1,18}$
        var regex = new System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9а-яА-Я]{1,18}$");
        var races = new[] { Race.Nuian, Race.Elf, Race.Dwarf, Race.Hariharan };
        var genders = new[] { Gender.Male, Gender.Female };

        foreach (var race in races)
        foreach (var gender in genders)
        {
            var names = BotNamePool.For(race, gender);
            await Assert.That(names.Count).IsGreaterThanOrEqualTo(16);
            foreach (var name in names)
                await Assert.That(regex.IsMatch(name)).IsTrue();
        }
    }

    [Test]
    public async Task GeneratedName_ComesFromRacePool()
    {
        var factory = NewFactory();
        var pool = BotNamePool.For(Race.Nuian, Gender.Male).ToHashSet();

        for (var seed = 0u; seed < 10; seed++)
        {
            var appearance = factory.Generate(new BotAppearanceSpec(Race.Nuian, Gender.Male, Seed: seed));
            await Assert.That(pool.Contains(appearance.Name)).IsTrue();
        }
    }

    // --------------------------------------------------------------- real data

    [Test]
    public async Task SqliteSource_AllEightModels_NonEmptyCatalogs()
    {
        // Canonical 1.2 data (compact.sqlite3) must cover every creatable
        // model — this is the runtime source the production factory uses.
        // Skips silently when the data file is absent (bare worker clone).
        var source = new SqliteBotAppearanceColorSource();
        var models = new uint[] { 10, 11, 16, 17, 18, 19, 20, 21 };

        foreach (var modelId in models)
        {
            try
            {
                await Assert.That(source.GetHairColorIds(modelId).Count).IsGreaterThan(0);
                await Assert.That(source.GetSkinColorIds(modelId).Count).IsGreaterThan(0);
                await Assert.That(source.GetNormalMapIds(modelId).Count).IsGreaterThan(0);
                await Assert.That(source.GetFacePresetModifiers(modelId).Count).IsGreaterThan(0);
            }
            catch (System.IO.FileNotFoundException)
            {
                return; // data file missing — nothing to verify against
            }
        }
    }

    [Test]
    public async Task SqliteSource_ModelsMatchCanonicalModelIdMapping()
    {
        // The factory's canonical model ids must match the live characters
        // table (guards fork drift).
        try
        {
            using var connection = AAEmu.Game.Utils.DB.SQLite.CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT model_id, char_race_id, char_gender_id FROM characters WHERE creatable=1";
            cmd.Prepare();
            using var reader = cmd.ExecuteReader();
            var mapped = new Dictionary<(Race, Gender), uint>();
            while (reader.Read())
            {
                var modelId = Convert.ToUInt32(reader.GetValue(0));
                var race = (Race)Convert.ToByte(reader.GetValue(1));
                var gender = (Gender)Convert.ToByte(reader.GetValue(2));
                mapped[(race, gender)] = modelId;
            }

            foreach (var ((race, gender), dbModelId) in mapped)
                await Assert.That(BotAppearanceDefaults.ModelIdFor(race, gender)).IsEqualTo(dbModelId);
        }
        catch (System.IO.FileNotFoundException)
        {
            return; // data file missing — nothing to verify against
        }
    }

    // --------------------------------------------------------------- helpers

    /// <summary>Reflection read of FaceModel's private fixed-decal slot array.</summary>
    private static IReadOnlyList<FixedDecalAsset> FixedDecalsOf(FaceModel face)
    {
        var field = typeof(FaceModel).GetField("FixedDecalAsset",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(face) as FixedDecalAsset[] ?? [];
    }
}
