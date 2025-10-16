using System.Text.RegularExpressions;
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

        var testRegions = new List<string>
        {
            "Калмыкия"
        };
        
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
                        var cases = await courtDecisionsParser.ParseCasesAsync(testRegions, 1);
                        
                        _logger.LogInformation("Парсер {ParserName} нашел {Count} дел", 
                            nameof(CourtDecisionsParser), cases.Count);

                        if (cases.Count > 0)
                        {
                            var messages = cases.Select(c => new CourtCaseMessage
                            {
                                
                                Title = c.Title,
                                Link = c.Link,
                                CaseNumber = c.CaseNumber,
                                CourtType = c.CourtType,
                                Description = c.Description,
                                Subject = c.Subject,
                                Timestamp = DateTime.UtcNow,
                                HasDecision = c.HasDecision,
                                DecisionLink = c.DecisionLink,
                                DecisionDate = c.DecisionDate,
                                DecisionType = c.DecisionType,
                                FederalDistrict = c.FederalDistrict,
                                Region = c.Region,
                                Plaintiff = c.Plaintiff,
                                Defendant = c.Defendant,
                                ReceivedDate = c.ReceivedDate,
                                CaseCategory = c.CaseCategory,
                                CaseSubcategory = c.CaseSubcategory,
                                DecisionContent = c.DecisionContent
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
    
        var casesWithDecisions = messages.Count(m => m.HasDecision);
        var embeddedDecisions = messages.Count(m => m.HasDecision && m.DecisionLink?.Contains("#embedded_decision") == true);
        var externalDecisions = messages.Count(m => m.HasDecision && m.DecisionLink?.Contains("#embedded_decision") == false);

        Console.WriteLine($"📊 Статистика:");
        Console.WriteLine($"   • Всего дел: {messages.Count}");
        Console.WriteLine($"   • С решениями: {casesWithDecisions}");
        Console.WriteLine($"   • Встроенных решений: {embeddedDecisions}");
        Console.WriteLine($"   • Внешних документов: {externalDecisions}");
        Console.WriteLine();

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            Console.WriteLine($"📋 Дело #{i + 1}");
            Console.WriteLine($"🏛️  Суд: {message.CourtType}");
            Console.WriteLine($"🔢 Номер дела: {message.CaseNumber}");
        
            if (!string.IsNullOrEmpty(message.FederalDistrict))
            {
                Console.WriteLine($"🗺️  Федеральный округ: {message.FederalDistrict}");
            }
        
            if (!string.IsNullOrEmpty(message.Region))
            {
                Console.WriteLine($"📍 Регион: {message.Region}");
            }
        
            if (!string.IsNullOrEmpty(message.CaseCategory))
            {
                Console.WriteLine($"📂 Категория: {message.CaseCategory}");
            }
        
            if (!string.IsNullOrEmpty(message.CaseSubcategory))
            {
                Console.WriteLine($"📁 Подкатегория: {message.CaseSubcategory}");
            }
        
            if (message.ReceivedDate.HasValue)
            {
                Console.WriteLine($"📨 Дата поступления: {message.ReceivedDate.Value:dd.MM.yyyy}");
            }
        
            if (!string.IsNullOrEmpty(message.Description))
            {
                Console.WriteLine($"📝 Описание: {TruncateText(message.Description, 80)}");
            }
        
            if (!string.IsNullOrEmpty(message.Plaintiff))
            {
                Console.WriteLine($"👤 Истец: {TruncateText(message.Plaintiff, 70)}");
            }
        
            if (!string.IsNullOrEmpty(message.Defendant))
            {
                Console.WriteLine($"⚖️  Ответчик: {TruncateText(message.Defendant, 70)}");
            }
        
            if (!string.IsNullOrEmpty(message.Subject) && message.Subject.Length > 50)
            {
                Console.WriteLine($"📄 Предмет: {TruncateText(message.Subject, 80)}");
            }
        
            Console.WriteLine($"🔗 Ссылка на дело: {message.Link}");
    
            if (message.HasDecision)
            {
                var decisionIcon = message.DecisionLink?.Contains("#embedded_decision") == true ? "📄" : "📎";
            
                Console.WriteLine($"✅ {decisionIcon} {message.DecisionType?.ToUpper()} НАЙДЕНО");
            
                if (message.DecisionDate.HasValue)
                {
                    Console.WriteLine($"📅 Дата решения: {message.DecisionDate.Value:dd.MM.yyyy}");
                }
            
                if (message.DecisionLink?.Contains("#embedded_decision") == true)
                {
                    Console.WriteLine($"💾 Тип: Встроенное решение в HTML");
                    Console.WriteLine($"🔗 Ссылка: {message.Link} (решение на странице)");
                
                    // Показываем превью содержимого решения
                    if (!string.IsNullOrEmpty(message.DecisionContent))
                    {
                        var preview = GetDecisionPreview(message.DecisionContent);
                        Console.WriteLine($"📋 Превью решения:");
                        Console.WriteLine($"   {preview}");
                    }
                }
                else
                {
                    Console.WriteLine($"💾 Тип: Отдельный документ");
                    Console.WriteLine($"🔗 Скачать: {message.DecisionLink}");
                }
            }
            else
            {
                Console.WriteLine($"❌ Решение не найдено");
            
                if (!string.IsNullOrEmpty(message.DecisionType) && message.DecisionType != "Не найдено")
                {
                    Console.WriteLine($"ℹ️  Статус: {message.DecisionType}");
                }
            }

            // Разделитель между делами
            if (i < messages.Count - 1)
            {
                Console.WriteLine("".PadRight(80, '─'));
            }
            else
            {
                Console.WriteLine("".PadRight(80, '═'));
            }
        }

        // Итоговая статистика
        Console.WriteLine($"\n📈 ИТОГ:");
        Console.WriteLine($"   ✅ Дела с решениями: {casesWithDecisions}/{messages.Count}");
        Console.WriteLine($"   📄 Встроенные решения: {embeddedDecisions}");
        Console.WriteLine($"   📎 Отдельные документы: {externalDecisions}");
    
        if (casesWithDecisions > 0)
        {
            var successRate = (double)casesWithDecisions / messages.Count * 100;
            Console.WriteLine($"   📊 Эффективность: {successRate:F1}%");
        }
    }

// Вспомогательные методы

    /// <summary>
    /// Обрезает текст до указанной длины и добавляет многоточие
    /// </summary>
    private string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        
        return text.Substring(0, maxLength - 3) + "...";
    }

    /// <summary>
    /// Создает превью содержимого решения
    /// </summary>
    private string GetDecisionPreview(string content)
    {
        if (string.IsNullOrEmpty(content))
            return "Содержимое недоступно";
        
        // Берем первые 100 символов и очищаем от лишних пробелов
        var preview = content.Length > 100 
            ? content.Substring(0, 100) + "..." 
            : content;
        
        // Убираем лишние пробелы и переносы
        preview = Regex.Replace(preview, @"\s+", " ").Trim();
    
        return preview;
    }
}