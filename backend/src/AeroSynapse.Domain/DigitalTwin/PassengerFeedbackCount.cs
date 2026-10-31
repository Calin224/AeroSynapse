namespace AeroSynapse.Domain.DigitalTwin;

/// <summary>Aggregate only. No respondent, flight, timestamp, device or profile can be attached.</summary>
public sealed class PassengerFeedbackCount
{
    public string Usefulness { get; set; } = "";
    public long Responses { get; set; }
}
