namespace AeroSynapse.Infrastructure.Audit;

public sealed class AuditOptions
{
    public int RetentionDays { get; set; } = 90;
}
