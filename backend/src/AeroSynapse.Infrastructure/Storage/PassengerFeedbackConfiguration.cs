using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class PassengerFeedbackConfiguration : IEntityTypeConfiguration<PassengerFeedbackCount>
{
    public void Configure(EntityTypeBuilder<PassengerFeedbackCount> entity)
    {
        entity.ToTable("passenger_feedback_counts", table =>
        {
            table.HasCheckConstraint("ck_passenger_feedback_choice", "\"Usefulness\" IN ('useful', 'partly', 'not_useful')");
            table.HasCheckConstraint("ck_passenger_feedback_count", "\"Responses\" >= 0");
        });
        entity.HasKey(row => row.Usefulness);
        entity.Property(row => row.Usefulness).HasMaxLength(16);
    }
}
