using AeroSynapse.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroSynapse.Infrastructure.Tests;

public sealed class IdentityStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Session_expiry_reads_back_as_utc_and_the_migrated_model_has_no_pending_changes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Token);
        var options = new DbContextOptionsBuilder<IdentityStore>().UseSqlite(connection).Options;
        var expires = new DateTime(2026, 10, 26, 17, 51, 50, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        await using (var store = new IdentityStore(options))
        {
            await store.Database.MigrateAsync(Token);
            Assert.False(store.Database.HasPendingModelChanges());
            var user = new WorkspaceUser { UserName = "operator.demo" };
            store.Users.Add(user);
            store.Sessions.Add(new WorkspaceSession { Id = id, UserId = user.Id, SecurityStamp = "stamp", ExpiresAtUtc = expires });
            await store.SaveChangesAsync(Token);
        }
        await using (var store = new IdentityStore(options))
        {
            var session = await store.Sessions.AsNoTracking().SingleAsync(item => item.Id == id, Token);
            // An unspecified kind serializes without "Z" and browsers east of UTC read it as already expired.
            Assert.Equal(DateTimeKind.Utc, session.ExpiresAtUtc.Kind);
            Assert.Equal(expires, session.ExpiresAtUtc);
        }
    }
}
