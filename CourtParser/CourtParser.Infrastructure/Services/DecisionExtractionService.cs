using CourtParser.Models.Entities;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Text.RegularExpressions;

namespace CourtParser.Infrastructure.Services;

public class DecisionExtractionService(ILogger<DecisionExtractionService> logger)
{
    public async Task CheckAndExtractDecisionAsync(IPage page, CourtCase courtCase, CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("Проверяем наличие решения для дела: {CaseNumber}", courtCase.CaseNumber);
        
            var navigationTask = page.WaitForNavigationAsync(new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle2],
                Timeout = 30000
            });
        
            await page.GoToAsync(courtCase.Link, WaitUntilNavigation.Networkidle2);
            await navigationTask;
            await Task.Delay(2000, cancellationToken);

            // 1. Сначала извлекаем детальную информацию о деле
            await ExtractDetailedCaseInfo(page, courtCase);
        
            // 2. Проверяем наличие встроенного решения в HTML
            bool embeddedDecisionFound = await ExtractEmbeddedDecisionAsync(page, courtCase);
            
            // 3. Если встроенного решения нет, ищем ссылки на отдельные документы
            if (!embeddedDecisionFound)
            {
                bool externalDecisionFound = await ExtractDecisionFromMainBlock(page, courtCase);
                
                if (!externalDecisionFound)
                {
                    externalDecisionFound = await FindDecisionLinksAlternativeAsync(page, courtCase);
                }
            }
            
            if (!embeddedDecisionFound && !courtCase.HasDecision)
            {
                courtCase.HasDecision = false;
                courtCase.DecisionLink = string.Empty;
                courtCase.DecisionType = "Не найдено";
                logger.LogInformation("❌ Для дела {CaseNumber} решение не найдено", courtCase.CaseNumber);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при проверке решения для дела {CaseNumber}", courtCase.CaseNumber);
            courtCase.HasDecision = false;
            courtCase.DecisionLink = string.Empty;
            courtCase.DecisionType = "Ошибка при проверке";
        }
    }
   
   
   
    /// <summary>
    /// Извлекает встроенное решение прямо из HTML страницы
    /// </summary>
    private async Task<bool> ExtractEmbeddedDecisionAsync(IPage page, CourtCase courtCase)
    {
        try
        {
            logger.LogInformation("Ищем встроенное решение в HTML для дела {CaseNumber}", courtCase.CaseNumber);

            // 1. Ищем блоки с решениями/определениями по различным селекторам
            var possibleDecisionContainers = new[]
            {
                ".col-md-12", // из вашего примера
                ".decision-content",
                ".court-decision", 
                ".document-content",
                "blockquote[itemprop='text']",
                ".document-text",
                "#decisionText",
                ".judicial-act"
            };

            foreach (var selector in possibleDecisionContainers)
            {
                var container = await page.QuerySelectorAsync(selector);
                if (container != null)
                {
                    var containerText = await container.EvaluateFunctionAsync<string>("el => el.textContent");
                    if (!string.IsNullOrEmpty(containerText) && IsJudicialDocument(containerText))
                    {
                        logger.LogInformation("Найден контейнер с судебным документом: {Selector}", selector);
                        
                        var documentType = DetermineDocumentTypeFromContent(containerText);
                        var documentContent = await ExtractStructuredDecisionContent(container, containerText);
                        
                        courtCase.HasDecision = true;
                        courtCase.DecisionLink = courtCase.Link + "#embedded_decision"; // маркер встроенного документа
                        courtCase.DecisionType = documentType;
                        courtCase.DecisionContent = documentContent; // новое поле для хранения содержимого
                        courtCase.DecisionDate = ExtractDateFromDecisionContent(containerText);
                        
                        logger.LogInformation("✅ Найдено встроенное {DocumentType} для дела {CaseNumber}", 
                            documentType, courtCase.CaseNumber);
                        
                        return true;
                    }
                }
            }

            // 2. Альтернативный поиск по текстовому содержанию
            var pageContent = await page.GetContentAsync();
            if (IsJudicialDocument(pageContent))
            {
                var documentType = DetermineDocumentTypeFromContent(pageContent);
                courtCase.HasDecision = true;
                courtCase.DecisionLink = courtCase.Link;
                courtCase.DecisionType = documentType;
                courtCase.DecisionContent = CleanText(pageContent);
                courtCase.DecisionDate = ExtractDateFromDecisionContent(pageContent);
                
                logger.LogInformation("✅ Найдено встроенное {DocumentType} в теле страницы для дела {CaseNumber}", 
                    documentType, courtCase.CaseNumber);
                
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при поиске встроенного решения для дела {CaseNumber}", courtCase.CaseNumber);
            return false;
        }
    }

    /// <summary>
    /// Определяет, является ли текст судебным документом
    /// </summary>
    private bool IsJudicialDocument(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var cleanText = text.ToLower();
        
        // Ключевые слова, характерные для судебных документов
        var judicialKeywords = new[]
        {
            "определение",
            "решение",
            "постановление", 
            "приказ",
            "суд ",
            "исковое заявление",
            "дело №",
            "председательствующий",
            "судья",
            "рассмотрев",
            "установил",
            "определил",
            "постановил"
        };

        return judicialKeywords.Any(keyword => cleanText.Contains(keyword));
    }

    /// <summary>
    /// Определяет тип документа на основе содержимого
    /// </summary>
    private string DetermineDocumentTypeFromContent(string content)
    {
        var cleanContent = content.ToLower();
        
        if (cleanContent.Contains("определение") && cleanContent.Contains("об оставлении искового заявления без рассмотрения"))
            return "Определение об оставлении искового заявления без рассмотрения";
        else if (cleanContent.Contains("определение") && cleanContent.Contains("о прекращении"))
            return "Определение о прекращении производства по делу";
        else if (cleanContent.Contains("определение"))
            return "Определение";
        else if (cleanContent.Contains("решение") && cleanContent.Contains("мотивированное"))
            return "Мотивированное решение";
        else if (cleanContent.Contains("решение"))
            return "Решение";
        else if (cleanContent.Contains("постановление"))
            return "Постановление";
        else if (cleanContent.Contains("приказ"))
            return "Судебный приказ";
        else
            return "Судебный документ";
    }

    /// <summary>
    /// Извлекает структурированное содержимое решения
    /// </summary>
    private async Task<string> ExtractStructuredDecisionContent(IElementHandle container, string containerText)
    {
        try
        {
            // Пытаемся извлечь структурированные данные
            var structuredData = new Dictionary<string, string>();
            
            // Извлекаем заголовок
            var titleElement = await container.QuerySelectorAsync("h3, h4, .text-center");
            if (titleElement != null)
            {
                structuredData["Заголовок"] = await titleElement.EvaluateFunctionAsync<string>("el => el.textContent");
            }

            // Извлекаем дату
            var dateMatch = Regex.Match(containerText, @"\d{1,2}\s+[а-я]+\s+\d{4}\s+года", RegexOptions.IgnoreCase);
            if (dateMatch.Success)
            {
                structuredData["Дата"] = dateMatch.Value;
            }

            // Извлекаем номер дела
            var caseNumberMatch = Regex.Match(containerText, @"дело\s*№?\s*[^\s,]+", RegexOptions.IgnoreCase);
            if (caseNumberMatch.Success)
            {
                structuredData["Номер дела"] = caseNumberMatch.Value;
            }

            // Извлекаем суд
            var courtMatch = Regex.Match(containerText, @"[А-Я][а-я]+\s+[А-Я][а-я]+\s+суд", RegexOptions.IgnoreCase);
            if (courtMatch.Success)
            {
                structuredData["Суд"] = courtMatch.Value;
            }

            // Формируем структурированный текст
            var structuredContent = string.Join("\n", 
                structuredData.Select(x => $"{x.Key}: {CleanText(x.Value)}"));
            
            // Добавляем полный текст
            structuredContent += $"\n\nПолный текст:\n{CleanText(containerText)}";
            
            return structuredContent;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при структурировании содержимого решения");
            return CleanText(containerText);
        }
    }

    /// <summary>
    /// Извлекает дату из содержимого решения
    /// </summary>
    private DateTime? ExtractDateFromDecisionContent(string content)
    {
        try
        {
            // Паттерны для дат в судебных документах
            var datePatterns = new[]
            {
                @"\d{1,2}\s+[а-я]+\s+\d{4}\s+года",
                @"\d{1,2}\.\d{1,2}\.\d{4}",
                @"\d{4}-\d{2}-\d{2}"
            };

            foreach (var pattern in datePatterns)
            {
                var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var dateStr = match.Value;
                    // Пробуем разные форматы парсинга
                    if (DateTime.TryParse(dateStr, out var date))
                        return date;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
   
   
   
   
   
   
   
   
   
   
   

    private async Task ExtractDetailedCaseInfo(IPage page, CourtCase courtCase)
    {
        try
        {
            logger.LogInformation("Извлекаем детальную информацию для дела: {CaseNumber}", courtCase.CaseNumber);

            // 1. Извлекаем информацию из заголовка (номер дела, дата начала, суд)
            await ExtractHeaderInfo(page, courtCase);

            // 2. Извлекаем информацию о сторонах (истец, ответчик, третьи лица)
            await ExtractPartiesInfo(page, courtCase);

            // 3. Извлекаем информацию о движении дела
            await ExtractCaseMovementInfo(page, courtCase);

            logger.LogInformation("✅ Детальная информация извлечена для дела {CaseNumber}", courtCase.CaseNumber);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при извлечении детальной информации для дела {CaseNumber}", courtCase.CaseNumber);
        }
    }

    private async Task ExtractHeaderInfo(IPage page, CourtCase courtCase)
    {
        try
        {
            // Ищем блок с основной информацией
            var headerBlock = await page.QuerySelectorAsync(".col-md-8.text-right");
            if (headerBlock == null)
            {
                logger.LogWarning("Блок заголовка не найден для дела {CaseNumber}", courtCase.CaseNumber);
                return;
            }

            var headerText = await headerBlock.EvaluateFunctionAsync<string>("el => el.textContent");
            logger.LogInformation("Текст заголовка: {HeaderText}", headerText);

            // Извлекаем номер дела
            var caseNumberMatch = Regex.Match(headerText, @"Номер дела:\s*<b>([^<]+)</b>", RegexOptions.IgnoreCase);
            if (caseNumberMatch.Success)
            {
                var detailedCaseNumber = caseNumberMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(detailedCaseNumber))
                {
                    courtCase.CaseNumber = detailedCaseNumber;
                    logger.LogInformation("Обновлен номер дела: {CaseNumber}", detailedCaseNumber);
                }
            }

            // Извлекаем дату начала дела
            var startDateMatch = Regex.Match(headerText, @"Дата начала:\s*<b>([^<]+)</b>", RegexOptions.IgnoreCase);
            if (startDateMatch.Success)
            {
                var startDateStr = startDateMatch.Groups[1].Value.Trim();
                if (DateTime.TryParse(startDateStr, out var startDate))
                {
                    courtCase.ReceivedDate = startDate;
                    logger.LogInformation("Найдена дата начала дела: {StartDate}", startDate);
                }
            }

            // Извлекаем информацию о суде
            var courtMatch = Regex.Match(headerText, @"Суд:\s*<b>([^<]+)</b>", RegexOptions.IgnoreCase);
            if (courtMatch.Success)
            {
                var courtName = courtMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(courtName))
                {
                    courtCase.CourtType = courtName;
                    logger.LogInformation("Обновлен суд: {CourtName}", courtName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при извлечении информации заголовка для дела {CaseNumber}", courtCase.CaseNumber);
        }
    }

    private async Task ExtractPartiesInfo(IPage page, CourtCase courtCase)
    {
        try
        {
            // Ищем таблицу с участниками процесса
            var partiesTables = await page.QuerySelectorAllAsync("table.table-condensed");
            
            foreach (var table in partiesTables)
            {
                var tableText = await table.EvaluateFunctionAsync<string>("el => el.textContent");
                if (tableText.Contains("Стороны по делу") || tableText.Contains("Вид лица"))
                {
                    await ExtractPartiesFromTable(table, courtCase);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при извлечении информации о сторонах для дела {CaseNumber}", courtCase.CaseNumber);
        }
    }

    private async Task ExtractPartiesFromTable(IElementHandle table, CourtCase courtCase)
    {
        try
        {
            var rows = await table.QuerySelectorAllAsync("tr");
            var plaintiff = new List<string>();
            var defendant = new List<string>();
            var thirdParties = new List<string>();

            foreach (var row in rows)
            {
                var cells = await row.QuerySelectorAllAsync("td");
                if (cells.Length >= 2)
                {
                    var partyType = await cells[0].EvaluateFunctionAsync<string>("el => el.textContent?.trim()");
                    var partyName = await cells[1].EvaluateFunctionAsync<string>("el => el.textContent?.trim()");

                    if (!string.IsNullOrEmpty(partyType) && !string.IsNullOrEmpty(partyName))
                    {
                        switch (partyType.ToUpper())
                        {
                            case "ИСТЕЦ":
                                plaintiff.Add(partyName);
                                break;
                            case "ОТВЕТЧИК":
                                defendant.Add(partyName);
                                break;
                            case "ТРЕТЬЕ ЛИЦО":
                                thirdParties.Add(partyName);
                                break;
                        }
                    }
                }
            }

            // Обновляем информацию о сторонах
            if (plaintiff.Any())
            {
                courtCase.Plaintiff = string.Join("; ", plaintiff);
                logger.LogInformation("Найден истец: {Plaintiff}", courtCase.Plaintiff);
            }

            if (defendant.Any())
            {
                courtCase.Defendant = string.Join("; ", defendant);
                logger.LogInformation("Найден ответчик: {Defendant}", courtCase.Defendant);
            }

            // Добавляем третьих лиц в описание если они есть
            if (thirdParties.Any())
            {
                courtCase.Subject += $" | Третьи лица: {string.Join("; ", thirdParties)}";
                logger.LogInformation("Найдены третьи лица: {ThirdParties}", string.Join("; ", thirdParties));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при извлечении сторон из таблицы для дела {CaseNumber}", courtCase.CaseNumber);
        }
    }

    private async Task ExtractCaseMovementInfo(IPage page, CourtCase courtCase)
    {
        try
        {
            // Ищем таблицу с движением дела
            var movementTables = await page.QuerySelectorAllAsync("table.table-condensed");
            
            foreach (var table in movementTables)
            {
                var tableText = await table.EvaluateFunctionAsync<string>("el => el.textContent");
                if (tableText.Contains("Движение дела") || tableText.Contains("Наименование события"))
                {
                    await ExtractMovementDatesFromTable(table, courtCase);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при извлечении информации о движении дела {CaseNumber}", courtCase.CaseNumber);
        }
    }

    private async Task ExtractMovementDatesFromTable(IElementHandle table, CourtCase courtCase)
    {
        try
        {
            var rows = await table.QuerySelectorAllAsync("tr");
            var events = new List<string>();

            foreach (var row in rows)
            {
                var cells = await row.QuerySelectorAllAsync("td");
                if (cells.Length >= 4) // Наименование, Результат, Основания, Дата
                {
                    var eventName = await cells[0].EvaluateFunctionAsync<string>("el => el.textContent?.trim()");
                    var eventDate = await cells[3].EvaluateFunctionAsync<string>("el => el.textContent?.trim()");

                    if (!string.IsNullOrEmpty(eventName) && !string.IsNullOrEmpty(eventDate))
                    {
                        events.Add($"{eventName}: {eventDate}");

                        // Ищем дату решения
                        if (eventName.Contains("Решение") && eventName.Contains("вынесено"))
                        {
                            if (DateTime.TryParse(eventDate, out var decisionDate))
                            {
                                courtCase.DecisionDate = decisionDate;
                                logger.LogInformation("Найдена дата решения из движения дела: {DecisionDate}", decisionDate);
                            }
                        }

                        // Ищем дату регистрации (поступления)
                        if (eventName.Contains("Регистрация") && eventName.Contains("иска"))
                        {
                            if (DateTime.TryParse(eventDate, out var receivedDate))
                            {
                                courtCase.ReceivedDate = receivedDate;
                                logger.LogInformation("Найдена дата регистрации из движения дела: {ReceivedDate}", receivedDate);
                            }
                        }
                    }
                }
            }

            // Обновляем описание с информацией о движении дела
            if (events.Any())
            {
                courtCase.Description = string.Join("; ", events.Take(3)); // Берем первые 3 события
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при извлечении дат движения дела {CaseNumber}", courtCase.CaseNumber);
        }
    }

    private async Task<bool> ExtractDecisionFromMainBlock(IPage page, CourtCase courtCase)
    {
        var decisionBlock = await page.QuerySelectorAsync(".btn-group1");
        if (decisionBlock == null)
        {
            logger.LogInformation("Блок с решениями (.btn-group1) не найден для дела {CaseNumber}", courtCase.CaseNumber);
            return false;
        }

        logger.LogInformation("Найден блок с решениями для дела {CaseNumber}", courtCase.CaseNumber);
        
        var decisionLinks = await decisionBlock.QuerySelectorAllAsync("a");
        logger.LogInformation("Найдено ссылок в блоке решений: {Count}", decisionLinks.Length);
        
        if (decisionLinks.Length == 0)
        {
            logger.LogInformation("Блок .btn-group1 найден, но внутри нет ссылок для дела {CaseNumber}", courtCase.CaseNumber);
            return false;
        }

        foreach (var link in decisionLinks)
        {
            try
            {
                var href = await link.EvaluateFunctionAsync<string>("el => el.getAttribute('href')");
                var text = await link.EvaluateFunctionAsync<string>("el => el.textContent?.trim()");
                var title = await link.EvaluateFunctionAsync<string>("el => el.getAttribute('title')");
            
                logger.LogInformation("Найдена ссылка: текст='{Text}', href='{Href}', title='{Title}'", 
                    text, href, title);
            
                if (!string.IsNullOrEmpty(href) && IsValidDecisionLink(href))
                {
                    var fullLink = href.StartsWith("/") 
                        ? "https://www.xn--90afdbaav0bd1afy6eub5d.xn--p1ai" + href 
                        : href;
                
                    courtCase.HasDecision = true;
                    courtCase.DecisionLink = fullLink;
                    courtCase.DecisionType = DetermineDocumentType(text);
                
                    logger.LogInformation("✅ Для дела {CaseNumber} найдено {DocumentType}: {DecisionLink}", 
                        courtCase.CaseNumber, courtCase.DecisionType, courtCase.DecisionLink);
                
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка при обработке ссылки в блоке решений для дела {CaseNumber}", 
                    courtCase.CaseNumber);
            }
        }

        return false;
    }

    private bool IsValidDecisionLink(string href)
    {
        if (string.IsNullOrEmpty(href)) 
            return false;
    
        // Проверяем, что ссылка ведет на документ решения
        return href.Contains("/decisions/") || 
               href.EndsWith(".doc") || 
               href.EndsWith(".docx") || 
               href.EndsWith(".pdf");
    }

    private string DetermineDocumentType(string linkText)
    {
        if (string.IsNullOrEmpty(linkText)) 
            return "Документ";
    
        var text = linkText.ToLower();
    
        if (text.Contains("решение") && text.Contains("мотивированное")) return "Мотивированное решение";
        else if (text.Contains("решение")) return "Решение";
        else if (text.Contains("определение")) return "Определение";
        else if (text.Contains("постановление")) return "Постановление";
        else if (text.Contains("приказ")) return "Судебный приказ";
        else if (text.Contains("жалоб")) return "Жалоба";
        else if (text.Contains("заявление")) return "Заявление";
        else if (text.Contains("скачать")) return "Документ для скачивания";
        else return "Документ";
    }

    private async Task<bool> FindDecisionLinksAlternativeAsync(IPage page, CourtCase courtCase)
    {
        try
        {
            // Альтернативные селекторы для поиска решений
            var alternativeSelectors = new[]
            {
                "a[href*='/decisions/']",
                "a[href$='.doc']",
                "a[href$='.docx']", 
                "a[href$='.pdf']",
                "a.btn-success"
            };
        
            foreach (var selector in alternativeSelectors)
            {
                var links = await page.QuerySelectorAllAsync(selector);
                logger.LogInformation("Альтернативный поиск: селектор {Selector} нашел {Count} ссылок", selector, links.Length);
            
                foreach (var link in links)
                {
                    try
                    {
                        var href = await link.EvaluateFunctionAsync<string>("el => el.getAttribute('href')");
                        var text = await link.EvaluateFunctionAsync<string>("el => el.textContent?.trim()");
                    
                        if (!string.IsNullOrEmpty(href) && IsValidDecisionLink(href))
                        {
                            var fullLink = href.StartsWith("/") 
                                ? "https://www.xn--90afdbaav0bd1afy6eub5d.xn--p1ai" + href 
                                : href;
                        
                            var documentType = DetermineDocumentType(text);
                        
                            courtCase.HasDecision = true;
                            courtCase.DecisionLink = fullLink;
                            courtCase.DecisionType = documentType;
                        
                            logger.LogInformation("✅ Альтернативный поиск: для дела {CaseNumber} найдено {DocumentType}", 
                                courtCase.CaseNumber, documentType);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Ошибка при обработке альтернативной ссылки");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при альтернативном поиске решений для дела {CaseNumber}", courtCase.CaseNumber);
        }

        return false;
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Убираем все лишние пробелы, переносы, табы
        var cleaned = string.Join(" ", text.Split([' ', '\n', '\r', '\t'], 
            StringSplitOptions.RemoveEmptyEntries)).Trim();

        // Убираем лишние пробелы вокруг дефисов и других символов
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"\s*-\s*", "-");
        
        return cleaned;
    }
}