using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PaymentsModule.Tests;

public sealed class SimulatedDatabaseTimeout() : DbException(FailingQueries.Message);

/// <summary>
/// Fails, as a SQL Server timeout would, every query that receives one of the chosen values as a parameter.
/// Keyed by value (e.g. a folio) instead of an on/off switch, so tests sharing one app can run in parallel.
/// </summary>
public sealed class FailingQueries : DbCommandInterceptor
{
    public const string Message = "Simulated SQL Server timeout.";

    private readonly ConcurrentDictionary<string, byte> _values = new();

    public void FailQueriesWith(string parameterValue) => _values.TryAdd(parameterValue, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ThrowIfChosen(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        ThrowIfChosen(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void ThrowIfChosen(DbCommand command)
    {
        if (command.Parameters.Cast<DbParameter>().Any(p => p.Value is string value && _values.ContainsKey(value)))
        {
            throw new SimulatedDatabaseTimeout();
        }
    }
}
