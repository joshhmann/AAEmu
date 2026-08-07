using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Thin DB seam over the game's MySQL connection idiom (MySQL.CreateConnection),
/// so the persistence manager is testable against an in-memory recording fake
/// (AAEmu.UnitTests/Utils/Mocks/BotPersistenceDbMock) without a live server.
///
/// Write flow: BeginAsync → ExecuteNonQueryAsync* → CommitAsync (or
/// RollbackAsync). Read flow: QueryAsync without Begin.
/// </summary>
public interface IBotPersistenceDb : IDisposable
{
    /// <summary>Opens a connection and starts a transaction (writes only).</summary>
    Task BeginAsync(CancellationToken ct = default);

    /// <summary>Executes one non-query statement inside the transaction (or standalone when none started).</summary>
    Task<int> ExecuteNonQueryAsync(string sql, IReadOnlyList<MySqlParameter>? parameters = null, CancellationToken ct = default);

    /// <summary>Executes a query and returns all rows as name→value dictionaries.</summary>
    Task<List<Dictionary<string, object>>> QueryAsync(string sql, IReadOnlyList<MySqlParameter>? parameters = null, CancellationToken ct = default);

    /// <summary>Commits the transaction.</summary>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>Rolls the transaction back (dirty flags are kept, so the next cycle retries).</summary>
    Task RollbackAsync(CancellationToken ct = default);
}
