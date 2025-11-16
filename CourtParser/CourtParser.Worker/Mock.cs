using CourtDecisions.Kafka.Abstraction;
using CourtDecisions.Kafka.Options;
using Microsoft.Extensions.Options;

namespace CourtParser.Worker;

public class Mock : BackgroundService
{
    private readonly ILogger<Mock> _logger;
    private readonly KafkaOptions _kafkaOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30); 

    public Mock(
        ILogger<Mock> logger,
        IOptions<KafkaOptions> kafkaOptions,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _kafkaOptions = kafkaOptions.Value;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Kafka Test Data Producer Service started");

        // Ждем немного перед началом работы
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var kafkaProducer = scope.ServiceProvider.GetRequiredService<IKafkaProducer>();

                    _logger.LogInformation("📤 Sending test messages to Kafka topic: {Topic}", _kafkaOptions.Topic);

                    // Отправляем тестовые сообщения
                    await kafkaProducer.ProduceSingleMockMessageAsync(_kafkaOptions.Topic);
                }

                _logger.LogInformation("✅ Test messages sent successfully. Waiting for next interval...");

                // Ждем перед следующей отправкой
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("🛑 Kafka Test Data Producer Service stopped");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in Kafka Test Data Producer Service");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); // Ждем перед повторной попыткой
            }
        }

        _logger.LogInformation("🔚 Kafka Test Data Producer Service finished");
    }
}