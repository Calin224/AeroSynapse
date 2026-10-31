using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AeroSynapse.Infrastructure.Identity;

public sealed class WorkspaceUser : IdentityUser
{
    public bool Disabled { get; set; }
    public string TenantId { get; set; } = "demo";
}

public sealed class WorkspaceSession
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string SecurityStamp { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

// Separate from simulation storage: authentication also protects the SQLite map API
// when PostgreSQL-backed simulation features are not configured.
public sealed class IdentityStore(DbContextOptions<IdentityStore> options)
    : IdentityDbContext<WorkspaceUser>(options)
{
    public DbSet<WorkspaceSession> Sessions => Set<WorkspaceSession>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<WorkspaceUser>().Property(user => user.TenantId).HasMaxLength(40).HasDefaultValue("demo");
        builder.Entity<WorkspaceSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.Property(session => session.SecurityStamp).HasMaxLength(256);
            // SQLite stores TEXT without a kind; restore UTC so the API serializes "...Z" and
            // browsers outside UTC do not treat a fresh session as already expired.
            entity.Property(session => session.ExpiresAtUtc)
                .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            entity.HasIndex(session => session.ExpiresAtUtc);
            entity.HasOne<WorkspaceUser>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class IdentityStoreFactory : IDesignTimeDbContextFactory<IdentityStore>
{
    public IdentityStore CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<IdentityStore>()
        .UseSqlite("Data Source=identity.db").Options);
}
