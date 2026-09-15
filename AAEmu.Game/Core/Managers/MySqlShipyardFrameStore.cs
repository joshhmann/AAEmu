using AAEmu.Commons.Utils.DB;

using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// MySQL <c>aaemu_game.shipyards</c> implementation of <see cref="IShipyardFrameStore"/>.
/// Parameterized text-protocol commands only: NEVER call Prepare() here —
/// MySql.Data 9.7.0 NREs on parameterless prepared statements.
/// </summary>
public sealed class MySqlShipyardFrameStore : IShipyardFrameStore
{
    public IReadOnlyList<ShipyardFrameRow> LoadAll()
    {
        var rows = new List<ShipyardFrameRow>();
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM shipyards";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ShipyardFrameRow(
                reader.GetUInt64("id"),
                reader.GetUInt32("template_id"),
                reader.GetUInt32("owner_id"),
                reader.GetString("owner_name"),
                reader.GetUInt32("faction_id"),
                reader.GetInt32("step"),
                reader.GetInt32("actions"),
                reader.GetInt32("hp"),
                reader.GetFloat("x"),
                reader.GetFloat("y"),
                reader.GetFloat("z"),
                reader.GetFloat("yaw"),
                reader.GetUInt32("zone_id"),
                reader.GetDateTime("spawned")));
        }
        return rows;
    }

    public void Upsert(ShipyardFrameRow row)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO shipyards (id, template_id, owner_id, owner_name, faction_id, step, actions, hp, x, y, z, yaw, zone_id, spawned) " +
            "VALUES (@id, @template_id, @owner_id, @owner_name, @faction_id, @step, @actions, @hp, @x, @y, @z, @yaw, @zone_id, @spawned) " +
            "ON DUPLICATE KEY UPDATE template_id = @template_id, owner_id = @owner_id, owner_name = @owner_name, " +
            "faction_id = @faction_id, step = @step, actions = @actions, hp = @hp, " +
            "x = @x, y = @y, z = @z, yaw = @yaw, zone_id = @zone_id, spawned = @spawned";
        command.Parameters.AddWithValue("@id", row.FrameId);
        command.Parameters.AddWithValue("@template_id", row.TemplateId);
        command.Parameters.AddWithValue("@owner_id", row.OwnerId);
        command.Parameters.AddWithValue("@owner_name", row.OwnerName);
        command.Parameters.AddWithValue("@faction_id", row.FactionId);
        command.Parameters.AddWithValue("@step", row.Step);
        command.Parameters.AddWithValue("@actions", row.Actions);
        command.Parameters.AddWithValue("@hp", row.Hp);
        command.Parameters.AddWithValue("@x", row.X);
        command.Parameters.AddWithValue("@y", row.Y);
        command.Parameters.AddWithValue("@z", row.Z);
        command.Parameters.AddWithValue("@yaw", row.Yaw);
        command.Parameters.AddWithValue("@zone_id", row.ZoneId);
        command.Parameters.AddWithValue("@spawned", row.Spawned);
        command.ExecuteNonQuery();
    }

    public void Delete(ulong frameId)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM shipyards WHERE id = @id";
        command.Parameters.AddWithValue("@id", frameId);
        command.ExecuteNonQuery();
    }
}
