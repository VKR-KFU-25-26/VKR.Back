namespace CourtDecisions.Kafka.Options;

public class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "court-cases";
    public string ClientId { get; set; } = "court-parser";
    public int MessageSendMaxRetries { get; set; } = 3;
    public int RetryBackoffMs { get; set; } = 1000;

    public int BatchSize { get; set; } = 50;
}