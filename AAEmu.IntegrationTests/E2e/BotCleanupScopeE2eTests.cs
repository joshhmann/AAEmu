using System.Text.Json;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

[Collection("e2e")]
public class BotCleanupScopeE2eTests
{
    [Fact]
    [Trait("Category", "e2e")]
    public void MetadataCleanupUsesOwnedAccountsRatherThanCharacterNames()
    {
        // This requires only the already-running isolated MySQL, not a game
        // server. All three names shadow real tables on this connection only.
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("E2E_ROOT")),
            "Select an already-running isolated test lane with E2E_ROOT.");
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(E2eStack.RuntimeGameDir, "Config.Local.json")));
        var database = config.RootElement.GetProperty("Connections").GetProperty("MySQLProvider");
        var connection = new MySqlConnectionStringBuilder
        {
            Server = database.GetProperty("Host").GetString(),
            Port = uint.Parse(database.GetProperty("Port").ToString(), System.Globalization.CultureInfo.InvariantCulture),
            UserID = database.GetProperty("User").GetString(),
            Password = database.GetProperty("Password").GetString(),
            Database = "aaemu_game",
            ConnectionTimeout = 5,
        };
        using var conn = new MySqlConnection(connection.ConnectionString);
        conn.Open();
        try
        {
            Execute(conn, "CREATE TEMPORARY TABLE aaemu_login.users (id INT PRIMARY KEY, username VARCHAR(100))");
            Execute(conn, "CREATE TEMPORARY TABLE characters (id INT PRIMARY KEY, account_id INT, name VARCHAR(100))");
            Execute(conn, "CREATE TEMPORARY TABLE playerbot_metadata (character_id INT PRIMARY KEY)");
            Execute(conn, "INSERT INTO aaemu_login.users VALUES (1, 'owned_one'), (2, 'other_account'), (3, 'owned_two')");
            Execute(conn, "INSERT INTO characters VALUES (11, 1, 'Anders'), (12, 2, 'CitizenUnrelated'), (13, 3, 'Barnaby')");
            Execute(conn, "INSERT INTO playerbot_metadata VALUES (11), (12), (13)");

            E2eStack.DeleteBotMetadata(conn, ["owned_one", "owned_two"]);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT character_id FROM playerbot_metadata ORDER BY character_id";
            using (var rows = cmd.ExecuteReader())
            {
                Assert.True(rows.Read());
                Assert.Equal(12, rows.GetInt32(0));
                Assert.False(rows.Read());
            }
            E2eStack.DeleteBotMetadata(conn, []);
            cmd.CommandText = "SELECT COUNT(*) FROM playerbot_metadata";
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }
        finally
        {
            // TEMPORARY explicitly prevents a cleanup error from dropping any
            // real table. Pooled connections must not retain the test tables.
            Execute(conn, "DROP TEMPORARY TABLE IF EXISTS playerbot_metadata");
            Execute(conn, "DROP TEMPORARY TABLE IF EXISTS characters");
            Execute(conn, "DROP TEMPORARY TABLE IF EXISTS aaemu_login.users");
        }
    }

    private static void Execute(MySqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
