using CourtDecisions.Kafka.Messages;

namespace CourtDecisions.Kafka.Abstraction;

/// <summary>
/// Интерфейс для взаимодействия с кафкой
/// </summary>
public interface IKafkaProducer
{
    Task ProduceAsync(string topic, CourtCaseMessage message);
    Task ProduceBatchAsync(string topic, List<CourtCaseMessage> messages);
    Task ProduceSingleMockMessageAsync(string topic);
}