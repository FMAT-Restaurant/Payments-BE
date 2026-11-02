using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PaymentsModule.Tests;

public sealed class SimulatedDatabaseTimeout() : DbException(FailingQueries.Message);

/// <summary>
/// Fails, as a SQL Server timeout would, every query that receives one of the chosen values as a parameter: either
/// before it runs (<see cref="FailQueriesWith"/>) or after a number of rows were read (<see cref="FailReadsAfter"/>).
/// Keyed by value (e.g. a folio or a date) instead of an on/off switch, so tests sharing one app can run in parallel.
/// </summary>
public sealed class FailingQueries : DbCommandInterceptor
{
    public const string Message = "Simulated SQL Server timeout.";

    private readonly ConcurrentDictionary<object, byte> _values = new();
    private readonly ConcurrentDictionary<object, int> _failAfterRows = new();

    public void FailQueriesWith(object parameterValue) => _values.TryAdd(parameterValue, 0);

    /// <summary>The query starts normally and its reader throws once <paramref name="rows"/> rows were read.</summary>
    public void FailReadsAfter(object parameterValue, int rows) => _failAfterRows[parameterValue] = rows;

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

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) =>
        WrapIfChosen(command, base.ReaderExecuted(command, eventData, result));

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default) =>
        WrapIfChosen(command, await base.ReaderExecutedAsync(command, eventData, result, cancellationToken));

    private void ThrowIfChosen(DbCommand command)
    {
        if (ParameterValues(command).Any(_values.ContainsKey))
        {
            throw new SimulatedDatabaseTimeout();
        }
    }

    private DbDataReader WrapIfChosen(DbCommand command, DbDataReader result)
    {
        foreach (var value in ParameterValues(command))
        {
            if (_failAfterRows.TryGetValue(value, out var rows))
            {
                return new FailingReader(result, rows, () => new SimulatedDatabaseTimeout());
            }
        }

        return result;
    }

    private static IEnumerable<object> ParameterValues(DbCommand command) =>
        command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<object>().Where(v => v is not DBNull);
}
