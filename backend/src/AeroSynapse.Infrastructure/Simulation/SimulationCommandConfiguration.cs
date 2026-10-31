using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Simulation;

internal sealed class SimulationCommandConfiguration : IEntityTypeConfiguration<SimulationCommand>
{
    public void Configure(EntityTypeBuilder<SimulationCommand> entity)
    {
        entity.ToTable("simulation_commands");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        entity.Property(x => x.RequestJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.ErrorCode).HasMaxLength(64);
        // Retain the idempotency tombstone for as long as the run exists; purge cascades it.
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(x => x.RunId).IsUnique();
        entity.HasIndex(x => new { x.FinishedAtUtc, x.NextAttemptAtUtc });
    }
}
