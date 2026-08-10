using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Race/gender name pools for bot citizens (P1 t_61814965). Every name
/// matches the NameManager default regex (^[a-zA-Z0-9а-яА-Я]{1,18}$) — the
/// same namespace humans live in. Names are flavored per race (Nuian =
/// Mediterranean, Elf = sylvan, Dwarf = nordic, Hariharan = eastern).
/// </summary>
public static class BotNamePool
{
    private static readonly IReadOnlyDictionary<(Race, Gender), string[]> Pools = new Dictionary<(Race, Gender), string[]>
    {
        [(Race.Nuian, Gender.Male)] =
        [
            "Aurelio", "Cassian", "Dario", "Emilio", "Fabian", "Gavriel", "Hadrian", "Isidro",
            "Juliano", "Kastor", "Lucian", "Marcello", "Nico", "Orazio", "Petro", "Quirino",
            "Renato", "Silvano", "Teodoro", "Umberto", "Vittore", "Alessio", "Rinaldo", "Sandro"
        ],
        [(Race.Nuian, Gender.Female)] =
        [
            "Adriana", "Beatrice", "Camilla", "Delia", "Elisa", "Fiorella", "Gaia", "Helena",
            "Isabetta", "Juliana", "Lucia", "Mirella", "Noemi", "Ottavia", "Paolina", "Rosa",
            "Serafina", "Teresa", "Valentina", "Zaira", "Angelica", "Claudia", "Elena", "Fiamma"
        ],
        [(Race.Elf, Gender.Male)] =
        [
            "Alaric", "Briar", "Caelum", "Dorian", "Eirian", "Fenwick", "Gaelan", "Hollis",
            "Ithil", "Jorel", "Kaelen", "Lirien", "Maelis", "Nimue", "Orien", "Percival",
            "Rowan", "Sylvan", "Thalion", "Vaelen", "Wren", "Yarrow", "Zephyrin", "Alden"
        ],
        [(Race.Elf, Gender.Female)] =
        [
            "Aelith", "Briallen", "Calanthe", "Dahlia", "Elowen", "Fenella", "Gwendolyn", "Hazel",
            "Isolde", "Juniper", "Kestrel", "Lorien", "Maeve", "Nerissa", "Opal", "Peregrine",
            "Rowena", "Seren", "Tamsin", "Violet", "Willow", "Ysabel", "Zinnia", "Aurelia"
        ],
        [(Race.Dwarf, Gender.Male)] =
        [
            "Baldur", "Bjorn", "Cedrik", "Dain", "Egil", "Fenrir", "Gunnar", "Hakon",
            "Ivar", "Joran", "Kjell", "Leif", "Magnus", "Njal", "Odin", "Ragnar",
            "Sven", "Torvald", "Ulric", "Vidar", "Wulfric", "Yngvar", "Arne", "Bram"
        ],
        [(Race.Dwarf, Gender.Female)] =
        [
            "Astrid", "Brynhild", "Dagny", "Eira", "Freya", "Gudrun", "Helga", "Ingrid",
            "Jorunn", "Kara", "Liv", "Maren", "Nanna", "Osa", "Runa", "Sigrid",
            "Tora", "Ulla", "Vilde", "Ylva", "Solveig", "Alva", "Bodil", "Eydis"
        ],
        [(Race.Hariharan, Gender.Male)] =
        [
            "Akihiro", "Bao", "Chen", "Daichi", "Eiji", "Feng", "Genji", "Haruki",
            "Isamu", "Jin", "Kenji", "Liang", "Min", "Noboru", "Osamu", "Peng",
            "Ren", "Shen", "Takashi", "Wei", "Xiang", "Yuki", "Zhi", "Akira"
        ],
        [(Race.Hariharan, Gender.Female)] =
        [
            "Akiko", "Baozhai", "Chiyo", "Daiyu", "Emi", "Fumiko", "Ginko", "Hana",
            "Isolde", "Jun", "Kiku", "Lian", "Mei", "Nami", "Otsu", "Ping",
            "Qi", "Rei", "Suki", "Tsubaki", "Yuna", "Xiu", "Aiko", "Bai"
        ],
    };

    /// <summary>All names for the race/gender pool.</summary>
    public static IReadOnlyList<string> For(Race race, Gender gender)
        => Pools.TryGetValue((race, gender), out var names) ? names : [];

    /// <summary>Deterministic index pick (same seed → same name).</summary>
    public static string Pick(Race race, Gender gender, Random random)
    {
        var names = For(race, gender);
        return names.Count == 0 ? $"Citizen{random.Next(1000, 9999)}" : names[random.Next(names.Count)];
    }
}
