using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Storage;

public sealed class TwinDbContext(DbContextOptions<TwinDbContext> options) : DbContext(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AiPolicyRecord> AiPolicies => Set<AiPolicyRecord>();
    public DbSet<AiAgentEvent> AiAgentEvents => Set<AiAgentEvent>();
    public DbSet<AgentDecision> AgentDecisions => Set<AgentDecision>();
    public DbSet<ExternalDataSnapshot> ExternalDataSnapshots => Set<ExternalDataSnapshot>();
    protected override void OnConfiguring(DbContextOptionsBuilder builder) => builder
        .AddInterceptors(AeroSynapse.Infrastructure.Audit.AuditCommandInterceptor.Instance)
        .EnableSensitiveDataLogging(false).EnableDetailedErrors(false);
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<Gate> Gates => Set<Gate>();
    public DbSet<CheckInArea> CheckInAreas => Set<CheckInArea>();
    public DbSet<StaffMember> Staff => Set<StaffMember>();
    public DbSet<PassengerRoute> Routes => Set<PassengerRoute>();
    public DbSet<SimulationRun> Runs => Set<SimulationRun>();
    public DbSet<SimulationCommand> SimulationCommands => Set<SimulationCommand>();
    public DbSet<SceneEvent> Events => Set<SceneEvent>();
    public DbSet<LayoutVersion> LayoutVersions => Set<LayoutVersion>();
    public DbSet<Scenario> Scenarios => Set<Scenario>();
    public DbSet<RunMetric> Metrics => Set<RunMetric>();
    public DbSet<RunSnapshot> Snapshots => Set<RunSnapshot>();
    public DbSet<RunRecommendation> Recommendations => Set<RunRecommendation>();
    public DbSet<RecommendationCandidate> RecommendationCandidates => Set<RecommendationCandidate>();
    public DbSet<RecommendationReview> RecommendationReviews => Set<RecommendationReview>();
    public DbSet<RecommendationAudit> RecommendationAudit => Set<RecommendationAudit>();
    public DbSet<OperationalNotification> Notifications => Set<OperationalNotification>();
    public DbSet<NotificationActivity> NotificationActivity => Set<NotificationActivity>();
    public DbSet<NotificationCommand> NotificationCommands => Set<NotificationCommand>();
    public DbSet<AnomalyReview> AnomalyReviews => Set<AnomalyReview>();
    public DbSet<OperationsCursor> OperationsCursors => Set<OperationsCursor>();
    public DbSet<OperationsEvent> OperationsEvents => Set<OperationsEvent>();
    public DbSet<RunProgress> Progress => Set<RunProgress>();
    public DbSet<SimulationBatch> Batches => Set<SimulationBatch>();
    public DbSet<BatchVariant> BatchVariants => Set<BatchVariant>();
    public DbSet<BatchReplication> BatchReplications => Set<BatchReplication>();
    public DbSet<CopilotTranscriptRecord> CopilotTranscripts => Set<CopilotTranscriptRecord>();
    public DbSet<CopilotWorkflowRecord> CopilotWorkflows => Set<CopilotWorkflowRecord>();
    public DbSet<PassengerFeedbackCount> PassengerFeedbackCounts => Set<PassengerFeedbackCount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("twin");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TwinDbContext).Assembly);
        modelBuilder.Entity<Scenario>().Property(row => row.DataSourceJson).HasColumnType("jsonb");
    }
}
