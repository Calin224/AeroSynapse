using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

public sealed class AiPolicyConfiguration : IEntityTypeConfiguration<AiPolicyRecord>
{
    public void Configure(EntityTypeBuilder<AiPolicyRecord> entity)
    {
        entity.ToTable("ai_policies"); entity.HasKey(row => row.Id);
        entity.HasIndex(row => row.Version).IsUnique();
        entity.Property(row => row.AllowedToolsJson).HasColumnType("jsonb");
        entity.Property(row => row.DailyBudgetUsd).HasPrecision(14, 6);
        entity.Property(row => row.CallReservationUsd).HasPrecision(14, 6);
    }
}
public sealed class AiAgentEventConfiguration : IEntityTypeConfiguration<AiAgentEvent>
{
    public void Configure(EntityTypeBuilder<AiAgentEvent> entity)
    {
        entity.ToTable("ai_agent_events"); entity.HasKey(row => row.Id);
        entity.HasIndex(row => new { row.RecordedAtUtc, row.Kind });
        entity.HasIndex(row => new { row.AttemptId, row.Kind }).IsUnique();
        entity.Property(row => row.ReservedUsd).HasPrecision(14, 6);
    }
}

public sealed class AgentDecisionConfiguration : IEntityTypeConfiguration<AgentDecision>
{
    public void Configure(EntityTypeBuilder<AgentDecision> entity)
    {
        entity.ToTable("agent_decisions"); entity.HasKey(row => row.Id);
        // One decision per point of a run: a retried or reconciled run reuses it instead of asking again.
        entity.HasIndex(row => new { row.RunId, row.Minute, row.Point }).IsUnique();
        entity.HasIndex(row => new { row.RunId, row.Sequence }).IsUnique();
        entity.Property(row => row.Point).HasMaxLength(64);
        entity.Property(row => row.Kind).HasMaxLength(32);
        entity.Property(row => row.OptionId).HasMaxLength(64);
        entity.Property(row => row.ModelStatus).HasMaxLength(32);
        entity.Property(row => row.Model).HasMaxLength(200);
        entity.Property(row => row.Reason).HasMaxLength(300);
        entity.Property(row => row.ActionJson).HasColumnType("jsonb");
        entity.Property(row => row.OptionsJson).HasColumnType("jsonb");
        entity.Property(row => row.ObservationJson).HasColumnType("jsonb");
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(row => row.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
