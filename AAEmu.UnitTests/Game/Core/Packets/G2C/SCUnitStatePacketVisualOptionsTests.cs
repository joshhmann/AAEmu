using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

/// <summary>
/// SCUnitStatePacket null-VisualOptions defect (P0 hotfix t_506a9acb): bots
/// provisioned through the production HeadlessSession path never receive a
/// client spawn packet, so CSSpawnCharacterPacket's
/// `ActiveChar.VisualOptions = new CharacterVisualOptions()` assignment never
/// runs — Character.VisualOptions stays null (auto-property, Character.cs:190)
/// and SCUnitStatePacket.Write :324 NREs while serializing the bot to a REAL
/// client (prod CT 133, 2026-08-08 15:42:43, Asssaa entering region 324, ×3
/// PacketStream errors). The EmptyStp fix (59c0fb9b) covers null-Stp INSIDE a
/// non-null VisualOptions; this rig covers the whole-object-null case.
/// </summary>
public class SCUnitStatePacketVisualOptionsTests
{
    /// <summary>
    /// Seeds the DI singletons SCUnitStatePacket.Write touches for a
    /// Character. Per-singleton missing-only guards (never replaces an
    /// established singleton — full-suite discipline, t_4f11a519), and
    /// dict fields are only FILLED when null so a pre-seeded real manager
    /// (pilot rig) is never clobbered:
    ///  - SkillManager (no parameterless ctor): the NetBuff region adds the
    ///    patron/AH license buffs (8000011/8000012) unconditionally, so
    ///    _buffs must carry minimal templates and _buffTags/_buffTriggers
    ///    must be dicts (TryGetValue paths);
    ///  - EffectTaskManager (no parameterless ctor): ScheduleEffect →
    ///    AddDispelTask schedules via ITaskManager (mock — no real ticker).
    /// </summary>
    private static void SeedPacketSurface()
    {
        if (!SingletonSeeded(typeof(Singleton<SkillManager>)))
        {
            SeedSingleton(typeof(Singleton<SkillManager>),
                new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object));
        }

        var manager = SkillManager.Instance;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        // Seed every dictionary field (the buff path touches _buffs,
        // _buffTags, _buffTriggers, _skillModifiers, _combatBuffs — the skill
        // path _skills/_skillTags/etc. — all TryGetValue/GetValueOrDefault
        // on null dicts NREs). Only fills when null; never replaces.
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
        // The NetBuff region adds the patron/AH license buffs
        // (8000011/8000012) unconditionally — minimal templates so the
        // AddBuff path (Template.Id/StackRule/GetDuration) has real data.
        var buffs = (Dictionary<uint, BuffTemplate>)typeof(SkillManager).GetField("_buffs", flags)!.GetValue(manager)!;
        foreach (var id in new[] { 8000011u, 8000012u })
        {
            if (!buffs.ContainsKey(id))
                buffs[id] = new BuffTemplate { Id = id, Duration = 1, Kind = BuffKind.Good };
        }

        // BuffModifiers/CombatBuffs paths consult BuffGameData (parameterless
        // ctor — auto-creates; dicts stay null until Load()). Same fill-if-
        // null treatment for its dictionary fields.
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

    /// <summary>
    /// Builds a character in the production bot shape: every sub-object
    /// SCUnitStatePacket.Write dereferences for a Character (Skills,
    /// Appellations, Abilities — the BaseUnit/Unit/GameObject ctors supply
    /// Buffs, CombatBuffs, Transform, Equipment, ModelParams) EXCEPT
    /// VisualOptions, which stays null exactly like a headless-provisioned
    /// bot (mirrors the HeadlessSession.Create/BuildProvisionedCharacter
    /// wiring minus the fix).
    /// </summary>
    private static Character BuildBotCharacter()
    {
        var character = new Character(new UnitCustomModelParams())
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
        return character;
    }

    private static byte[] Serialize(Character character)
    {
        SeedPacketSurface();
        var packet = new SCUnitStatePacket(character);
        return packet.Write(new PacketStream()).GetBytes();
    }

    [Test]
    public async Task Write_NullVisualOptions_SerializesWithoutNre()
    {
        // Was NRE: character.VisualOptions.WriteOptions(stream)
        // (SCUnitStatePacket.cs:324) — VisualOptions is null on bots created
        // via the production HeadlessSession.Provision path (no client-supplied
        // visual options, no spawn packet).
        var bot = BuildBotCharacter();

        var body = Serialize(bot);

        await Assert.That(body.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Write_NullVisualOptions_WireEqualsFreshInstance()
    {
        // The null-safe backstop must not change the wire: serializing the SAME
        // bot character with VisualOptions null (backstop → Default) and then
        // with a fresh instance must produce byte-identical SCUnitStatePacket
        // output. The patron/AH license buffs (8000011/8000012) that
        // SCUnitStatePacket.Write would add are pre-added with pinned
        // StartTime so buff time fields (GetTimeElapsed) can't drift between
        // the two serializations — any difference is then exactly the
        // visual-options block.
        SeedPacketSurface();
        var bot = BuildBotCharacter();
        foreach (var id in new[] { 8000011u, 8000012u })
        {
            var template = SkillManager.Instance.GetBuffTemplate(id);
            bot.Buffs.AddBuff(new Buff(bot, bot, SkillCaster.GetByType(SkillCasterType.Unit), template, null, DateTime.UtcNow));
        }
        var effectsField = typeof(Buffs).GetField("_effects",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        foreach (var buff in (List<Buff>)effectsField!.GetValue(bot.Buffs)!)
            buff.StartTime = DateTime.UnixEpoch;

        var nullBody = Serialize(bot);
        bot.VisualOptions = new CharacterVisualOptions();
        var withBody = Serialize(bot);

        await Assert.That(nullBody.SequenceEqual(withBody)).IsTrue();
    }
}
