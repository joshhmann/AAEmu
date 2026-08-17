using System.Text;

using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.3 EXIT scenario rig test (t_c73d6293, REQ-M5.3-11) — drives the
/// curated core-surface segment (observe → move → stop → target → cast)
/// on the canonical rig and emits the MACHINE-READABLE trace:
/// scorecard-explorations/generated/m5.3-core-surface-exit.jsonl — one
/// ActorAuditRecord.ToJson() per line (every request, transition, result,
/// failure), plus a human evidence block appended to
/// scorecard-explorations/generated/m5.3-core-surface-exit.md.
///
/// Deterministic evidence (no wall-clock in the headers; the JSONL lines
/// carry the real server timestamps from the audit records — those ARE the
/// trace). H stays UNKNOWN — scripted evidence is proxy only.
/// </summary>
[NotInParallel]
public class M53CoreSurfaceExitScenarioRigTests
{
    /// <summary>Fixture world adapter: spawns the target NPC on demand.</summary>
    internal sealed class FixtureWorldAdapter : BotScenarioRunner.IScenarioWorldAdapter
    {
        private readonly AAEmu.Game.Models.Game.Bots.HeadlessSession _session;

        public FixtureWorldAdapter(AAEmu.Game.Models.Game.Bots.HeadlessSession session) => _session = session;

        public uint ResolveNpcObjId(uint npcTemplateId) => _session.SpawnNpc(npcTemplateId);

        public uint ResolveDoodadObjId(uint doodadTemplateId) => _session.SpawnDoodad(doodadTemplateId);
    }

    /// <summary>
    /// E11: the scripted actor completes the full segment on the canonical
    /// rig, all criteria green, and the machine-readable trace is written.
    /// </summary>
    [Test]
    public async Task M53Exit_ObserveMoveStopTargetCast_CompletesWithMachineReadableTrace()
    {
        GameplayActorTestRig.Seed();
        var (_, session) = GameplayActorTestRig.CreateActor("m53exit");

        var result = M53CoreSurfaceExitScenario.Run(session.Character, new FixtureWorldAdapter(session));

        WriteTraceEvidence(result);

        await Assert.That(result.Passed, "M5.3 exit scenario FAILED:\n" + result.Evidence()).IsTrue();
        await Assert.That(result.FailStage, "no fail stage on a pass").IsEmpty();

        // The five actions in exact segment order (IsEquivalentTo is the
        // TUnit contents matcher; order is pinned below by the trace).
        var actions = result.TraceRecords.Select(r => r.Action).ToList();
        await Assert.That(actions).IsEquivalentTo(
            new[]
            {
                ActorActionType.Observe,
                ActorActionType.Move,
                ActorActionType.Stop,
                ActorActionType.Target,
                ActorActionType.Cast
            });

        // Every request/transition/result/failure is in the trace records.
        await Assert.That(result.TraceRecords.Count).IsEqualTo(5);
        foreach (var record in result.TraceRecords)
        {
            await Assert.That(record.StateChanges.Count >= 3).IsTrue();
            await Assert.That(record.TraceId).IsNotEqualTo(Guid.Empty);
            await Assert.That(record.ActorId).IsEqualTo(session.Character.ObjId);
        }

        // All criteria green (the evidence block carries the verdicts).
        var failed = result.Criteria.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Detail).ToList();
        await Assert.That(failed, "all exit criteria must pass: " + string.Join("; ", failed)).IsEmpty();
    }

    private static bool s_evidenceInitialized;
    private static readonly object s_evidenceLock = new();

    /// <summary>
    /// Writes the machine-readable JSONL trace (one audit record per line —
    /// the M5 {trace_id, actor_id, action, target_id, requested_at,
    /// started_at, completed_at, result, state_changes} shape) plus the
    /// human evidence block. Worktree-tolerant repo-root discovery (a .git
    /// FILE counts — worktrees; a .git DIRECTORY counts — real clones).
    /// </summary>
    internal static void WriteTraceEvidence(BotScenarioRunner.ScenarioRunResult result)
    {
        lock (s_evidenceLock)
        {
            var repoRoot = RepoRoot();
            var jsonlPath = Path.Combine(repoRoot, "scorecard-explorations", "generated", "m5.3-core-surface-exit.jsonl");
            var mdPath = Path.Combine(repoRoot, "scorecard-explorations", "generated", "m5.3-core-surface-exit.md");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);

            // Machine-readable trace: one JSON object per request line.
            var sb = new StringBuilder();
            foreach (var record in result.TraceRecords)
                sb.AppendLine(record.ToJson());
            File.WriteAllText(jsonlPath, sb.ToString());

            // Human evidence block.
            var md = new StringBuilder();
            if (!s_evidenceInitialized)
            {
                s_evidenceInitialized = true;
                if (File.Exists(mdPath))
                    File.Delete(mdPath);
                md.AppendLine("# M5.3 core-surface EXIT scenario (t_c73d6293, REQ-M5.3-11)");
                md.AppendLine();
                md.AppendLine("> Generated by M53CoreSurfaceExitScenarioRigTests (deterministic — no wall-clock).");
                md.AppendLine("> Machine-readable trace: `m5.3-core-surface-exit.jsonl` (one ActorAuditRecord per line).");
                md.AppendLine("> Scripted actor completes observe → move → stop → target → cast through the REAL engine");
                md.AppendLine("> paths (WorldManager queries, movement pipeline, Unit.CurrentTarget, Character.UseSkill).");
                md.AppendLine("> H stays UNKNOWN — proxy/bot-functional evidence only.");
                md.AppendLine();
            }
            md.AppendLine("```");
            md.AppendLine(result.Evidence());
            md.AppendLine("```");
            md.AppendLine();
            File.AppendAllText(mdPath, md.ToString());
            Console.WriteLine("m5.3 exit trace written to " + jsonlPath);
        }
    }

    /// <summary>Worktree-tolerant repo root (M1M2 pattern; accepts .git dir OR file).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }
}
