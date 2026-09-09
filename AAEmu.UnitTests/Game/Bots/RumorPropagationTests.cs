using System.Reflection;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// R1 rumor propagation (M9 substrate slice R1): pure in-memory graph +
/// decay + imperfect-copy rule. No DB, no tick, no chat, no engine — the
/// store under test touches zero singletons.
/// </summary>
public class RumorPropagationTests
{
    private const uint WitnessId = 101;
    private const uint MerchantId = 202;

    private static RumorStore CreateStore(Dictionary<uint, string>? personalities = null)
    {
        personalities ??= new Dictionary<uint, string>
        {
            [WitnessId] = "guard",
            [MerchantId] = "greedy merchant"
        };
        return new RumorStore(
            id => new PlayerBotMetadata
            {
                CharacterId = id,
                Personality = personalities.TryGetValue(id, out var personality) ? personality : string.Empty
            },
            TimeProvider.System);
    }

    [Test]
    public async Task Observe_WitnessTheft_ExactCopyAtFullConfidence()
    {
        var store = CreateStore();
        var seed = new RumorComposer.TheftSeedOptions { ZoneName = "Marianople", ActorName = "Redhand", ItemTemplateId = 15659, Count = 5 };

        var eventId = RumorComposer.SeedTheftWithWitness(store, seed);

        await Assert.That(store.TryGetHearsay(eventId, WitnessId, out var copy)).IsTrue();
        await Assert.That(copy).IsNotNull();
        await Assert.That(copy!.Confidence).IsEqualTo(1.0);
        await Assert.That(copy.Hop).IsEqualTo(0);
        await Assert.That(copy.ZoneName).IsEqualTo("Marianople");
        await Assert.That(copy.ItemTemplateId).IsEqualTo(15659u);
        await Assert.That(copy.ActorName).IsEqualTo("Redhand");
        await Assert.That(copy.Count).IsEqualTo(5);
    }

    [Test]
    public async Task TickRetell_Hop1ToMerchant_DegradedCopyAtDecayedConfidence()
    {
        var store = CreateStore();
        var eventId = RumorComposer.SeedTheftWithWitness(store);

        var result = store.TickRetell(eventId, WitnessId, MerchantId);

        await Assert.That(result.Accepted).IsTrue();
        await Assert.That(result.Hearsay).IsNotNull();
        await Assert.That(result.Hearsay!.Confidence).IsEqualTo(RumorStore.DecayPerHop);
        await Assert.That(result.Hearsay.Confidence < 1.0).IsTrue();
        await Assert.That(result.Hearsay.ZoneName).IsEqualTo("Marianople");
        await Assert.That(result.Hearsay.ItemTemplateId).IsEqualTo(15659u);
        var degraded = string.IsNullOrEmpty(result.Hearsay.ActorName) || result.Hearsay.Count != 5;
        await Assert.That(degraded).IsTrue();
    }

    [Test]
    public async Task TickRetell_Hop4AndBeyond_RefusedFailClosed()
    {
        var store = CreateStore();
        var eventId = RumorComposer.SeedTheftWithWitness(store);

        var chain = RumorComposer.RetellChain(store, eventId, WitnessId, 201, 202, 203);
        await Assert.That(chain.Count).IsEqualTo(3);

        var refused = store.TickRetell(eventId, 203, 204);

        await Assert.That(refused.Accepted).IsFalse();
        await Assert.That(refused.Hearsay).IsNull();
        await Assert.That(string.IsNullOrWhiteSpace(refused.Reason)).IsFalse();
        await Assert.That(store.TryGetHearsay(eventId, 204, out _)).IsFalse();
    }

    [Test]
    public async Task ConsumerApi_GroundTruth_UnreachableFromHearsaySurface()
    {
        var storeType = typeof(RumorStore);

        foreach (var method in storeType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            await Assert.That(method.ReturnType.Name.Contains("GroundTruth", StringComparison.Ordinal)).IsFalse();
            foreach (var parameter in method.GetParameters())
            {
                await Assert.That(parameter.ParameterType.Name.Contains("GroundTruth", StringComparison.Ordinal)).IsFalse();
            }
        }
        foreach (var property in storeType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            await Assert.That(property.PropertyType.Name.Contains("GroundTruth", StringComparison.Ordinal)).IsFalse();
        }
        var nestedTruth = storeType.Assembly.GetType("AAEmu.Game.Core.Managers.Bots.RumorGroundTruth");
        await Assert.That(nestedTruth is null || !nestedTruth.IsVisible).IsTrue();

        var store = CreateStore();
        var eventId = RumorComposer.SeedTheftWithWitness(store);
        store.TickRetell(eventId, WitnessId, MerchantId);
        await Assert.That(store.TryGetHearsay(eventId, MerchantId, out var merchantCopy)).IsTrue();
        await Assert.That(merchantCopy).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(merchantCopy!.ActorName)).IsTrue();
    }

    [Test]
    public async Task TickRetell_SameSeed_IdenticalRetellStrings()
    {
        static List<string> RunChain()
        {
            var store = CreateStore();
            var eventId = RumorComposer.SeedTheftWithWitness(store);
            return RumorComposer.RetellChain(store, eventId, WitnessId, MerchantId, 203)
                .Select(copy => copy.RetellText)
                .ToList();
        }

        var first = RunChain();
        var second = RunChain();

        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(second.Count).IsEqualTo(2);
        for (var i = 0; i < first.Count; i++)
        {
            await Assert.That(second[i]).IsEqualTo(first[i]);
        }
        foreach (var text in first)
        {
            await Assert.That(string.IsNullOrWhiteSpace(text)).IsFalse();
        }
    }
}
