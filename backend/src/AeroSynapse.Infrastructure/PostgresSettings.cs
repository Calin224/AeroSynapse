using Npgsql;

namespace AeroSynapse.Infrastructure;

public sealed record PostgresSettings(string Host, int Port, string Database, string Username, string Password)
{
    public string ConnectionString(int commandTimeout = 30) => new NpgsqlConnectionStringBuilder
    {
        Host = Host, Port = Port, Database = Database, Username = Username, Password = Password,
        Timeout = 3, CommandTimeout = commandTimeout
    }.ConnectionString;

    public static PostgresSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("Postgres__Host") ?? "localhost",
        int.Parse(Environment.GetEnvironmentVariable("Postgres__Port") ?? "5432"),
        Environment.GetEnvironmentVariable("Postgres__Database") ?? "aerosynapse",
        Environment.GetEnvironmentVariable("Postgres__Username") ?? "aerosynapse",
        Environment.GetEnvironmentVariable("Postgres__Password") ?? "local-demo-only");
}
