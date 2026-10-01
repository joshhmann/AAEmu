#nullable enable

using System.Collections.Immutable;
using System.Numerics;

using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// The disagreement record writer: write → read → equal. The schema contract
/// covers the NaN-carrying frozen inputs (unreadable measurements must survive
/// the round-trip) and the trigger list.
/// </summary>
[NotInParallel]
public class JevDisagreementStoreTests
{
    private static readonly Vector3 Self = new(100f, 100f, 10f);
    private static readonly DateTime FrozenAt = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task TravelRecord_RoundTrips_WithNaNDistanceAndTriggers()
    {
        // A tampered verdict against frozen inputs with an UNREADABLE distance
        // (NaN): the NaN must survive the round-trip, or the row no longer
        // describes the wake it was frozen from.
        var frozen = TravelReady() with { DistanceM = float.NaN };
        var expected = TravelBrain.Decide(frozen);
        var actual = expected with { Reason = TravelReason.Repathed };
        var wake = new JevTravelWake("wake-travel-1", frozen, actual);
        var record = JevDisagreementStore.FromMismatch(
            wake, TravelDecisionPayload.From(actual),
            [JevSamplingTriggers.RetriedTrigger, JevSamplingTriggers.OscillationTrigger], FrozenAt);

        var path = TempPath();
        try
        {
            JevDisagreementStore.Write(path, record);
            var reread = JevDisagreementStore.Read(path);

            // Field-by-field: whole-record IsEqualTo can never hold because
            // ImmutableArray<string> equality is reference-based, so a
            // deserialized row would always differ even when every field matches.
            await Assert.That(reread.SchemaVersion).IsEqualTo(JevDisagreementStore.CurrentSchemaVersion);
            await Assert.That(reread.Brain).IsEqualTo(nameof(JevBrain.Travel));
            await Assert.That(reread.WakeId).IsEqualTo(record.WakeId);
            await Assert.That(reread.FrozenAtUtc).IsEqualTo(record.FrozenAtUtc);
            await Assert.That(reread.InputsJson).IsEqualTo(record.InputsJson);
            await Assert.That(reread.ExpectedJson).IsEqualTo(record.ExpectedJson);
            await Assert.That(reread.ActualJson).IsEqualTo(record.ActualJson);
            await Assert.That(reread.Triggers).IsEquivalentTo(
                ImmutableArray.Create(JevSamplingTriggers.RetriedTrigger, JevSamplingTriggers.OscillationTrigger));

            var inputs = JevCalibrationJson.Decode<TravelBrainInputs>(reread.InputsJson);
            await Assert.That(float.IsNaN(inputs.DistanceM)).IsTrue();
            var expectedDecision = JevCalibrationJson.Decode<TravelDecision>(reread.ExpectedJson);
            await Assert.That(expectedDecision).IsEqualTo(actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task NeedsRecord_RoundTrips_WithNaNCropDistance()
    {
        // The needs hold on an unreadable crop distance: the NaN authorises
        // nothing, and the stored row must preserve that fact.
        var live = NeedsBlank() with { CropState = NeedsCropState.Live, CropMature = true, CropDistanceM = float.NaN };
        var expected = NeedsBrain.Decide(live);
        var wake = new JevNeedsWake("wake-needs-1", live, expected);
        var record = JevDisagreementStore.FromMismatch(
            wake, NeedsDecisionPayload.From(expected),
            [JevSamplingTriggers.RecoveryPathTrigger], FrozenAt);

        var path = TempPath();
        try
        {
            JevDisagreementStore.Write(path, record);
            var reread = JevDisagreementStore.Read(path);

            await Assert.That(reread.InputsJson).IsEqualTo(record.InputsJson);
            await Assert.That(reread.ExpectedJson).IsEqualTo(record.ExpectedJson);
            await Assert.That(reread.ActualJson).IsEqualTo(record.ActualJson);
            await Assert.That(reread.Triggers).IsEquivalentTo([JevSamplingTriggers.RecoveryPathTrigger]);
            var inputs = JevCalibrationJson.Decode<NeedsBrainInputs>(reread.InputsJson);
            await Assert.That(float.IsNaN(inputs.CropDistanceM)).IsTrue();
            var decision = JevCalibrationJson.Decode<NeedsBrainDecision>(reread.ExpectedJson);
            await Assert.That(decision).IsEqualTo(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Record_PreservesIdentityFields()
    {
        var frozen = TravelReady();
        var expected = TravelBrain.Decide(frozen);
        var wake = new JevTravelWake("wake-id-7", frozen, expected);
        var record = JevDisagreementStore.FromMismatch(
            wake, TravelDecisionPayload.From(expected), [], FrozenAt);

        var path = TempPath();
        try
        {
            JevDisagreementStore.Write(path, record);
            var reread = JevDisagreementStore.Read(path);

            await Assert.That(reread.WakeId).IsEqualTo("wake-id-7");
            await Assert.That(reread.FrozenAtUtc).IsEqualTo(FrozenAt);
            await Assert.That(reread.Triggers).IsEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------ fixtures

    private static TravelBrainInputs TravelReady() => new(
        ActorObjId: 4242,
        SelfPosition: Self,
        TargetKind: TravelTargetKind.Position,
        TargetObjId: 0,
        Destination: new Vector3(400f, 100f, 10f),
        DistanceM: 300f,
        ArrivalRadiusM: TravelBrain.DefaultArrivalRadiusM,
        LocalModeMaxM: TravelBrain.DefaultLocalModeMaxM,
        FollowRequested: false,
        DestWorldKnown: true,
        ActorWorldId: 1,
        ActorInstanceId: 1,
        DestWorldId: 1,
        DestInstanceId: 1,
        TargetResolved: true,
        PriorResolveAttempts: 0,
        LegOutcome: TravelLegOutcome.None,
        LegLive: false,
        LegTargetObjId: 0,
        LegDestinationKnown: false,
        LegDestination: Vector3.Zero,
        PriorRepathCount: 0,
        RepathBudget: TravelBrain.DefaultRepathBudget,
        RouteAvailable: false,
        RouteWaypoint: Vector3.Zero,
        RouteWaypointCount: 0,
        PriorMode: TravelMode.None,
        RetreatRequested: false,
        ThreatObjId: 0,
        ThreatPosition: Vector3.Zero);

    private static NeedsBrainInputs NeedsBlank() => new(
        ActorObjId: 7777,
        SelfPosition: Self,
        SoilReadable: true,
        SeedReadable: true,
        SeedCount: 1,
        OnValidSoil: false,
        CropState: NeedsCropState.None,
        CropMature: false,
        CropDistanceM: float.NaN,
        CropPosition: Vector3.Zero,
        CropEnRoute: false,
        CropObjId: 0,
        CropTemplateId: 0,
        CropApproachAttempts: 0,
        MaxCropApproachAttempts: NeedsBrain.DefaultMaxCropApproachAttempts,
        HarvestJustLanded: false,
        SoilEnRoute: false,
        SoilResolved: false,
        SoilDestination: Vector3.Zero,
        SoilAttempts: 0,
        MaxSoilAttempts: NeedsBrain.DefaultMaxSoilAttempts,
        HarvestRangeM: NeedsBrain.DefaultHarvestRangeM);

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "aaemu-jev-disagreement-" + Guid.NewGuid().ToString("N") + ".json");
}

