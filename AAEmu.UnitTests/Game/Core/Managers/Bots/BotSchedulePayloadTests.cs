namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Hermetic rig for the C1 schedule JSON payload extension: the additive
/// anchors/lastPhase keys inside the B4 schedule TEXT column (no SQL
/// migration), key preservation, determinism, and byte-equality of
/// <c>PreserveExtensions</c> when no extensions exist (B4 restart shape).
/// </summary>
public class BotSchedulePayloadTests
{
    private const string PlainRoamJson =
        "{\"kind\":\"roam-loop\",\"waypoints\":8,\"radius\":30,\"phase\":1,\"loop\":true," +
        "\"home\":[19950,20050,100],\"path\":[[19950,20020,100]]}";

    [Test]
    public async Task TryReadAnchors_PlainRoamJson_ReturnsFalse()
    {
        await Assert.That(BotSchedulePayload.TryReadAnchors(PlainRoamJson, out var anchors)).IsFalse();
        await Assert.That(anchors).IsEqualTo(BotDailyAnchors.Template); // out = template default
    }

    [Test]
    public async Task TryReadAnchors_EmptyOrGarbage_ReturnsFalse()
    {
        await Assert.That(BotSchedulePayload.TryReadAnchors(string.Empty, out _)).IsFalse();
        await Assert.That(BotSchedulePayload.TryReadAnchors(null, out _)).IsFalse();
        await Assert.That(BotSchedulePayload.TryReadAnchors("not json {", out _)).IsFalse();
    }

    [Test]
    public async Task WithRuntimeState_PreservesExistingKeys_AndAddsExtensions()
    {
        var merged = BotSchedulePayload.WithRuntimeState(
            PlainRoamJson, new BotDailyAnchors { WorkStart = 9f }, BotSchedulePhase.Rest);

        await Assert.That(merged).Contains("\"kind\":\"roam-loop\"");
        await Assert.That(merged).Contains("\"radius\":30");
        await Assert.That(merged).Contains("\"phase\":1");
        await Assert.That(merged).Contains("\"anchors\"");
        await Assert.That(merged).Contains("\"workStart\":9");
        await Assert.That(merged).Contains("\"lastPhase\":\"Rest\"");

        // Round trip.
        await Assert.That(BotSchedulePayload.TryReadLastPhase(merged, out var phase)).IsTrue();
        await Assert.That(phase).IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(BotSchedulePayload.TryReadAnchors(merged, out var anchors)).IsTrue();
        await Assert.That(anchors.WorkStart).IsEqualTo(9f);
    }

    [Test]
    public async Task WithRuntimeState_OnEmptyJson_ProducesExtensionOnlyDocument()
    {
        var merged = BotSchedulePayload.WithRuntimeState(string.Empty, BotDailyAnchors.Template, null);

        await Assert.That(merged).Contains("\"anchors\"");
        await Assert.That(merged).DoesNotContain("lastPhase");
        await Assert.That(BotSchedulePayload.TryReadAnchors(merged, out _)).IsTrue();
    }

    [Test]
    public async Task WithRuntimeState_IsDeterministic_ForIdenticalInput()
    {
        var first = BotSchedulePayload.WithRuntimeState(PlainRoamJson, BotDailyAnchors.Template, BotSchedulePhase.Work);
        var second = BotSchedulePayload.WithRuntimeState(PlainRoamJson, BotDailyAnchors.Template, BotSchedulePhase.Work);

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task PreserveExtensions_WithoutExtensions_ReturnsNewJsonVerbatim()
    {
        // THE B4 guarantee: a store row that never carried extensions keeps
        // its byte-equal restart snapshot.
        var rebuilt = "{\"kind\":\"roam-loop\",\"waypoints\":8,\"radius\":30,\"phase\":0}";
        await Assert.That(BotSchedulePayload.PreserveExtensions(null, rebuilt)).IsEqualTo(rebuilt);
        await Assert.That(BotSchedulePayload.PreserveExtensions(string.Empty, rebuilt)).IsEqualTo(rebuilt);
        await Assert.That(BotSchedulePayload.PreserveExtensions(PlainRoamJson, rebuilt)).IsEqualTo(rebuilt);
    }

    [Test]
    public async Task PreserveExtensions_CarriesAnchorsAndPhaseOntoRebuiltDescriptor()
    {
        var withExtensions = BotSchedulePayload.WithRuntimeState(
            PlainRoamJson, new BotDailyAnchors { RestStart = 23f, RestEnd = 7f }, BotSchedulePhase.Home);
        var rebuilt = "{\"kind\":\"roam-loop\",\"waypoints\":8,\"radius\":30,\"phase\":2}";

        var preserved = BotSchedulePayload.PreserveExtensions(withExtensions, rebuilt);

        await Assert.That(preserved).Contains("\"kind\":\"roam-loop\"");
        await Assert.That(preserved).Contains("\"phase\":2"); // new descriptor wins
        await Assert.That(BotSchedulePayload.TryReadAnchors(preserved, out var anchors)).IsTrue();
        await Assert.That(anchors.RestStart).IsEqualTo(23f);
        await Assert.That(anchors.RestEnd).IsEqualTo(7f);
        await Assert.That(BotSchedulePayload.TryReadLastPhase(preserved, out var phase)).IsTrue();
        await Assert.That(phase).IsEqualTo(BotSchedulePhase.Home);
    }

    [Test]
    public async Task TryFromJsonElement_RejectsInvalidAnchors()
    {
        // Degenerate (equal-edge) windows → invalid → caller falls back to Template.
        var json = "{\"anchors\":{\"workStart\":18,\"workEnd\":18}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        await Assert.That(BotDailyAnchors.TryFromJsonElement(doc.RootElement.GetProperty("anchors"), out _)).IsFalse();

        var outOfRange = "{\"anchors\":{\"workStart\":25,\"workEnd\":30}}";
        using var doc2 = System.Text.Json.JsonDocument.Parse(outOfRange);
        await Assert.That(BotDailyAnchors.TryFromJsonElement(doc2.RootElement.GetProperty("anchors"), out _)).IsFalse();
    }

    [Test]
    public async Task TryFromJsonElement_AcceptsWrapAroundWindows()
    {
        // Night-shift work 22→06 wraps midnight and must be VALID.
        var json = "{\"anchors\":{\"workStart\":22,\"workEnd\":6,\"restStart\":10,\"restEnd\":14,\"homeBy\":9}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        await Assert.That(BotDailyAnchors.TryFromJsonElement(doc.RootElement.GetProperty("anchors"), out var anchors))
            .IsTrue();
        await Assert.That(anchors.WorkStart).IsEqualTo(22f);
        await Assert.That(anchors.HomeBy).IsEqualTo(9f);
    }

    [Test]
    public async Task TryReadRoamDescriptor_ParsesCenterRadiusSeed()
    {
        await Assert.That(BotSchedulePayload.TryReadRoamDescriptor(PlainRoamJson, out var center, out var radius, out var seed))
            .IsTrue();
        await Assert.That(center).IsEqualTo(new Vector3(19950f, 20050f, 100f));
        await Assert.That(radius).IsEqualTo(30f);
        await Assert.That(seed).IsEqualTo(1);

        await Assert.That(BotSchedulePayload.TryReadRoamDescriptor("{\"kind\":\"other\"}", out _, out _, out _)).IsFalse();
    }
}
