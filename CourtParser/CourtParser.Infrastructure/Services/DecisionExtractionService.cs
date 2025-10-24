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
        
            // Сбрасываем флаг решения перед проверкой
            courtCase.HasDecision = false;
            courtCase.DecisionLink = string.Empty;
            courtCase.DecisionType = "Не найдено";
            courtCase.DecisionContent = string.Empty;
            courtCase.DecisionDate = null;
        
            // Ждем загрузки страницы
            await page.GoToAsync(courtCase.Link, WaitUntilNavigation.Networkidle2);
            await Task.Delay(2000, cancellationToken);

            // 1. Сначала извлекаем детальную информацию о деле
            await ExtractDetailedCaseInfo(page, courtCase);
        
            // 2. Извлекаем оригинальную ссылку на сайт суда
            await ExtractOriginalCaseLinkAsync(page, courtCase);
        
            // 3. ПРИОРИТЕТ: Проверяем наличие ссылок на файлы решений
            bool fileDecisionFound = await CheckFileDecisionLinks(page, courtCase);
            
            if (fileDecisionFound)
            {
                logger.LogInformation("✅ Найдено файловое решение для дела {CaseNumber}", courtCase.CaseNumber);
                return;
            }

            // 4. Если файлового решения нет, проверяем встроенное HTML-решение
            bool embeddedDecisionFound = await ExtractEmbeddedDecisionAsync(page, courtCase);
            
            if (embeddedDecisionFound)
            {
                logger.LogInformation("✅ Найдено встроенное решение для дела {CaseNumber}", courtCase.CaseNumber);
                return;
            }

            // 5. Если ничего не найдено
            logger.LogInformation("❌ Для дела {CaseNumber} решение не найдено", courtCase.CaseNumber);
            courtCase.HasDecision = false;
            courtCase.DecisionType = "Не найдено";
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
    /// Извлекает встроенное решение прямо из HTML страницы - УЛУЧШЕННАЯ ВЕРСИЯ
    /// </summary>
    private async Task<bool> ExtractEmbeddedDecisionAsync(IPage page, CourtCase courtCase)
    {
        try
        {
            logger.LogInformation("Ищем встроенное решение в HTML для дела {CaseNumber}", courtCase.CaseNumber);

            var pageContent = await page.GetContentAsync();
            
            // СПЕЦИАЛЬНАЯ ПРОВЕРКА: Ищем блоки MsoNormal с выравниванием по ширине
            bool hasMsoNormalStructure = await CheckMsoNormalStructure(page, courtCase);
            if (hasMsoNormalStructure)
            {
                logger.LogInformation("✅ Найдена структура MsoNormal для дела {CaseNumber}", courtCase.CaseNumber);
                return true;
            }

            // Стандартная проверка структуры решения
            bool hasStandardStructure = await CheckStandardDecisionStructure(page, courtCase);
            if (hasStandardStructure)
            {
                return true;
            }

            // Проверка по содержимому страницы
            bool hasDecisionContent = await CheckDecisionByContent(page, pageContent, courtCase);
            if (hasDecisionContent)
            {
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
    /// СПЕЦИАЛЬНАЯ ПРОВЕРКА: Ищем блоки MsoNormal с выравниванием по ширине
    /// </summary>
    private async Task<bool> CheckMsoNormalStructure(IPage page, CourtCase courtCase)
    {
        try
        {
            // Ищем все параграфы с классом MsoNormal и выравниванием по ширине
            var msoNormalElements = await page.QuerySelectorAllAsync(
                "p.MsoNormal[style*='TEXT-ALIGN: justify'], " +
                "p.MsoNormal[style*='text-align: justify'], " +
                "p[class*='MsoNormal'][style*='justify']"
            );

            logger.LogInformation("Найдено элементов MsoNormal с выравниванием: {Count}", msoNormalElements.Length);

            if (msoNormalElements.Length < 5) // Должно быть достаточно много таких параграфов
            {
                logger.LogInformation("Недостаточно элементов MsoNormal для признания решения: {Count}", msoNormalElements.Length);
                return false;
            }

            // Извлекаем текст из всех найденных элементов
            var decisionTextParts = new List<string>();
            foreach (var element in msoNormalElements.Take(20)) // Берем первые 20 элементов
            {
                var text = await element.EvaluateFunctionAsync<string>("el => el.textContent?.trim()");
                if (!string.IsNullOrEmpty(text) && text.Length > 10) // Отсекаем короткие фрагменты
                {
                    decisionTextParts.Add(text);
                }
            }

            if (decisionTextParts.Count < 3)
            {
                logger.LogInformation("Недостаточно текстового контента в MsoNormal элементах");
                return false;
            }

            var fullText = string.Join(" ", decisionTextParts);
            
            // ВАЛИДАЦИЯ: проверяем, что это действительно судебное решение
            if (!IsValidDecisionContent(fullText))
            {
                logger.LogInformation("Текст из MsoNormal не прошел валидацию как судебное решение");
                return false;
            }

            // Определяем тип документа
            var documentType = DetermineDocumentTypeFromContent(fullText);
            if (string.IsNullOrEmpty(documentType))
            {
                logger.LogInformation("Не удалось определить тип документа из MsoNormal контента");
                return false;
            }

            // УСПЕХ: решение найдено
            courtCase.HasDecision = true;
            courtCase.DecisionLink = courtCase.Link + "#embedded_decision";
            courtCase.DecisionType = documentType;
            courtCase.DecisionContent = fullText;
            courtCase.DecisionDate = ExtractDateFromDecisionContent(fullText);

            logger.LogInformation("✅ Найдено валидное решение в MsoNormal структуре: {Type}", documentType);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при проверке MsoNormal структуры");
            return false;
        }
    }

    
    /// <summary>
    /// Проверяет стандартную структуру решения (h3 + blockquote)
    /// </summary>
    private async Task<bool> CheckStandardDecisionStructure(IPage page, CourtCase courtCase)
    {
        try
        {
            var pageContent = await page.GetContentAsync();
            
            bool hasSolutionStructure = 
                pageContent.Contains("<h3 class=\"text-center\">") && 
                pageContent.Contains("<blockquote itemprop=\"text\">");

            if (!hasSolutionStructure)
            {
                return false;
            }

            // Извлекаем заголовок
            var headerMatch = Regex.Match(
                pageContent, 
                @"<h3 class=""text-center"">([^<]*)</h3>"
            );
            
            if (!headerMatch.Success)
            {
                return false;
            }

            // Извлекаем текст решения из blockquote
            var blockquoteMatch = Regex.Match(
                pageContent,
                @"<blockquote itemprop=""text"">(.*?)</blockquote>",
                RegexOptions.Singleline
            );
            
            if (!blockquoteMatch.Success)
            {
                return false;
            }

            var decisionText = CleanText(blockquoteMatch.Groups[1].Value);
            
            if (!IsValidDecisionContent(decisionText))
            {
                return false;
            }

            var documentType = DetermineDocumentTypeFromContent(decisionText);
            if (string.IsNullOrEmpty(documentType))
            {
                return false;
            }

            courtCase.HasDecision = true;
            courtCase.DecisionLink = courtCase.Link + "#embedded_decision";
            courtCase.DecisionType = documentType;
            courtCase.DecisionContent = decisionText;
            courtCase.DecisionDate = ExtractDateFromDecisionContent(decisionText);
            
            logger.LogInformation("✅ Найдено решение в стандартной структуре: {Type}", documentType);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при проверке стандартной структуры");
            return false;
        }
    }
     
     
    /// <summary>
    /// ВАЛИДАЦИЯ содержимого решения - УЛУЧШЕННАЯ ВЕРСИЯ
    /// </summary>
    private bool IsValidDecisionContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var cleanContent = content.ToLower();

        // ОБЯЗАТЕЛЬНЫЕ элементы судебного решения
        var requiredElements = new[]
        {
            "именем российской федерации",
            "решил:",
            "решила:",
            "определил:",
            "определила:",
            "постановил:",
            "постановила:",
            "установил:",
            "установила:"
        };

        // ДОПОЛНИТЕЛЬНЫЕ признаки (нужно минимум 3)
        var additionalElements = new[]
        {
            "суд",
            "судья",
            "рассмотрев",
            "заявление",
            "иск",
            "дело №",
            "председательствующий",
            "решение",
            "определение",
            "постановление",
            "удовлетворить",
            "отказать",
            "истец",
            "ответчик"
        };

        // Должен содержать хотя бы один ОБЯЗАТЕЛЬНЫЙ элемент
        bool hasRequired = requiredElements.Any(element => cleanContent.Contains(element));
        
        // И хотя бы три ДОПОЛНИТЕЛЬНЫХ элемента
        int additionalCount = additionalElements.Count(element => cleanContent.Contains(element));

        bool isValid = hasRequired && additionalCount >= 3;

        logger.LogDebug("Валидация контента: Required={HasRequired}, Additional={AdditionalCount}, Valid={IsValid}", 
            hasRequired, additionalCount, isValid);

        return isValid;
    }
     

    /// <summary>
    /// Проверяет решение по содержимому всей страницы
    /// </summary>
    private async Task<bool> CheckDecisionByContent(IPage page, string pageContent, CourtCase courtCase)
    {
        try
        {
            // Ищем явные признаки решения в тексте
            var cleanContent = pageContent.ToLower();
            
            bool hasStrongIndicators = 
                cleanContent.Contains("р е ш е н и е") ||
                cleanContent.Contains("о п р е д е л е н и е") ||
                cleanContent.Contains("именем российской федерации");

            if (!hasStrongIndicators)
            {
                return false;
            }

            // Извлекаем основной текст страницы
            var bodyText = await page.EvaluateFunctionAsync<string>(@"
                () => {
                    // Убираем скрипты, стили, навигацию
                    const scripts = document.querySelectorAll('script, style, nav, header, footer');
                    scripts.forEach(el => el.remove());
                    
                    return document.body.innerText;
                }
            ");

            if (!IsValidDecisionContent(bodyText))
            {
                return false;
            }

            var documentType = DetermineDocumentTypeFromContent(bodyText);
            if (string.IsNullOrEmpty(documentType))
            {
                return false;
            }

            courtCase.HasDecision = true;
            courtCase.DecisionLink = courtCase.Link + "#embedded_decision";
            courtCase.DecisionType = documentType;
            courtCase.DecisionContent = CleanText(bodyText);
            courtCase.DecisionDate = ExtractDateFromDecisionContent(bodyText);
            
            logger.LogInformation("✅ Найдено решение по содержимому страницы: {Type}", documentType);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при проверке по содержимому страницы");
            return false;
        }
    }

    /// <summary>
    /// Проверяет ссылки на файлы решений
    /// </summary>
    private async Task<bool> CheckFileDecisionLinks(IPage page, CourtCase courtCase)
    {
        try
        {
            logger.LogInformation("Ищем ссылки на файлы решений для дела {CaseNumber}", courtCase.CaseNumber);

            // Ищем блок с кнопками для скачивания
            var btnGroup = await page.QuerySelectorAsync(".btn-group1");
            if (btnGroup == null)
            {
                logger.LogInformation("Блок .btn-group1 не найден для дела {CaseNumber}", courtCase.CaseNumber);
                return false;
            }

            var decisionLinks = await btnGroup.QuerySelectorAllAsync("a");
            logger.LogInformation("Найдено ссылок в блоке решений: {Count}", decisionLinks.Length);
        
            foreach (var link in decisionLinks)
            {
                var href = await link.EvaluateFunctionAsync<string>("el => el.getAttribute('href')");
                var text = await link.EvaluateFunctionAsync<string>("el => el.textContent");
            
                if (!string.IsNullOrEmpty(href) && IsValidDecisionFileLink(href, text))
                {
                    var fullLink = href.StartsWith("/") 
                        ? "https://www.xn--90afdbaav0bd1afy6eub5d.xn--p1ai" + href 
                        : href;
                
                    courtCase.HasDecision = true;
                    courtCase.DecisionLink = fullLink;
                    courtCase.DecisionType = DetermineDocumentTypeFromLink(text);
                
                    logger.LogInformation("✅ Найдена ссылка на файл решения для дела {CaseNumber}: {Type}", 
                        courtCase.CaseNumber, courtCase.DecisionType);
                
                    return true;
                }
            }
        
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при проверке ссылок на файлы решений для дела {CaseNumber}", courtCase.CaseNumber);
            return false;
        }
    }
  
    /// <summary>
    /// Строгая проверка валидности ссылки на файл решения
    /// </summary>
    private bool IsValidDecisionFileLink(string href, string linkText)
    {
        if (string.IsNullOrEmpty(href)) 
            return false;
    
        // Проверяем расширения файлов
        bool hasValidExtension = href.EndsWith(".doc") || 
                                 href.EndsWith(".docx") || 
                                 href.EndsWith(".pdf") ||
                                 href.EndsWith(".rtf");

        // Проверяем путь
        bool hasValidPath = href.Contains("/decisions/");

        // Проверяем текст ссылки
        var cleanText = (linkText ?? "").ToLower();
        bool hasValidText = cleanText.Contains("решение") ||
                            cleanText.Contains("определение") ||
                            cleanText.Contains("постановление") ||
                            cleanText.Contains("приказ") ||
                            cleanText.Contains("мотивированное");

        // Должны совпасть ВСЕ критерии
        return hasValidExtension && hasValidPath && hasValidText;
    }
    
    /// <summary>
    /// Определяет тип документа из текста ссылки
    /// </summary>
    private string DetermineDocumentTypeFromLink(string linkText)
    {
        if (string.IsNullOrEmpty(linkText)) 
            return "Документ";
    
        var text = linkText.ToLower();
    
        if (text.Contains("мотивированное решение")) return "Мотивированное решение";
        if (text.Contains("решение")) return "Решение";
        if (text.Contains("определение")) return "Определение";
        if (text.Contains("постановление")) return "Постановление";
        if (text.Contains("приказ")) return "Судебный приказ";
        return "Документ";
    }
    
    /// <summary>
    /// Определяет тип документа из содержимого - УЛУЧШЕННАЯ ВЕРСИЯ
    /// </summary>
    private string DetermineDocumentTypeFromContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null!;

        var cleanContent = content.ToLower();

        // ТОЧНЫЕ СОВПАДЕНИЯ с форматированием
        if (cleanContent.Contains("р е ш е н и е") || 
            (cleanContent.Contains("решение") && cleanContent.Contains("именем российской федерации")))
            return "Решение";
        else if (cleanContent.Contains("о п р е д е л е н и е") || 
                 (cleanContent.Contains("определение") && cleanContent.Contains("именем российской федерации")))
            return "Определение";
        else if (cleanContent.Contains("п о с т а н о в л е н и е") || 
                 (cleanContent.Contains("постановление") && cleanContent.Contains("именем российской федерации")))
            return "Постановление";
        else if (cleanContent.Contains("приказ") && cleanContent.Contains("именем российской федерации"))
            return "Судебный приказ";
        else if (cleanContent.Contains("мотивированное решение"))
            return "Мотивированное решение";
        
        // Проверяем по ключевым словам в тексте
        else if (cleanContent.Contains("решил:") || cleanContent.Contains("решила:"))
            return "Решение";
        else if (cleanContent.Contains("определил:") || cleanContent.Contains("определила:"))
            return "Определение";
        else if (cleanContent.Contains("постановил:") || cleanContent.Contains("постановила:"))
            return "Постановление";
        else if (cleanContent.Contains("именем российской федерации"))
            return "Судебный акт";
        else
            return null!; // Неизвестный тип - считаем что решения нет
    }
   
  
    /// <summary>
    /// Извлекает дату из содержимого решения
    /// </summary>
    private DateTime? ExtractDateFromDecisionContent(string content)
    {
        try
        {
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
    
    /// <summary>
    /// Извлекает ссылку на оригинальный сайт суда
    /// </summary>
    private async Task ExtractOriginalCaseLinkAsync(IPage page, CourtCase courtCase)
    {
        try
        {
            logger.LogInformation("Ищем ссылку на оригинальный сайт суда для дела {CaseNumber}", courtCase.CaseNumber);

            // 1. Ищем кнопку для показа ссылки
            var showLinkButton = await page.QuerySelectorAsync("#show-original-link");
            if (showLinkButton == null)
            {
                logger.LogInformation("Кнопка показа оригинальной ссылки не найдена для дела {CaseNumber}", courtCase.CaseNumber);
                return;
            }

            // 2. Кликаем на кнопку чтобы показать ссылку
            await showLinkButton.ClickAsync();
            await Task.Delay(2000); // Ждем появления ссылки

            // 3. Ищем появившуюся ссылку
            var originalLinkContainer = await page.QuerySelectorAsync("#original-link");
            if (originalLinkContainer != null)
            {
                var linkElement = await originalLinkContainer.QuerySelectorAsync("a[target='_blank']");
                if (linkElement != null)
                {
                    var originalLink = await linkElement.EvaluateFunctionAsync<string>("el => el.getAttribute('href')");
                    if (!string.IsNullOrEmpty(originalLink))
                    {
                        courtCase.OriginalCaseLink = originalLink;
                        logger.LogInformation("✅ Найдена оригинальная ссылка для дела {CaseNumber}: {Link}", 
                            courtCase.CaseNumber, originalLink);
                        return;
                    }
                }
            }

            logger.LogInformation("Оригинальная ссылка не найдена после нажатия кнопки для дела {CaseNumber}", courtCase.CaseNumber);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при извлечении оригинальной ссылки для дела {CaseNumber}", courtCase.CaseNumber);
        }
    }

    private async Task ExtractPartiesInfo(IPage page, CourtCase courtCase)
    {
        try
        {
            // Ищем ВСЕ таблицы с классом table-condensed
            var tables = await page.QuerySelectorAllAsync("table.table-condensed");
        
            foreach (var table in tables)
            {
                // Проверяем есть ли в таблице нужные заголовки
                var tableHtml = await table.EvaluateFunctionAsync<string>("el => el.outerHTML");
                if (tableHtml.Contains("ИСТЕЦ") || tableHtml.Contains("ОТВЕТЧИК") || tableHtml.Contains("ТРЕТЬЕ") || tableHtml.Contains("ПРЕДСТАВИТЕЛЬ"))
                {
                    await ExtractPartiesFromTable(table, courtCase);
                    return; // Нашли - выходим
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при поиске таблицы сторон");
        }
    }

    private async Task ExtractPartiesFromTable(IElementHandle table, CourtCase courtCase)
    {
        try
        {
            var rows = await table.QuerySelectorAllAsync("tr");
            var plaintiffs = new List<string>();
            var defendants = new List<string>();
            var thirdParties = new List<string>();
            var representatives = new List<string>();

            foreach (var row in rows)
            {
                var cells = await row.QuerySelectorAllAsync("td");
                if (cells.Length >= 2)
                {
                    var partyType = await cells[0].EvaluateFunctionAsync<string>("el => el.textContent");
                    var partyName = await cells[1].EvaluateFunctionAsync<string>("el => el.textContent");

                    if (!string.IsNullOrEmpty(partyType) && !string.IsNullOrEmpty(partyName))
                    {
                        var cleanType = partyType.Trim().ToUpper();
                        var cleanName = partyName.Trim();

                        // Простая проверка по тексту
                        if (cleanType.Contains("ИСТЕЦ"))
                            plaintiffs.Add(cleanName);
                        else if (cleanType.Contains("ОТВЕТЧИК"))
                            defendants.Add(cleanName);
                        else if (cleanType.Contains("ТРЕТЬЕ"))
                            thirdParties.Add(cleanName);
                        else if (cleanType.Contains("ПРЕДСТАВИТЕЛЬ"))
                            representatives.Add(cleanName);
                    }
                }
            }

            // Собираем всех через точку с запятой
            courtCase.Plaintiff = plaintiffs.Any() ? string.Join("; ", plaintiffs) : "";
            courtCase.Defendant = defendants.Any() ? string.Join("; ", defendants) : "";
            courtCase.ThirdParties = thirdParties.Any() ? string.Join("; ", thirdParties) : "";
        
            // Представителей добавляем к третьим лицам (или можно в отдельное поле если нужно)
            if (representatives.Any())
            {
                var allThirdParties = courtCase.ThirdParties;
                if (!string.IsNullOrEmpty(allThirdParties))
                    allThirdParties += "; ";
                allThirdParties += string.Join("; ", representatives);
                courtCase.ThirdParties = allThirdParties;
            }

            logger.LogInformation("Стороны извлечены: Истцов={PlaintiffsCount}, Ответчиков={DefendantsCount}, Третьих лиц={ThirdPartiesCount}", 
                plaintiffs.Count, defendants.Count, thirdParties.Count + representatives.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при извлечении сторон из таблицы");
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
    
        if (text.Contains("мотивированное решение")) return "Мотивированное решение";
        if (text.Contains("решение")) return "Решение";
        if (text.Contains("определение")) return "Определение";
        if (text.Contains("постановление")) return "Постановление";
        if (text.Contains("приказ")) return "Судебный приказ";
        return "Документ";
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