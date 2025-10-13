using CourtDecisions.Kafka.Abstraction;
using CourtDecisions.Kafka.Messages;
using CourtDecisions.Kafka.Options;
using CourtParser.Core.Interfaces;
using CourtParser.Infrastructure.Parsers;
using Microsoft.Extensions.Options;

namespace CourtParser.Worker;

public class Worker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Worker> _logger;
    private readonly KafkaOptions _kafkaConfig;

    public Worker(IServiceProvider serviceProvider, ILogger<Worker> logger, IOptions<KafkaOptions> kafkaConfig)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _kafkaConfig = kafkaConfig.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Парсинг воркер запущен");

        // Ждем немного перед первым запуском
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();

                var kafkaProducer = scope.ServiceProvider.GetRequiredService<IKafkaProducer>();
                
                // Получаем ВСЕ парсеры
                var parsers = scope.ServiceProvider.GetServices<IParser>().ToList();
                
                _logger.LogInformation("Найдено парсеров: {Count}", parsers.Count);
                
                // Запускаем только CourtDecisionsParser
                var courtDecisionsParser = parsers.FirstOrDefault(p => p is CourtDecisionsParser);
                
                if (courtDecisionsParser != null)
                {
                    try
                    {
                        _logger.LogInformation("Запуск парсера: {ParserName}", nameof(CourtDecisionsParser));
                        
                        // Передаем cancellation token в парсер
                        var cases = await courtDecisionsParser.ParseCasesAsync(1);
                        
                        _logger.LogInformation("Парсер {ParserName} нашел {Count} дел", 
                            nameof(CourtDecisionsParser), cases.Count);

                        if (cases.Count > 0)
                        {
                            var messages = cases.Select(c => new CourtCaseMessage
                            {
                                Title = c.Title,
                                CaseNumber = c.CaseNumber,
                                Link = c.Link,
                                CourtType = c.CourtType,
                                Description = c.Description,
                                Subject = c.Subject,
                                Timestamp = DateTime.UtcNow
                            }).ToList();

                            await kafkaProducer.ProduceBatchAsync(_kafkaConfig.Topic, messages);
                            _logger.LogInformation("Отправлено в Kafka: {Count} дел", messages.Count);
                            PrintResult(messages);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка в парсере {ParserName}", nameof(CourtDecisionsParser));
                    }
                }
                else
                {
                    _logger.LogWarning("Парсер CourtDecisionsParser не найден среди зарегистрированных сервисов");
                }

                
                await WaitWithCancellation(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Работа воркера корректно остановлена");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в worker");
                // Ждем 5 минут перед повторной попыткой с проверкой отмены
                await WaitWithCancellation(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
        _logger.LogInformation("Парсинг воркер остановлен");
    }

    private async Task WaitWithCancellation(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Задержка прервана - приложение завершает работу");
            throw new OperationCanceledException();
        }
    }

    private void PrintResult(List<CourtCaseMessage> messages)
    {
        if (messages.Count == 0)
        {
            Console.WriteLine("❌ Дела не найдены");
            return;
        }

        Console.WriteLine($"\n🎯 Найдено дел: {messages.Count}\n");
        
        for (var i = 0; i < 5; i++)
        {
            var message = messages[i];
            Console.WriteLine($"📋 Дело #{i + 1}");
            Console.WriteLine($"🏛️  Суд: {message.CourtType}");
            Console.WriteLine($"🔢 Номер дела: {message.CaseNumber}");
            Console.WriteLine($"📝 {message.Description}");
            Console.WriteLine($"👥 {message.Subject}");
            Console.WriteLine($"🔗 Ссылка: {message.Link}");
            Console.WriteLine("─".PadRight(60, '─'));
        }
    }
}