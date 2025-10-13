using System.Text.Json;
using Confluent.Kafka;
using CourtDecisions.Kafka.Abstraction;
using CourtDecisions.Kafka.KafkaHelpers;
using CourtDecisions.Kafka.Messages;
using CourtDecisions.Kafka.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CourtParser.Infrastructure.Producers;

public class KafkaCourtMessageProducer : IKafkaProducer
{
    private readonly IProducer<Null, string> _producer;
    private readonly KafkaOptions _config;
    private readonly ILogger<KafkaCourtMessageProducer> _logger;
    private readonly KafkaTopicHelpers _topicHelpers;
    private readonly HashSet<string> _verifiedTopics = [];

    public KafkaCourtMessageProducer(
        IOptions<KafkaOptions> config, 
        ILogger<KafkaCourtMessageProducer> logger,
        KafkaTopicHelpers topicHelpers)
    {
        _config = config.Value;
        _logger = logger;
        _topicHelpers = topicHelpers;

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _config.BootstrapServers,
            ClientId = _config.ClientId,
            MessageSendMaxRetries = _config.MessageSendMaxRetries,
            RetryBackoffMs = _config.RetryBackoffMs,
        };

        _producer = new ProducerBuilder<Null, string>(producerConfig)
            .SetErrorHandler(OnProducerError)
            .Build();
    }

    public async Task ProduceAsync(string topic, CourtCaseMessage message)
    {
        await EnsureTopicExistsAsync(topic);
        
        try
        {
            var jsonMessage = JsonSerializer.Serialize(message, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var kafkaMessage = new Message<Null, string> 
            { 
                Value = jsonMessage,
                Timestamp = new Timestamp(DateTime.UtcNow)
            };

            var result = await _producer.ProduceAsync(topic, kafkaMessage);
            
            _logger.LogDebug("Сообщение отправлено в топик {Topic}, partition {Partition}, offset {Offset}",
                result.Topic, result.Partition.Value, result.Offset.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка отправки сообщения в топик {Topic}", topic);
            throw;
        }
    }

    public async Task ProduceBatchAsync(string topic, List<CourtCaseMessage> messages)
    {
        await EnsureTopicExistsAsync(topic);
        
        var sendTasks = messages.Select(message => ProduceAsync(topic, message));
        
        try
        {
            await Task.WhenAll(sendTasks);
            _logger.LogInformation("Успешно отправлено {Count} сообщений в топик {Topic}", 
                messages.Count, topic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибки при отправке batch в топик {Topic}", topic);
            throw;
        }
    }

    public async Task ProduceBatchWithRetryAsync(string topic, List<CourtCaseMessage> messages, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await ProduceBatchAsync(topic, messages);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "Попытка {Attempt}/{MaxRetries} не удалась. Повтор через 2 секунды...", 
                    attempt, maxRetries);
                await Task.Delay(2000);
            }
        }
        
        throw new Exception($"Не удалось отправить batch после {maxRetries} попыток");
    }

    private async Task EnsureTopicExistsAsync(string topic, bool forceCheck = false)
    {
        if (_verifiedTopics.Contains(topic) && !forceCheck)
            return;

        await _topicHelpers.EnsureTopicExistsAsync(
            _config.BootstrapServers, 
            topic,
            numPartitions: 3,
            replicationFactor: 1);
        
        _verifiedTopics.Add(topic);
    }

    private void OnProducerError(IProducer<Null, string> producer, Error error)
    {
        _logger.LogError("Kafka producer error: {Reason} (Code: {Code})", error.Reason, error.Code);
    }

    public void Dispose()
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
    }
}