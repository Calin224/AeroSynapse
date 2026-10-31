using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroSynapse.Infrastructure.Tests;

// Every test owns a fresh database. The application's configured database is never reset.
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly PostgresSettings admin;
    private readonly string name = "aerosynapse_test_" + Guid.NewGuid().ToString("N");
    private bool created;
    private TestDatabase()
    {
        admin = PostgresSettings.FromEnvironment() with { Database = "postgres" };
        Context = NewContext();
    }

    public TwinDbContext Context { get; }
    public TwinDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TwinDbContext>();
        StorageRegistration.Configure(options, admin with { Database = name }, seedSynthetic: true);
        return new TwinDbContext(options.Options);
    }

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        try
        {
            await database.AdminCommand($"CREATE DATABASE \"{database.name}\"");
            database.created = true;
            await database.Context.Database.MigrateAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private async Task AdminCommand(string sql)
    {
        await using var connection = new NpgsqlConnection(admin.ConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        if (created && name.StartsWith("aerosynapse_test_", StringComparison.Ordinal))
            await AdminCommand($"DROP DATABASE \"{name}\" WITH (FORCE)");
    }
}
