using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Production IBotPersistenceDb over the game's MySQL helper
/// (AAEmu.Commons.Utils.DB.MySQL.CreateConnection). One connection per
/// flush cycle; commands share the cycle's transaction.
/// </summary>
public sealed class MySqlBotPersistenceDb : IBotPersistenceDb
{
    private readonly MySqlConnection _connection;
    private MySqlTransaction? _transaction;

    /// <summary>Expects an already-open connection (MySQL.CreateConnection opens it).</summary>
    public MySqlBotPersistenceDb(MySqlConnection connection)
    {
        _connection = connection;
    }

    public Task BeginAsync(CancellationToken ct = default)
    {
        _transaction = _connection.BeginTransaction();
        return Task.CompletedTask;
    }

    public async Task<int> ExecuteNonQueryAsync(string sql, IReadOnlyList<MySqlParameter>? parameters = null, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction != null)
            command.Transaction = _transaction;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Dictionary<string, object>>> QueryAsync(string sql, IReadOnlyList<MySqlParameter>? parameters = null, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction != null)
            command.Transaction = _transaction;
        AddParameters(command, parameters);

        var rows = new List<Dictionary<string, object>>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_transaction == null)
            return;
        await _transaction.CommitAsync(ct).ConfigureAwait(false);
        _transaction = null;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_transaction == null)
            return;
        try
        {
            await _transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _transaction = null;
        }
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
        _connection.Dispose();
    }

    private static void AddParameters(MySqlCommand command, IReadOnlyList<MySqlParameter>? parameters)
    {
        if (parameters == null)
            return;
        command.Parameters.Clear();
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter);
    }
}
