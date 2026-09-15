namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Persistence row for one placed (half-built) shipyard frame.
/// Plain DTO so the manager-level round-trip can be tested without live MySQL.
/// </summary>
public sealed record ShipyardFrameRow(
    ulong FrameId,
    uint TemplateId,
    uint OwnerId,
    string OwnerName,
    uint FactionId,
    int Step,
    int Actions,
    int Hp,
    float X,
    float Y,
    float Z,
    float Yaw,
    uint ZoneId,
    DateTime Spawned);

/// <summary>
/// Test seam over the additive <c>aaemu_game.shipyards</c> table.
/// Production uses <see cref="MySqlShipyardFrameStore"/>; unit tests substitute
/// an in-memory fake, mirroring the DominionManager slice-1 table without its
/// Docker-bound E2E proof (recorded as follow-up).
/// </summary>
public interface IShipyardFrameStore
{
    IReadOnlyList<ShipyardFrameRow> LoadAll();
    void Upsert(ShipyardFrameRow row);
    void Delete(ulong frameId);
}
