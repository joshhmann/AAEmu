using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// P0 hotfix t_76730833 — invisible bot bodies (name tags render, the
/// character mesh does not). Rig evidence chain:
///
///  FAIL-BEFORE (prod + code): headless provisioning builds the character
///  with a bare <c>new UnitCustomModelParams()</c> — type=None — which
///  serializes to the 1-byte blob <c>00</c>. Prod rows Citizen01-03 carry
///  exactly that (LENGTH(unit_model_params)=1, HEX=00) while a real human
///  row (Asssaa, id 6) carries 231 bytes (type=Face, hair 733, skin 1,
///  model 0 + full FaceModel). The 1.2 client cannot build a character
///  mesh from empty custom model params — SCUnitStatePacket writes the
///  params block inline (SCUnitStatePacket.cs:152) and the NPC path
///  already forces SetType(Skin) when a unit has no body parts
///  (SCUnitStatePacket.cs:406) — the client needs at least hair/skin/model
///  data to render ANY body.
///
///  PASS-AFTER: BotAppearanceDefaults.BuildDefault(race, gender, modelId)
///  reproduces the human create-path shape (type=Face + canonical per-model
///  hair/skin ids + full FaceModel) — serializes to the same 231-byte
///  structure as the proven human blob.
/// </summary>
public class BotAppearanceDefaultsTests
{
    // ------------------------------------------------------------------ fail-before evidence

    [Test]
    public async Task EmptyModelParams_SerializesToSingleZeroByte_FailBefore()
    {
        // The pre-hotfix provisioning shape: a bare UnitCustomModelParams
        // writes ONLY the type byte (None=0). This is the exact 1-byte blob
        // found in prod rows Citizen01-03.
        var bytes = new UnitCustomModelParams().Write(new PacketStream()).GetBytes();

        await Assert.That(bytes.Length).IsEqualTo(1);
        await Assert.That(bytes[0]).IsEqualTo((byte)UnitCustomModelType.None);
    }

    [Test]
    public async Task IsDegenerate_EmptyParams_True_FailBefore()
    {
        await Assert.That(BotAppearanceDefaults.IsDegenerate(new UnitCustomModelParams())).IsTrue();
    }

    // ------------------------------------------------------------------ pass-after: default appearance

