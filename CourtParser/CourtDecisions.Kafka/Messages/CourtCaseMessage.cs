namespace CourtDecisions.Kafka.Messages;

public class CourtCaseMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string CaseNumber { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string CourtType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}