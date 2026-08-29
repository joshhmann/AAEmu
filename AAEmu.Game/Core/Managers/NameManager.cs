using System.Text.RegularExpressions;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models;
using AAEmu.Game.Models.StaticValues;
using Microsoft.Extensions.Options;
using NLog;

namespace AAEmu.Game.Core.Managers;

public partial class NameManager(Lazy<ICharacterManager> characterManager = null, IOptions<AppConfiguration> options = null) : Singleton<NameManager>, INameManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private Regex _characterNameRegex;

    // Thread safety: the three registries below are mutated by
    // HeadlessSession.Provision (bridge 'provision'/'seedDormant' commands),
    // which can run on several bridge connection threads concurrently. Plain
    // Dictionaries corrupted under that load ("Operations that change
    // non-concurrent collections must have exclusive access" after ~100 bots,
    // tier3 probe report §11.2). One lock guards all three so multi-registry
    // invariants (id ↔ name ↔ account) stay linearizable — it only covers
    // in-memory dictionary ops, never I/O, and does not serialize provisioning
    // itself (DB work in Provision runs outside it).
    private readonly object _registryLock = new();

    private Dictionary<uint, string> _characterIds = [];
    private Dictionary<string, uint> _characterNames = [];
    private Dictionary<uint, uint> _characterAccounts = [];

    public string GetCharacterName(uint characterId)
    {
        lock (_registryLock)
        {
            return _characterIds.TryGetValue(characterId, out var characterName)
                ? characterName
                : null;
        }
    }

    public uint GetCharacterId(string normalizedCharacterName)
    {
        if (string.IsNullOrEmpty(normalizedCharacterName))
            return 0u;

        lock (_registryLock)
        {
            if (_characterNames.TryGetValue(normalizedCharacterName, out var characterId))
                return characterId;
            var normalized = normalizedCharacterName.NormalizeName();
            if (_characterNames.TryGetValue(normalized, out characterId))
                return characterId;
            foreach (var (key, id) in _characterNames)
            {
                if (string.Equals(key, normalizedCharacterName, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
            return 0u;
        }
    }

    public uint GetCharacterAccount(uint characterId)
    {
        lock (_registryLock)
        {
            return _characterAccounts.TryGetValue(characterId, out var accountId)
                ? accountId
                : 0;
        }
    }

    public NameManager() : this(null, null) { }

    private const string DefaultCharacterNameRegexPattern = "^[a-zA-Z0-9а-яА-Я]{1,18}$";
    [GeneratedRegex(DefaultCharacterNameRegexPattern)]
    private static partial Regex DefaultCharacterNameRegex();

    public void Load()
    {
        if (options?.Value.CharacterNameRegex is { } characterNameRegex &&
            characterNameRegex != DefaultCharacterNameRegexPattern)
        {
            _characterNameRegex = new Regex(characterNameRegex, RegexOptions.Compiled);
        }

        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, name, account_id, deleted FROM characters";
                command.Prepare();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        lock (_registryLock)
                        {
                            var id = reader.GetUInt32("id");
                            var name = reader.GetString("name").ToLower();
                            var account = reader.GetUInt32("account_id");
                            var deleted = reader.GetInt32("deleted");
                            var normalizedName = name.NormalizeName();
                            _characterIds.Add(id, normalizedName);
                            if (deleted == 0)
                                _characterNames.Add(normalizedName, id); // Ignore deleted names, but do add the IDs to the old account
                            _characterAccounts.Add(id, account);
                        }
                    }
                }
            }
        }

        Logger.Info($"Loaded {_characterIds.Count} character names");
    }

    /// <summary>
    /// For testing purposes
    /// </summary>
    /// <param name="characterIds">Initial character ids</param>
    /// <param name="characterNames">Initial character names</param>
    /// <param name="characterAccounts">Initial character accounts</param>
    internal void Load(
        Dictionary<uint, string> characterIds,
        Dictionary<string, uint> characterNames,
        Dictionary<uint, uint> characterAccounts)
    {
        if (options?.Value.CharacterNameRegex is { } characterNameRegex &&
            characterNameRegex != DefaultCharacterNameRegexPattern)
        {
            _characterNameRegex = new Regex(characterNameRegex, RegexOptions.Compiled);
        }

        lock (_registryLock)
        {
            _characterIds = characterIds;
            _characterNames = characterNames;
            _characterAccounts = characterAccounts;
        }
    }

    public CharacterCreateError ValidateCharacterName(string name)
    {
        lock (_registryLock)
        {
            if (_characterNames.TryGetValue(name, out _))
            {
                if (characterManager?.Value.IsCharacterPendingDeletion(name) == true)
                    return CharacterCreateError.Failed;

                return CharacterCreateError.NameAlreadyExists;
            }
        }

        if (string.IsNullOrWhiteSpace(name) || !ValidatesName(name.AsSpan()))
            return CharacterCreateError.InvalidCharacters;

        return CharacterCreateError.Ok;
    }

    private bool ValidatesName(ReadOnlySpan<char> name) =>
        (_characterNameRegex ?? DefaultCharacterNameRegex())
        .IsMatch(name);

    public void AddCharacter(uint characterId, string name, uint accountId)
    {
        var normalizedName = name.NormalizeName();
        lock (_registryLock)
        {
            if (!_characterIds.TryAdd(characterId, name.NormalizeName()))
            {
                var oldName = _characterIds.GetValueOrDefault(characterId) ?? string.Empty;
                if (string.Compare(name, oldName, StringComparison.InvariantCultureIgnoreCase) != 0)
                    Logger.Error($"AddCharacterName, failed to register name for {name} ({characterId}), Account {accountId}, OldName {oldName}");
            }
            else
            {
                Logger.Info($"AddCharacterName, Registered character name {name} ({characterId})");
            }

            if (!_characterNames.TryAdd(normalizedName, characterId))
            {
                var oldId = _characterNames.GetValueOrDefault(normalizedName);
                if (characterId != oldId)
                    Logger.Error($"AddCharacterName, failed to register id for {name} ({characterId}), Account {accountId}, OldId {oldId}");
            }
            else
            {
                Logger.Info($"AddCharacterName, Registered character id {name} ({characterId})");
            }

            if (!_characterAccounts.TryAdd(characterId, accountId))
            {
                var oldAccount = _characterAccounts.GetValueOrDefault(characterId);
                if (accountId != oldAccount)
                    Logger.Error($"AddCharacterName, failed to register account for {name} ({characterId}), Account {accountId}, OldAccount {oldAccount}");
            }
            else
            {
                Logger.Info($"AddCharacterName, Registered account {accountId} for {name} ({characterId})");
            }
        }
    }

    public void RemoveCharacterId(uint characterId)
    {
        lock (_registryLock)
        {
            if (_characterIds.TryGetValue(characterId, out var characterName))
            {
                _characterIds.Remove(characterId);
                _characterNames.Remove(characterName);
                Logger.Info($"AddCharacterName, Remove name and id registrations for character Id {characterId}");
            }
            else
            {
                Logger.Error($"AddCharacterName, No name was registered for character Id {characterId}");
            }

            if (_characterAccounts.Remove(characterId))
            {
                Logger.Info($"AddCharacterName, Removed account registration for character Id {characterId}");
            }
            else
            {
                Logger.Error($"AddCharacterName, No account was registered for character Id {characterId}");
            }
        }
    }

    public bool NoNamesRegistered()
    {
        lock (_registryLock)
        {
            return _characterIds.Count <= 0;
        }
    }
}
