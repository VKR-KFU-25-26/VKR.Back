using CourtParser.Models.Entities;

namespace CourtDecisions.Kafka.Messages;

public class CourtCaseMessage : CourtCase
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}