using Npgsql;
using Testcontainers.PostgreSql;

namespace VetPlatform.Api.Tests.TestSupport;

/// <summary>
/// One PostgreSQL server for the whole test run; each fixture gets its own freshly created database on it.
/// Uses the server in the <see cref="ConnectionStringVariable"/> environment variable when set (CI, or a local
/// install without Docker); otherwise starts a throwaway Docker container, which Testcontainers removes afterwards.
/// </summary>
public static class TestPostgres
{
    public const string ConnectionStringVariable = "VETPLATFORM_TEST_POSTGRES";

    // Keep in step with the image in backend/compose.yaml.
    private const string Image = "postgres:18-alpine";

    private static readonly Lazy<Task<string>> ServerConnectionString = new(StartServerAsync);

    /// <summary>Creates an empty database and returns a connection string for it.</summary>
    public static async Task<string> CreateDatabaseAsync()
    {
        var server = await ServerConnectionString.Value;
        var name = $"vetplatform_test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(server))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(await ServerConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> StartServerAsync()
    {
        var external = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            return external;
        }

        var container = new PostgreSqlBuilder(Image).Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Database tests need PostgreSQL: start Docker, or set " + ConnectionStringVariable +
                " to a connection string for a server where the user may create databases.",
                exception);
        }

        return container.GetConnectionString();
    }
}
