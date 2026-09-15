using System.Numerics;
using System.Text.Json;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

public sealed class PlayerTraceSequentialParallelLimit : IParallelLimit
{
    public int Limit => 1;
}

/// <summary>
/// Unit tests for PlayerTraceService:
/// Verifies trace lifecycle, single-character filtering, movement sampling throttling,
/// zero-cost disabled behavior, mark emission, and JSONL schema formatting.
/// </summary>
[ParallelLimiter<PlayerTraceSequentialParallelLimit>]
[NotInParallel]
public class PlayerTraceServiceTests
{
    private static readonly SemaphoreSlim TestGate = new(1, 1);

    [Test]
    public async Task IsActive_WhenNotStarted_ReturnsFalse()
    {
        await TestGate.WaitAsync();
        try
        {
            var service = PlayerTraceService.Instance;
            if (service.IsActive)
            {
                service.StopTrace();
            }

            await Assert.That(service.IsActive).IsFalse();
        }
        finally
        {
            TestGate.Release();
        }
    }

    [Test]
    public async Task DisabledTracing_CallsDoNotThrowAndProduceNoEvents()
    {
        await TestGate.WaitAsync();
        try
        {
            var service = PlayerTraceService.Instance;
            if (service.IsActive)
            {
                service.StopTrace();
            }

            var character = new Character(new UnitCustomModelParams()) { Id = 999, Name = "TestChar" };

            // None of these should throw or change state when inactive
            service.RecordMovementSample(character, true, Vector3.One, 0.5f, Vector3.Zero);
            service.RecordSkill(character, "skill_requested", 1234);
            service.RecordInteraction(character, "doodad_use", 555, "Doodad");
            service.RecordWorld(character, "doodad_created", 1, 1482);
            service.RecordResource(character, "labor_delta", "labor", -5, 95);
            service.RecordMark("checkpoint");

            await Assert.That(service.IsActive).IsFalse();
            await Assert.That(service.EventCount).IsEqualTo(0);
        }
        finally
        {
            TestGate.Release();
        }
    }

