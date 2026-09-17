#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Authoritative reference coordinates and entity templates for the canonical
/// Nuian starter corridor in Solzreed Peninsula (Zone 125) to Windshade (Zone 1).
///
/// Data source: canonical compact.sqlite3 (ArcheAge 1.2 r208022).
/// </summary>
public static class SolzreedStarterCorridorProfile
{
    // ---- Zone Identifiers ----
    public const uint SolzreedZoneKey = 125;
    public const uint SolzreedRegionId = 8;
    public const uint WindshadeZoneKey = 1;

    // ---- Waypoints (Coordinates in World Units) ----
    /// <summary>Canonical Nuian character birth / spawn location.</summary>
    public static readonly Vector3 StarterSpawnPosition = new(15578.0f, 15382.1f, 126.5f);

    /// <summary>Mor (NPC 3597) — First quest giver ('나를 찾는 사람').</summary>
    public static readonly Vector3 MorNpcPosition = new(15562.5f, 15354.3f, 126.3f);

    /// <summary>Report NPC 3511 — Outskirts of starting camp.</summary>
    public static readonly Vector3 CampReportNpcPosition = new(15498.2f, 15287.0f, 127.1f);

    /// <summary>Baragi Village Recruiter (NPC 3512) — '바라기 마을로' report & '화난 멧돼지들' accept.</summary>
    public static readonly Vector3 BaragiVillageNpcPosition = new(15320.1f, 15024.4f, 128.5f);

    /// <summary>Windshade Blue Salt Brotherhood Recruiter (NPC 9789).</summary>
    public static readonly Vector3 WindshadeRecruiterPosition = new(12556.42f, 15369.47f, 161.61f);

    // ---- Canonical NPC Template IDs ----
    public const uint MorNpcTemplateId = 3597;
    public const uint CampReportNpcTemplateId = 3511;
    public const uint BaragiNpcTemplateId = 3512;
    public const uint WindshadeRecruiterNpcTemplateId = 9789;

    // ---- Canonical Quest Context IDs ----
    public const uint Quest330PersonLookingForMe = 330;
    public const uint Quest6198ToBaragiVillage = 6198;
    public const uint Quest251AngryWildBoars = 251;
    public const uint Quest254MothersWorry = 254;
    public const uint Quest255JennysRequest = 255;
    public const uint Quest4415WindshadeWater = 4415;

    // ---- Experience Thresholds (from compact.sqlite3 levels table) ----
    public const int ExpLevel1 = 0;
    public const int ExpLevel2 = 400;
    public const int ExpLevel9 = 31200;
    public const int ExpLevel10 = 42000;
}
