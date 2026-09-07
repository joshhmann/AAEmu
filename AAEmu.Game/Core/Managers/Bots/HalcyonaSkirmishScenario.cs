using System.Numerics;

using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Halcyona skirmish (Q8 WAR-HONOR acceptance seed): a Nuia-alliance fireteam
/// vs a Haranya-alliance fireteam fighting REAL casts through the REAL
/// Character.DoDie honor path inside a War-zone conflict.
/// Canonical alliance hostility (Nuia 148 ↔ Haranya 149 = Hostile, compact
/// system_faction_relations row 907, state 1) — no fixture relation invented.
///
/// Composition only: GameplayActor.Cast + CharacterResurrection.DoDie-driven
/// honor, driven by ordinary Characters through normal gameplay services
/// (AGENTS.md #9). No parallel combat, damage, or honor implementation.
/// Fighter HP is a documented skirmish fixture (short waves, same for both
/// sides); damage, death, resurrection, and honor math stay engine-real.
/// </summary>
public static class HalcyonaSkirmishScenario
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "halcyona-skirmish";

    /// <summary>
    /// Fixture skirmish skill: Triple-Slash-shaped fixed-damage inline skill.
    /// A custom id with no canonical requirement rows (real 18131 requires
    /// Battlerage ability via unit_reqs, unusable headless) — same melee
    /// hostile-target shape, deterministic 100-damage hits.
    /// </summary>
    public const uint DefaultCastSkillId = 918181u;

    /// <summary>Canonical alliance roots (FactionsEnum; relation row 907 = Hostile).</summary>
    public static readonly FactionsEnum NuiaAlliance = (FactionsEnum)148;
    public static readonly FactionsEnum HaranyaAlliance = (FactionsEnum)149;

    /// <summary>Fixture wave HP so waves resolve in a bounded number of casts (same both sides).</summary>
    public const int SkirmishWaveHp = 150;

    /// <summary>Scenario parameters.</summary>
    public sealed record SkirmishOptions
    {
        public uint CastSkillId { get; init; } = DefaultCastSkillId;
        public int MaxRounds { get; init; } = 120;
        public bool RespawnFallen { get; init; } = true;
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class SkirmishResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public int Rounds { get; init; }
        public int NuiaKills { get; init; }
        public int HaranyaKills { get; init; }
        public Dictionary<uint, uint> HonorGained { get; init; } = new();
        public string FailReason { get; init; } = "";
    }

    /// <summary>
    /// Builds the two alliance factions with mutual canonical Hostile
    /// relations (mirrors FactionManager.Load lines 85-88, no DB needed).
    /// </summary>
    public static (SystemFaction Nuia, SystemFaction Haranya) SeedAllianceFactions()
    {
        // MotherId = self: GetRelationState collapses MotherId-0 pairs to
        // Friendly, so each alliance names itself as its own root and the
        // mutual Hostile entries below decide (canonical row 907, state 1).
        var nuia = new SystemFaction { Id = NuiaAlliance, MotherId = NuiaAlliance };
        var haranya = new SystemFaction { Id = HaranyaAlliance, MotherId = HaranyaAlliance };
        var nuiaToHaranya = new FactionRelation { Id = NuiaAlliance, Id2 = HaranyaAlliance, State = RelationState.Hostile };
        var haranyaToNuia = new FactionRelation { Id = HaranyaAlliance, Id2 = NuiaAlliance, State = RelationState.Hostile };
        nuia.Relations.Add(HaranyaAlliance, nuiaToHaranya);
        haranya.Relations.Add(NuiaAlliance, haranyaToNuia);
        return (nuia, haranya);
    }

    private static float Distance(Character a, Character b)
    {
        var pa = a.Transform.World.Position;
        var pb = b.Transform.World.Position;
        var dx = pa.X - pb.X;
        var dy = pa.Y - pb.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static GameplayActor? NearestFoe(GameplayActor fighter, IReadOnlyList<GameplayActor> foes)
    {
        GameplayActor? best = null;
        var bestDist = float.MaxValue;
        foreach (var foe in foes)
        {
            if (foe.Character.IsDead)
                continue;
            var d = Distance(fighter.Character, foe.Character);
            if (d < bestDist)
            {
                bestDist = d;
                best = foe;
            }
        }
        return best;
    }

    private static void ReviveWave(IEnumerable<GameplayActor> team)
    {
        foreach (var fighter in team)
        {
            if (!fighter.Character.IsDead)
                continue;
            CharacterResurrection.Resurrect(fighter.Character, inPlace: true);
            fighter.Character.Hp = SkirmishWaveHp;
        }
    }

    /// <summary>
    /// Runs the skirmish: every living fighter casts once per round at its
    /// nearest living foe through the real skill pipeline; deaths flow through
    /// the real DoDie honor path; the fallen resurrect between waves when
    /// enabled. Returns kill/honor totals per side with per-character deltas.
    /// </summary>
    public static SkirmishResult Run(
        IReadOnlyList<GameplayActor> nuia,
        IReadOnlyList<GameplayActor> haranya,
        SkirmishOptions? options = null)
    {
        options ??= new SkirmishOptions();
        var (nuiaFaction, haranyaFaction) = SeedAllianceFactions();
        foreach (var a in nuia)
        {
            a.Character.Faction = nuiaFaction;
            a.Character.Hp = SkirmishWaveHp;
        }
        foreach (var a in haranya)
        {
            a.Character.Faction = haranyaFaction;
            a.Character.Hp = SkirmishWaveHp;
        }

        var honorBefore = nuia.Concat(haranya).ToDictionary(a => a.Character.Id, a => a.Character.HonorGainedInCombat);
        var deadCounted = new HashSet<uint>();
        var nuiaKills = 0;
        var haranyaKills = 0;
        var rounds = 0;

        void CountFreshDeaths()
        {
            foreach (var a in nuia)
            {
                if (a.Character.IsDead && deadCounted.Add(a.Character.ObjId))
                    haranyaKills++;
            }
            foreach (var a in haranya)
            {
                if (a.Character.IsDead && deadCounted.Add(a.Character.ObjId))
                    nuiaKills++;
            }
        }

        while (rounds < options.MaxRounds)
        {
            rounds++;
            var order = nuia.Concat(haranya).Where(f => !f.Character.IsDead).ToList();
            foreach (var fighter in order)
            {
                if (fighter.Character.IsDead)
                    continue;
                var foes = nuia.Contains(fighter) ? haranya : nuia;
                var target = NearestFoe(fighter, foes);
                if (target == null)
                    break;
                fighter.Character.CurrentTarget = target.Character;
                fighter.Cast(options.CastSkillId, target.Character.ObjId);
                CountFreshDeaths();
            }
            CountFreshDeaths();

            var nuiaAlive = nuia.Any(f => !f.Character.IsDead);
            var haranyaAlive = haranya.Any(f => !f.Character.IsDead);
            if (!nuiaAlive || !haranyaAlive)
            {
                if (!options.RespawnFallen)
                    break;
                ReviveWave(nuia);
                ReviveWave(haranya);
                deadCounted.Clear();
            }
        }

        var honor = nuia.Concat(haranya).ToDictionary(a => a.Character.Id, a => a.Character.HonorGainedInCombat - honorBefore[a.Character.Id]);
        Logger.Info("[{Scenario}] {Rounds} rounds: Nuia {NuiaKills} — Haranya {HaranyaKills} kills",
            ScenarioName, rounds, nuiaKills, haranyaKills);
        return new SkirmishResult
        {
            Scenario = ScenarioName,
            Passed = nuiaKills > 0 && haranyaKills > 0,
            FailStage = nuiaKills > 0 && haranyaKills > 0 ? "" : "SKIRMISH",
            Rounds = rounds,
            NuiaKills = nuiaKills,
            HaranyaKills = haranyaKills,
            HonorGained = honor,
            FailReason = nuiaKills > 0 && haranyaKills > 0 ? "" : $"one-sided: Nuia {nuiaKills} / Haranya {haranyaKills} in {rounds} rounds",
        };
    }
}