    [Test]
    public async Task StartAndStopTrace_CreatesValidJsonlFile_WithCorrectLifecycleEvents()
    {
        await TestGate.WaitAsync();
        try
        {
            var service = PlayerTraceService.Instance;
            if (service.IsActive)
            {
                service.StopTrace();
            }

            const uint charId = 42;
            const string charName = "TestFarmer";
            const string scenario = "unit_test_trace";
            var character = new Character(new UnitCustomModelParams()) { Id = charId, Name = charName };

            var (started, startMsg, traceFile) = service.StartTrace(charId, charName, scenario);
            await Assert.That(started).IsTrue();
            await Assert.That(service.IsActive).IsTrue();
            await Assert.That(service.TracedCharacterId).IsEqualTo(charId);
            await Assert.That(service.TracedCharacterName).IsEqualTo(charName);
            await Assert.That(traceFile).IsNotNull();

            // Record a mark and raw event
            service.RecordMark("crop_planted");
            service.RecordWorld(character, "doodad_created", 1001, 1482, new { x = 10, y = 20 });

            var (stopped, stopMsg, finalPath) = service.StopTrace();
            await Assert.That(stopped).IsTrue();
            await Assert.That(service.IsActive).IsFalse();
            await Assert.That(finalPath).IsNotNull();
            await Assert.That(File.Exists(finalPath!)).IsTrue();

            try
            {
                var lines = await File.ReadAllLinesAsync(finalPath!);
                await Assert.That(lines.Length).IsGreaterThanOrEqualTo(3);

                // Parse lines as JSON
                var firstRecord = JsonSerializer.Deserialize<PlayerTraceRecord>(lines[0]);
                await Assert.That(firstRecord).IsNotNull();
                await Assert.That(firstRecord!.Category).IsEqualTo("lifecycle");
                await Assert.That(firstRecord.Event).IsEqualTo("trace_started");
                await Assert.That(firstRecord.CharacterId).IsEqualTo(charId);

                var lastRecord = JsonSerializer.Deserialize<PlayerTraceRecord>(lines[^1]);
                await Assert.That(lastRecord).IsNotNull();
                await Assert.That(lastRecord!.Category).IsEqualTo("lifecycle");
                await Assert.That(lastRecord.Event).IsEqualTo("trace_stopped");
            }
            finally
            {
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }
            }
        }
        finally
        {
            TestGate.Release();
        }
    }

    [Test]
    public async Task Filtering_OnlyRecordsForTracedCharacterId()
    {
        await TestGate.WaitAsync();
        try
        {
            var service = PlayerTraceService.Instance;
            if (service.IsActive)
            {
                service.StopTrace();
            }

            const uint targetCharId = 100;
            const uint otherCharId = 200;
            var target = new Character(new UnitCustomModelParams()) { Id = targetCharId, Name = "TargetBot" };
            var other = new Character(new UnitCustomModelParams()) { Id = otherCharId, Name = "OtherBot" };

            var (started, _, traceFile) = service.StartTrace(targetCharId, "TargetBot", "filter_test");
            await Assert.That(started).IsTrue();

            try
            {
                // Event for untracked character should be dropped
                service.RecordSkill(other, "skill_requested", 999);

                // Event for tracked character should be recorded
                service.RecordSkill(target, "skill_requested", 1234);

                var eventCount = service.EventCount;
                // Started (1) + Target skill (1) = 2 events (otherCharId was ignored)
                await Assert.That(eventCount).IsEqualTo(2);
            }
            finally
            {
                var (_, _, path) = service.StopTrace();
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            TestGate.Release();
        }
    }

    [Test]
    public async Task MovementSampling_ThrottlesSuccessiveSamples()
    {
        await TestGate.WaitAsync();
        try
        {
            var service = PlayerTraceService.Instance;
            if (service.IsActive)
            {
                service.StopTrace();
            }

            const uint charId = 300;
            var character = new Character(new UnitCustomModelParams()) { Id = charId, Name = "Walker" };
            var (started, _, _) = service.StartTrace(charId, "Walker", "move_test");
            await Assert.That(started).IsTrue();

            try
            {
                // First movement sample records state transition (still -> moving)
                service.RecordMovementSample(character, isMoving: true, pos: new Vector3(10, 0, 10), yaw: 1.57f, velocity: Vector3.Zero);

                var countAfterFirst = service.EventCount;

                // Immediate sample with slight movement under 250ms threshold should be throttled
                service.RecordMovementSample(character, isMoving: true, pos: new Vector3(10.05f, 0, 10.05f), yaw: 1.57f, velocity: Vector3.Zero);
                await Assert.That(service.EventCount).IsEqualTo(countAfterFirst);

                // State transition (moving -> stopped) should record immediately regardless of timing
                service.RecordMovementSample(character, isMoving: false, pos: new Vector3(10.1f, 0, 10.1f), yaw: 1.57f, velocity: Vector3.Zero);
                await Assert.That(service.EventCount).IsGreaterThan(countAfterFirst);
            }
            finally
            {
                var (_, _, path) = service.StopTrace();
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            TestGate.Release();
        }
    }

    [Test]
    public async Task RefusalRecording_EmitsRefusalCategoryWithCoordinatesAndReason()
    {
        await TestGate.WaitAsync();
        try
        {
            var service = PlayerTraceService.Instance;
            if (service.IsActive)
                service.StopTrace();

            const uint charId = 401;
            var character = new Character(new UnitCustomModelParams()) { Id = charId, Name = "Caster" };
            character.Transform.Local.SetPosition(new Vector3(100f, 200f, 50f));
            var (started, _, path) = service.StartTrace(charId, "Caster", "refusal_test");
            await Assert.That(started).IsTrue();

            try
            {
                service.RecordRefusal(character, "skill", 13980, "GlobalCooldown", 102);

                await Assert.That(service.EventCount).IsGreaterThan(0);
            }
            finally
            {
                service.StopTrace();
                if (path != null && File.Exists(path))
                {
                    var lines = await File.ReadAllLinesAsync(path);
                    var refusalLine = lines.FirstOrDefault(l => l.Contains("GlobalCooldown"));
                    await Assert.That(refusalLine).IsNotNull();
                    await Assert.That(refusalLine?.Contains("\"category\":\"refusal\"") == true).IsTrue();
                    await Assert.That(refusalLine?.Contains("\"event\":\"skill_refused\"") == true).IsTrue();
                    File.Delete(path);
                }
            }
        }
        finally
        {
            TestGate.Release();
        }
    }

    [Test]
    public async Task OperatorChatFilter_ExcludesCommandsFromPacketInTrace()
    {
        await TestGate.WaitAsync();
        try
        {
            var service = PlayerTraceService.Instance;
            if (service.IsActive)
                service.StopTrace();

            const uint charId = 501;
            var character = new Character(new UnitCustomModelParams()) { Id = charId, Name = "AdminUser" };
            var (started, _, path) = service.StartTrace(charId, "AdminUser", "filter_test");
            await Assert.That(started).IsTrue();

            try
            {
                var cmdPacket = new AAEmu.Game.Core.Packets.C2G.CSSendChatMessagePacket();
                typeof(AAEmu.Game.Core.Packets.C2G.CSSendChatMessagePacket)
                    .GetProperty("IsCommand")!
                    .SetValue(cmdPacket, true);

                var beforeCount = service.EventCount;
                service.RecordPacketIn(character, cmdPacket);
                await Assert.That(service.EventCount).IsEqualTo(beforeCount);
            }
            finally
            {
                service.StopTrace();
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            TestGate.Release();
        }
    }
}
