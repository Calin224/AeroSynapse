using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class OperationalNotificationConfiguration : IEntityTypeConfiguration<OperationalNotification>
{
    public void Configure(EntityTypeBuilder<OperationalNotification> entity)
    {
        Mapping.Base(entity, "operational_notifications");
        entity.Property(x => x.ScenarioName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.ZoneName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Source).HasMaxLength(32).IsRequired();
        entity.Property(x => x.RuleKey).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Level).HasMaxLength(16).IsRequired();
        entity.Property(x => x.Assignee).HasMaxLength(160).IsRequired();
        entity.Property(x => x.AcknowledgedBy).HasMaxLength(160).IsRequired();
        entity.Property(x => x.EvidenceJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<Scenario>().WithMany().HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.RunId, x.ZoneId, x.RuleKey }).IsUnique();
        entity.HasIndex(x => new { x.ScenarioId, x.LastObservedAtUtc, x.Id });
        entity.ToTable(t => {
            t.HasCheckConstraint("ck_notification_level", "\"Level\" IN ('warning', 'critical', 'resolved')");
            t.HasCheckConstraint("ck_notification_synthetic", "\"IsSynthetic\"");
            t.HasCheckConstraint("ck_notification_rule", "\"WarningThreshold\" >= 0 AND \"CriticalThreshold\" > \"WarningThreshold\" AND \"CriticalThreshold\" <= 100000 AND (\"HorizonMinutes\" BETWEEN 15 AND 30 OR (\"Source\" = 'recorded_queue' AND \"HorizonMinutes\" = 0))");
            t.HasCheckConstraint("ck_notification_counts", "\"Revision\" > 0 AND \"Observations\" > 0 AND \"LatestFromMinute\" >= 0");
        });
    }
}
internal sealed class NotificationActivityConfiguration : IEntityTypeConfiguration<NotificationActivity>
{
    public void Configure(EntityTypeBuilder<NotificationActivity> entity)
    {
        Mapping.Base(entity, "notification_activity");
        entity.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        entity.Property(x => x.Actor).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Text).HasMaxLength(2000).IsRequired();
        entity.Property(x => x.ObservationKey).HasMaxLength(64);
        entity.Property(x => x.EvidenceJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<OperationalNotification>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.AlertId, x.AlertRevision }).IsUnique();
        entity.HasIndex(x => new { x.AlertId, x.ObservationKey }).IsUnique();
        entity.HasIndex(x => new { x.RecordedAtUtc, x.Id });
        entity.ToTable(t => t.HasCheckConstraint("ck_notification_activity_synthetic", "\"IsSynthetic\""));
    }
}
internal sealed class NotificationCommandConfiguration : IEntityTypeConfiguration<NotificationCommand>
{
    public void Configure(EntityTypeBuilder<NotificationCommand> entity)
    {
        Mapping.Base(entity, "notification_commands");
        entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        entity.Property(x => x.ReceiptJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<Scenario>().WithMany().HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(t => t.HasCheckConstraint("ck_notification_command_synthetic", "\"IsSynthetic\""));
    }
}
