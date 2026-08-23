using System.Collections.Frozen;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Embedded Layer-1/2 chatter bank (ROADMAP chatter tiering — Living Village
/// launches with ZERO LLM dependency): one canned template set per personality
/// archetype, with Layer-2 procedural fill tokens ({name} = speaking bot,
/// {target} = nearby character, {zone} = real zone name) substituted at send
/// time from live world data.
///
/// Shipped as code on purpose (no filesystem directory dependency yet); the
/// ROADMAP's `chatter/{archetype}/` file layout is a later migration that can
/// swap this table out behind <see cref="GetLines"/>.
/// </summary>
public static class BotChatterTemplates
{
    /// <summary>Speaking bot's name token.</summary>
    public const string NameToken = "{name}";

    /// <summary>Nearby character's name token.</summary>
    public const string TargetToken = "{target}";

    /// <summary>Real zone name token.</summary>
    public const string ZoneToken = "{zone}";

    /// <summary>Archetype keys, aligned with the ROADMAP personality names.</summary>
    public static readonly string[] Archetypes =
    [
        "lawful", "greedy", "cheerful", "paranoid", "pirate", "farmer", "merchant", "guard"
    ];

    private static readonly FrozenDictionary<string, string[]> Bank = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["lawful"] =
        [
            "Order keeps {zone} standing, {target}. Remember that.",
            "{name} straightens up. Rules exist for a reason, friend.",
            "Honest work, honest pay — that's the whole law, {target}.",
            "You'll find no trouble from me, {target}, so long as you keep the peace."
        ],
        ["greedy"] =
        [
            "Coin is coin, {target}, but I never say no to more.",
            "You look like someone with deep pockets, {target}.",
            "Prices in {zone} are robbery lately. Robbery, I tell you.",
            "Everything has a price, {target}. Everything."
        ],
        ["cheerful"] =
        [
            "Well met, {target}! Fine day in {zone}, isn't it?",
            "{name} waves at {target}. Beautiful {zone} weather!",
            "Hello there, {target}! Another good day in {zone}.",
            "{target}! Good to see a friendly face out here."
        ],
        ["paranoid"] =
        [
            "Keep your voice down, {target}. Walls have ears in {zone}.",
            "{name} watches {target} carefully. Who sent you?",
            "I don't trust the roads out of {zone} these days, {target}.",
            "You're not from around here, are you, {target}?"
        ],
        ["pirate"] =
        [
            "The coast near {zone} belongs to the free crews, {target}.",
            "{name} grins at {target}. Loose lips sink ships, friend.",
            "Gold spends the same no matter whose flag it sailed under, {target}.",
            "Storm's coming, {target}. The sea always collects its due."
        ],
        ["farmer"] =
        [
            "Good soil here in {zone}, {target}. Blesses the whole valley.",
            "{name} wipes the dirt off. Crops won't tend themselves, {target}.",
            "Rain's coming, {target}. I can feel it in my bones.",
            "Mind the fences, {target}. Beasts have been bold this season."
        ],
        ["merchant"] =
        [
            "Looking to trade, {target}? Best prices in {zone}.",
            "{name} straightens their stall. Fresh goods just in, {target}.",
            "Caravans through {zone} have been slow, {target}. Prices reflect it.",
            "Buy low, sell high, {target}. That's the whole secret."
        ],
        ["guard"] =
        [
            "Move along, {target}. Keep your blade sheathed in {zone}.",
            "{name} nods at {target}. All quiet on this stretch.",
            "Stay alert, {target}. Pirates sighted near {zone} lately.",
            "No trouble in {zone} on my watch, citizen."
        ]
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] FallbackOrder = ["cheerful", "merchant", "guard", "farmer"];

    /// <summary>
    /// The canned lines for an archetype. Unknown archetypes fall back to a
    /// small deterministic subset so a bot with junk metadata still speaks.
    /// </summary>
    public static IReadOnlyList<string> GetLines(string archetype)
    {
        if (!string.IsNullOrWhiteSpace(archetype) && Bank.TryGetValue(archetype.Trim(), out var lines))
            return lines;
        return Bank[FallbackOrder[0]];
    }

    /// <summary>
    /// Deterministic archetype resolution: the bot's recorded
    /// <see cref="Models.Game.Bots.PlayerBotMetadata.Personality"/> wins when
    /// it names (or contains) a known archetype; otherwise a seed-stable hash
    /// of the character id picks one, so the same bot always lands on the same
    /// voice across restarts.
    /// </summary>
    public static string ResolveArchetype(string? personality, uint characterId)
    {
        if (!string.IsNullOrWhiteSpace(personality))
        {
            var normalized = personality.Trim();
            foreach (var archetype in Archetypes)
                if (normalized.Contains(archetype, StringComparison.OrdinalIgnoreCase))
                    return archetype;
        }

        return Archetypes[(int)(Fnv1a(characterId) % (uint)Archetypes.Length)];
    }

    /// <summary>
    /// Picks a deterministic line for a (bot, target) encounter: the same pair
    /// hashes to the same line until their pair cooldown expires and the pick
    /// re-rolls.
    /// </summary>
    public static string PickLine(string archetype, uint botId, uint targetId)
    {
        var lines = GetLines(archetype);
        var index = (int)(Fnv1a(botId, targetId) % (uint)lines.Count);
        return lines[index];
    }

    /// <summary>
    /// Layer-2 procedural fill: substitutes real entity/zone values into the
    /// template. Unresolvable values degrade to neutral placeholders instead
    /// of leaking raw tokens into chat.
    /// </summary>
    public static string Substitute(string template, string name, string target, string zone)
        => template
            .Replace(NameToken, string.IsNullOrWhiteSpace(name) ? "friend" : name)
            .Replace(TargetToken, string.IsNullOrWhiteSpace(target) ? "traveler" : target)
            .Replace(ZoneToken, string.IsNullOrWhiteSpace(zone) ? "these parts" : zone);

    private static uint Fnv1a(uint value)
    {
        var hash = 2166136261u;
        for (var i = 0; i < 4; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= 16777619u;
        }
        return hash;
    }

    private static uint Fnv1a(uint a, uint b)
    {
        var hash = 2166136261u;
        for (var i = 0; i < 4; i++)
        {
            hash ^= (byte)(a >> (i * 8));
            hash *= 16777619u;
        }
        for (var i = 0; i < 4; i++)
        {
            hash ^= (byte)(b >> (i * 8));
            hash *= 16777619u;
        }
        return hash;
    }
}
