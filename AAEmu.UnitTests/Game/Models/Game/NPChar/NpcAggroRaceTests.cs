using System.Collections.Concurrent;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

/// <summary>
/// Regression for the live DoDie aggro-table race (presence demo:
/// `Effect ... threw on target` with InvalidOperationException "Operations
/// that change non-concurrent collections must have exclusive access" from
/// Unit.ClearAggroOfUnit via Npc.DoDie): concurrent killing blows for the
/// same NPC raced the death-block aggro clear against ongoing damage aggro
/// over plain dictionaries (Character.IsInAggroListOf, assault lists).
/// ClearAggroOfUnit and the AddUnitAggro mutation sequences now share the
/// static <c>Unit.AggroLock</c> (a per-unit lock proved insufficient: the
/// victim's list is mutated from many distinct attacker locks; AggroTable
/// itself was already a ConcurrentDictionary; Aggro entries self-lock).
///
/// Shape matters: the victim's aggro-list dictionary only resizes while it
/// grows, so the hammer storms it with many DISTINCT attacker NPCs released
/// at once — cold table, concurrent growth plus a repeated death clear.
/// </summary>
[NotInParallel]
public class NpcAggroRaceTests
{
    private const int CorpseCount = 24;
    private const uint CorpseTemplateBase = 92_101;
    private const uint KillerTemplateId = 92_200;
    private const int ChurnIters = 150;
    private const int DeathIters = 25;

    [Test]
    public async Task DoDie_ConcurrentKillingBlows_ClearsAggroWithoutThrow()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();

        var (_, session) = GameplayActorTestRig.CreateActor("aggro-host");
        var (victimActor, _) = GameplayActorTestRig.CreateActor("aggro-victim");
        GameplayActorTestRig.JoinActorWorld(session, victimActor);
        var victim = victimActor.Character;
        SeedEmptyLootMapping();

        var corpses = new List<Npc>();
        for (var i = 0; i < CorpseCount; i++)
        {
            var corpse = session.World.GetNpc(session.SpawnNpc(CorpseTemplateBase + (uint)i));
            corpse.CharacterTagging = new Tagging(corpse);
            corpse.Buffs = new Buffs(corpse);
            corpse.Template = new NpcTemplate();
            AttachToWorld(session, corpse);
            corpse.Hp = 1;
            corpses.Add(corpse);
        }

        // NPC killer: skips the character-only XP/mate branches of DoDie so
        // the hammer exercises the death-block aggro clear itself.
        var npcKiller = session.World.GetNpc(session.SpawnNpc(KillerTemplateId));
        npcKiller.CharacterTagging = new Tagging(npcKiller);
        npcKiller.Buffs = new Buffs(npcKiller);
        npcKiller.Template = new NpcTemplate();
        AttachToWorld(session, npcKiller);

        var errors = new ConcurrentBag<Exception>();
        using var gate = new ManualResetEventSlim(false);
        var tasks = new List<Task>();
        // Damage aggro storm: every corpse adds the shared victim (distinct
        // keys per corpse grow the victim's aggro-list dictionary).
        foreach (var corpse in corpses)
        {
            tasks.Add(Task.Run(() =>
            {
                gate.Wait();
                try
                {
                    for (var i = 0; i < ChurnIters; i++)
                        corpse.AddUnitAggro(AggroKind.Damage, victim, 10);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }));
        }
        // Repeated killing blows on corpse 0 racing the storm.
        var doomed = corpses[0];
        tasks.Add(Task.Run(() =>
        {
            gate.Wait();
            try
            {
                for (var i = 0; i < DeathIters; i++)
                    doomed.DoDie(npcKiller, KillReason.Damage);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }));
        gate.Set();
        await Task.WhenAll(tasks);

        await Assert.That(errors).IsEmpty();

        // No writers left: a final death clear must drop the doomed corpse's
        // key from the victim's list.
        doomed.DoDie(npcKiller, KillReason.Damage);
        await Assert.That(victim.IsInAggroListOf.ContainsKey(doomed.ObjId)).IsFalse();
    }

    /// <summary>
    /// Same headless registry bypass as <see cref="GameplayActorTestRig.JoinActorWorld"/>:
    /// pre-set the Transform _instanceId / GameObject _parentWorld backing
    /// fields so assignment no-ops instead of re-entering the shared
    /// WorldManager registry (which has no such world headless).
    /// </summary>
    private static void AttachToWorld(HeadlessSession session, Npc npc)
    {
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", Flags)!
            .SetValue(npc.Transform, session.World.Id);
        typeof(AAEmu.Game.Models.Game.World.GameObject)
            .GetField("_parentWorld", Flags)!
            .SetValue(npc, session.World);
    }

    /// <summary>
    /// The loot-pack NPC mapping is null until DB load; GenerateLoot (run by
    /// DoDie) NREs on it. An empty mapping returns before any loot work.
    /// </summary>
    private static void SeedEmptyLootMapping()
    {
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var field = typeof(ItemManager).GetField("_lootPackDroppingNpc", Flags)!;
        field.SetValue(ItemManager.Instance, new Dictionary<uint, List<LootPackDroppingNpc>>());
    }
}
