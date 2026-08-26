using System.Collections.Concurrent;

using AAEmu.Game.Core.Managers;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Managers.Name;

public sealed class NameManagerTests
{
    [Test]
    public async Task EmptyNameManagerShouldNotHaveNames()
    {
        // Arrange
        var sut = new NameManager();

        // Act
        sut.Load([], [], []);

        // Assert
        await Assert.That(sut.NoNamesRegistered()).IsTrue();
    }

    [Test]
    public async Task AddCharacterNameShouldHaveNames()
    {
        // Arrange
        var charName = "TestName".NormalizeName();
        var charId = 1u;
        var charAccount = 1000u;

        var sut = new NameManager();
        sut.Load([], [], []);

        // Act
        sut.AddCharacter(charId, charName, charAccount);

        // Assert
        await Assert.That(sut.NoNamesRegistered()).IsFalse();
        await Assert.That(sut.GetCharacterName(charId)).IsEqualTo(charName);
        await Assert.That(sut.GetCharacterId(charName)).IsEqualTo(charId);
    }

    [Test]
    public async Task GetCharacterAccountShouldReturnFoundAccounts()
    {
        // Arrange
        var charId = 1u;
        var charAccount = 1000u;
        var sut = new NameManager();
        sut.Load([], [],
            characterAccounts: new Dictionary<uint, uint>
            {
                [charId] = charAccount
            });

        // Act
        var accountId = sut.GetCharacterAccount(charId);

        // Assert
        await Assert.That(accountId).IsEqualTo(charAccount);
    }

    [Test]
    public async Task ValidationCharacterNameAlreadyExistsCheck()
    {
        // Arrange
        var charId = 1u;
        var charAccount = 1000u;
        var charName = "TestName".NormalizeName();
        var mockCharacterManager = Mock.Of<ICharacterManager>();
        mockCharacterManager.IsCharacterPendingDeletion(charName).Returns(false);
        var sut = new NameManager(new Lazy<ICharacterManager>(() => mockCharacterManager.Object));

        sut.Load([], [], []);

        sut.AddCharacter(charId, charName, charAccount);

        // Act
        var result = sut.ValidateCharacterName(charName);

        // Assert
        await Assert.That(result).IsEqualTo(CharacterCreateError.NameAlreadyExists);
    }

    [Test]
    public async Task ValidationCharacterNamePendingDeletionFailed()
    {
        // Arrange
        var charId = 1u;
        var charAccount = 1000u;
        var charName = "TestName".NormalizeName();
        var mockCharacterManager = Mock.Of<ICharacterManager>();
        mockCharacterManager.IsCharacterPendingDeletion(charName).Returns(true);
        var sut = new NameManager(new Lazy<ICharacterManager>(() => mockCharacterManager.Object));

        sut.Load([], [], []);

        sut.AddCharacter(charId, charName, charAccount);

        // Act
        var result = sut.ValidateCharacterName(charName);

        // Assert
        await Assert.That(result).IsEqualTo(CharacterCreateError.Failed);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("000&#$*")]
    public async Task ValidationCharacterInvalidName(string providedName)
    {
        // Arrange
        var charName = providedName.NormalizeName();
        var sut = new NameManager();

        sut.Load([], [], []);

        // Act
        var result = sut.ValidateCharacterName(charName);

        // Assert
        await Assert.That(result).IsEqualTo(CharacterCreateError.InvalidCharacters);
    }

    [Test]
    [Arguments("Roger")]
    [Arguments("Zero")]
    [Arguments("NLObP")]
    public async Task ValidationCharacterValidNameSucceed(string providedName)
    {
        // Arrange
        var charName = providedName.NormalizeName();
        var mockCharacterManager = Mock.Of<ICharacterManager>();
        var sut = new NameManager(new Lazy<ICharacterManager>(() => mockCharacterManager.Object));

        sut.Load([], [], []);

        // Act
        var result = sut.ValidateCharacterName(charName);

        // Assert
        await Assert.That(result).IsEqualTo(CharacterCreateError.Ok);
    }

    [Test]
    public async Task RemoveCharacterIdWorksAsExpected()
    {
        // Arrange
        var charId = 1u;
        var charAccount = 1000u;
        var charName = "TestName".NormalizeName();
        var mockCharacterManager = Mock.Of<ICharacterManager>();
        mockCharacterManager.IsCharacterPendingDeletion(charName).Returns(true);
        var sut = new NameManager(new Lazy<ICharacterManager>(() => mockCharacterManager.Object));

        sut.Load([], [], []);

        sut.AddCharacter(charId, charName, charAccount);

        // Act
        sut.RemoveCharacterId(charId);

        // Assert
        await Assert.That(sut.NoNamesRegistered()).IsTrue();
        await Assert.That(sut.GetCharacterName(charId)).IsNull();
        await Assert.That(sut.GetCharacterId(charName)).IsEqualTo(0u);
        await Assert.That(sut.GetCharacterAccount(charId)).IsEqualTo(0u);
    }

    /// <summary>
    /// Regression (tier3 probe report §11.2): concurrent HeadlessSession.Provision
    /// calls from several bridge connections corrupted NameManager's plain
    /// Dictionaries ("Operations that change non-concurrent collections must have
    /// exclusive access") after ~100 seeded bots. Registry access is now guarded
    /// by an internal lock — this test hammers AddCharacter + reads + removes from
    /// parallel threads and fails on any corruption or lost registration.
    /// </summary>
    [Test]
    public async Task ConcurrentRegistrationMustNotCorruptRegistries()
    {
        // Arrange — every thread provisions its own disjoint id/name slice while
        // all threads read the shared registries, mirroring parallel seedDormant
        // bridge calls; a second worker wave removes never-registered ids.
        var sut = new NameManager();
        sut.Load([], [], []);

        const int threadCount = 8;
        const int perThread = 500;
        var errors = new ConcurrentBag<Exception>();

        var workers = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    var id = (uint)(t * perThread + i + 1);
                    var name = $"Bot{t:D2}_{i:D4}".NormalizeName();
                    sut.AddCharacter(id, name, 9000u + (uint)t);
                    if (sut.GetCharacterId(name) != id)
                        errors.Add(new InvalidOperationException($"lost registration for '{name}'"));
                    if (sut.GetCharacterAccount(id) != 9000u + t)
                        errors.Add(new InvalidOperationException($"lost account for id {id}"));
                    if (sut.GetCharacterName(id) != name)
                        errors.Add(new InvalidOperationException($"lost name for id {id}"));
                    sut.ValidateCharacterName(name);
                }
                catch (Exception e)
                {
                    errors.Add(e);
                }
            }
        })).ToArray();

        var removers = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread / 2; i++)
            {
                try
                {
                    sut.RemoveCharacterId((uint)(threadCount * perThread + t * perThread + i + 1));
                }
                catch (Exception e)
                {
                    errors.Add(e);
                }
            }
        })).ToArray();

        // Act
        await Task.WhenAll(workers.Concat(removers).ToArray());

        // Assert — no collection corruption, and every registration is intact.
        await Assert.That(errors.IsEmpty).IsTrue();
        for (var t = 0; t < threadCount; t++)
        for (var i = 0; i < perThread; i++)
        {
            var id = (uint)(t * perThread + i + 1);
            var name = $"Bot{t:D2}_{i:D4}".NormalizeName();
            await Assert.That(sut.GetCharacterId(name)).IsEqualTo(id);
        }
    }
}