    [Test]
    public async Task BuildDefault_NuianMale_SerializesFullFaceBlob()
    {
        // Model 10 = Nuian male (canonical: hair 1, skin 1). Must serialize
        // the same STRUCTURE as the prod human blob: type=Face + hair(4) +
        // skin(4) + model(4) + FaceModel(20+32+16+20+2+128) = 231 bytes.
        var modelParams = BotAppearanceDefaults.BuildDefault(Race.Nuian, Gender.Male, 10);

        var bytes = modelParams.Write(new PacketStream()).GetBytes();

        await Assert.That(bytes.Length).IsEqualTo(231);
        await Assert.That(bytes[0]).IsEqualTo((byte)UnitCustomModelType.Face);

        // Round-trip: the DB blob path reads back through Read().
        var stream = (PacketStream)bytes;
        var read = new UnitCustomModelParams();
        read.Read(stream);
        await Assert.That(read.Write(new PacketStream()).GetBytes().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    public async Task BuildDefault_AllRaces_SerializeFaceStructure_AndCanonicalIds()
    {
        // Every creatable race/gender (compact.sqlite3 characters table,
        // model ids 10/11/16/17/18/19/20/21) gets a Face-type blob with
        // canonical (non-zero) hair/skin ids — never the degenerate 1-byte
        // shape.
        var models = new (Race Race, Gender Gender, uint ModelId)[]
        {
            (Race.Nuian, Gender.Male, 10),
            (Race.Nuian, Gender.Female, 11),
            (Race.Elf, Gender.Male, 16),
            (Race.Elf, Gender.Female, 17),
            (Race.Hariharan, Gender.Male, 18),
            (Race.Hariharan, Gender.Female, 19),
            (Race.Ferre, Gender.Male, 20),
            (Race.Ferre, Gender.Female, 21)
        };

        foreach (var (race, gender, modelId) in models)
        {
            var bytes = BotAppearanceDefaults.BuildDefault(race, gender, modelId)
                .Write(new PacketStream()).GetBytes();

            await Assert.That(bytes.Length).IsEqualTo(231);
            await Assert.That(bytes[0]).IsEqualTo((byte)UnitCustomModelType.Face);
            // hair color id (bytes 1-4) and skin color id (bytes 5-8) non-zero
            await Assert.That(BitConverter.ToUInt32(bytes, 1) > 0).IsTrue();
            await Assert.That(BitConverter.ToUInt32(bytes, 5) > 0).IsTrue();
        }
    }

    [Test]
    public async Task IsDegenerate_DefaultParams_False_PassAfter()
    {
        await Assert.That(BotAppearanceDefaults.IsDegenerate(
            BotAppearanceDefaults.BuildDefault(Race.Nuian, Gender.Male, 10))).IsFalse();
    }

    // ------------------------------------------------------------------ wire-level: SCUnitStatePacket carries the params

    [Test]
    public async Task SCUnitStatePacket_BotWithDefaultParams_WireGrowsByParamsBlock()
    {
        // The packet writes ModelParams inline (SCUnitStatePacket.cs:152).
        // A bot that had the degenerate 1-byte blob and now carries the
        // 231-byte default must produce a packet exactly 230 bytes longer —
        // proving the appearance data actually reaches the client stream.
        SeedPacketSurface();

        var degenerate = BuildBotCharacter(new UnitCustomModelParams());
        var defaulted = BuildBotCharacter(BotAppearanceDefaults.BuildDefault(Race.Nuian, Gender.Male, 10));

        var degenerateBody = Serialize(degenerate);
        var defaultedBody = Serialize(defaulted);

        await Assert.That(defaultedBody.Length - degenerateBody.Length).IsEqualTo(230);
        // The params block now starts with the Face type byte.
        await Assert.That(defaultedBody.Length).IsGreaterThan(degenerateBody.Length);
    }

    // ------------------------------------------------------------------ helpers (mirrors SCUnitStatePacketVisualOptionsTests)

    private static Character BuildBotCharacter(UnitCustomModelParams modelParams)
    {
        var character = new Character(modelParams)
        {
            Id = 1,
            Name = "Citizen01",
            Level = 1,
            Race = Race.Nuian,
            Gender = Gender.Male
        };
        character.Skills = new CharacterSkills(character);
        character.Appellations = new CharacterAppellations(character);
        character.Abilities = new CharacterAbilities(character);
        character.VisualOptions = new CharacterVisualOptions();
        return character;
    }

    private static byte[] Serialize(Character character)
    {
        var packet = new SCUnitStatePacket(character);
        return packet.Write(new PacketStream()).GetBytes();
    }

    private static void SeedPacketSurface()
    {
        if (!SingletonSeeded(typeof(Singleton<SkillManager>)))
        {
            SeedSingleton(typeof(Singleton<SkillManager>),
                new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object));
        }

        var manager = SkillManager.Instance;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        foreach (var field in typeof(SkillManager).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(manager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(manager, Activator.CreateInstance(dictType));
            }
        }

        var buffs = (Dictionary<uint, BuffTemplate>)typeof(SkillManager).GetField("_buffs", flags)!.GetValue(manager)!;
        foreach (var id in new[] { 8000011u, 8000012u })
        {
            if (!buffs.ContainsKey(id))
                buffs[id] = new BuffTemplate { Id = id, Duration = 1, Kind = BuffKind.Good };
        }

        var buffGameData = BuffGameData.Instance;
        foreach (var field in typeof(BuffGameData).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(buffGameData) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(buffGameData, Activator.CreateInstance(dictType));
            }
        }

        if (!SingletonSeeded(typeof(Singleton<EffectTaskManager>)))
        {
            SeedSingleton(typeof(Singleton<EffectTaskManager>),
                new EffectTaskManager(Mock.Of<ITaskManager>().Object));
        }
    }

    private static bool SingletonSeeded(Type singletonBase)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) != null;

    private static void SeedSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) == null)
            field.SetValue(null, instance);
    }
}
