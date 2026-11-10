using Microsoft.Data.SqlClient;

namespace PaymentsModule.Tests;

/// <summary>SQL Server used by the tests. The password never lives in the repo: CI and developers set it by env var.</summary>
internal static class TestDatabase
{
    /// <summary>The catalog is always forced, so a variable pointing at the dev database can never be wiped by a test.</summary>
    public static string ConnectionString(string catalog = "payments_tests") =>
        new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
            ?? throw new InvalidOperationException(
                "Set the ConnectionStrings__SqlServer environment variable to run the database tests (see README)."))
        {
            InitialCatalog = catalog,
        }.ConnectionString;
}
