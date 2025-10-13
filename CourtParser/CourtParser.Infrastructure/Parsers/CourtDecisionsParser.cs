using CourtParser.Core.Interfaces;
using CourtParser.Models.Entities;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Text.RegularExpressions;

namespace CourtParser.Infrastructure.Parsers;

public class CourtDecisionsParser(ILogger<CourtDecisionsParser> logger) : IParser
{
    private const int MaxPages = 2; 

    public async Task<List<CourtCase>> ParseCasesAsync(int page)
    {
        return await FillFormAndSearchCasesAsync(CancellationToken.None);
    }
    
    private async Task<List<CourtCase>> FillFormAndSearchCasesAsync(CancellationToken cancellationToken = default)
    {
        await new BrowserFetcher().DownloadAsync();

        var launchOptions = new LaunchOptions
        {
            Headless = false,
            Args = ["--no-sandbox", "--disable-setuid-sandbox"]
        };

        await using var browser = await Puppeteer.LaunchAsync(launchOptions);
        await using var page = await browser.NewPageAsync();

        await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        var allCases = new List<CourtCase>();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Настраиваем обработку навигации
            page.FrameNavigated += (_, e) =>
            {
                logger.LogInformation("Произошла навигация на: {Url}", e.Frame.Url);
            };

            await page.GoToAsync("https://www.xn--90afdbaav0bd1afy6eub5d.xn--p1ai/extended-search", 
                WaitUntilNavigation.Networkidle2);

            await page.WaitForSelectorAsync("#extendedSearch_case_type", new WaitForSelectorOptions 
            { 
                Timeout = 30000 
            });

            logger.LogInformation("Форма загружена, начинаем заполнение...");

            // 1. Выбираем тип дела
            await page.SelectAsync("#extendedSearch_case_type", "gr_first");
            logger.LogInformation("Выбран тип дела: Первая инстанция (гражданские и административные дела)");
            
            // Ждем появления поля категории
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#extendedSearch_sub_category_1') !== null",
                new WaitForFunctionOptions { Timeout = 10000 }
            );
            await Task.Delay(1500, cancellationToken);

            // 2. Выбираем категорию
            var categorySelect = await page.QuerySelectorAsync("#extendedSearch_sub_category_1");
            if (categorySelect == null)
            {
                logger.LogError("Поле категории не найдено после выбора типа дела");
                await page.ScreenshotAsync("category_not_found.png");
                return [];
            }
            
            await page.SelectAsync("#extendedSearch_sub_category_1", "46");
            logger.LogInformation("Выбрана категория: Имущественные споры");
            
            // Ждем появления подкатегории
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#extendedSearch_sub_category_2') !== null",
                new WaitForFunctionOptions { Timeout = 10000 }
            );
            await Task.Delay(1500, cancellationToken);

            // 3. Выбираем подкатегорию
            var subcategorySelect = await page.QuerySelectorAsync("#extendedSearch_sub_category_2");
            if (subcategorySelect == null)
            {
                logger.LogError("Поле подкатегории не найдено после выбора категории");
                await page.ScreenshotAsync("subcategory_not_found.png");
                return [];
            }
            
            await page.SelectAsync("#extendedSearch_sub_category_2", "53");
            logger.LogInformation("Выбрана подкатегория: Иски о взыскании сумм по договору займа, кредитному договору");
            await Task.Delay(1000, cancellationToken);

            // 4. Нажимаем кнопку поиска и ждем навигации
            logger.LogInformation("Нажимаем кнопку поиска...");
            
            // Используем WaitForNavigation для ожидания загрузки новой страницы
            var navigationTask = page.WaitForNavigationAsync(new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle2],
                Timeout = 30000
            });
            
            await page.ClickAsync("#extendedSearch_search");
            
            // Ждем завершения навигации
            await navigationTask;
            logger.LogInformation("Навигация завершена, страница результатов загружена");

            // Даем странице время на полную загрузку
            await Task.Delay(3000, cancellationToken);

            // Проверяем, что мы на странице результатов
            var currentUrl = page.Url;
            logger.LogInformation("Текущий URL: {Url}", currentUrl);

            // Делаем скриншот для отладки
            await page.ScreenshotAsync("after_search.png");
            logger.LogInformation("Скриншот после поиска сохранен: after_search.png");

            // 5. Парсим первую страницу с повторными попытками
            var firstPageCases = await ParseSearchResultsWithRetry(page);
            allCases.AddRange(firstPageCases);
            logger.LogInformation("Спарсено дел на странице 1: {Count}", firstPageCases.Count);

            // 6. Листаем следующие страницы
            if (firstPageCases.Count > 0)
            {
                await ParseAdditionalPagesAsync(page, allCases, cancellationToken);
            }

            logger.LogInformation("Всего спарсено дел со всех страниц: {Count}", allCases.Count);
        
            return allCases;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Парсинг отменен");
            return allCases;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при заполнении формы и поиске");
            try
            {
                await page.ScreenshotAsync("error.png");
                logger.LogInformation("Скриншот ошибки сохранен: error.png");
            }
            catch
            {
                // Игнорируем ошибки при создании скриншота
            }
            return allCases;
        }
    }
    private async Task ParseAdditionalPagesAsync(IPage page, List<CourtCase> allCases, CancellationToken cancellationToken)
    {
        try
        {
            for (int currentPage = 2; currentPage <= MaxPages; currentPage++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogInformation("Проверяем наличие страницы {Page}...", currentPage);

                // Ищем пагинацию
                var pagination = await page.QuerySelectorAsync(".pagination");
                if (pagination == null)
                {
                    logger.LogWarning("Пагинация не найдена на странице {Page}. Завершение.", currentPage);
                    break;
                }

                // Ищем ссылку на конкретную страницу
                var nextPageLink = await pagination.QuerySelectorAsync($"a[href*='page={currentPage}']");
                if (nextPageLink == null)
                {
                    logger.LogInformation("Ссылка на страницу {Page} не найдена. Завершение пагинации.", currentPage);
                    break;
                }

                logger.LogInformation("Переходим на страницу {Page}...", currentPage);

                // Используем WaitForNavigation для перехода на следующую страницу
                var navigationTask = page.WaitForNavigationAsync(new NavigationOptions
                {
                    WaitUntil = new[] { WaitUntilNavigation.Networkidle2 },
                    Timeout = 30000
                });

                await nextPageLink.ClickAsync();
            
                // Ждем загрузки новой страницы
                await navigationTask;

                // Ждем немного для полной загрузки
                await Task.Delay(3000, cancellationToken);

                // Парсим текущую страницу
                var pageCases = await ParseSearchResultsWithRetry(page);
            
                if (pageCases.Count == 0)
                {
                    logger.LogWarning("На странице {Page} не найдено дел. Завершение.", currentPage);
                    break;
                }

                // Просто добавляем все дела без проверки дубликатов
                allCases.AddRange(pageCases);
                logger.LogInformation("Спарсено дел на странице {Page}: {Count}", currentPage, pageCases.Count);

                // Задержка между страницами чтобы не нагружать сервер
                await Task.Delay(2000, cancellationToken);
            }

            logger.LogInformation("Пагинация завершена. Обработано страниц: {Pages}", MaxPages);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Пагинация прервана");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при парсинге дополнительных страниц");
        }
    }
    
    private async Task<List<CourtCase>> ParseSearchResultsWithRetry(IPage page, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                logger.LogInformation("Попытка парсинга результатов #{Attempt}", attempt);
                
                // Ждем появления результатов с разными селекторами
                try
                {
                    await page.WaitForSelectorAsync("table.table-bordered", new WaitForSelectorOptions 
                    { 
                        Timeout = 10000 
                    });
                }
                catch
                {
                    // Пробуем альтернативный селектор
                    await page.WaitForSelectorAsync("table", new WaitForSelectorOptions 
                    { 
                        Timeout = 5000 
                    });
                }

                var cases = await ParseSearchResultsAsync(page);
                
                if (cases.Count > 0)
                {
                    logger.LogInformation("Успешно спарсено {Count} дел с попытки #{Attempt}", cases.Count, attempt);
                    return cases;
                }

                logger.LogWarning("На попытке #{Attempt} не найдено дел, ждем и пробуем снова", attempt);
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка при парсинге на попытке #{Attempt}", attempt);
                if (attempt < maxRetries)
                {
                    await Task.Delay(2000);
                }
            }
        }

        logger.LogError("Не удалось спарсить результаты после {MaxRetries} попыток", maxRetries);
        return [];
    }
    
    private async Task<List<CourtCase>> ParseSearchResultsAsync(IPage page)
    {
        var cases = new List<CourtCase>();

        try
        {
            // Получаем общее количество найденных документов
            var countElement = await page.QuerySelectorAsync(".count");
            if (countElement != null)
            {
                var countText = await countElement.EvaluateFunctionAsync<string>("el => el.textContent");
                logger.LogInformation("Информация о результатах: {CountText}", countText);
            }

            // Ищем все таблицы с делами
            var caseTables = await page.QuerySelectorAllAsync("table.table-bordered");
            logger.LogInformation("Найдено таблиц с делами: {Count}", caseTables.Length);

            // Альтернативный поиск - через общий контейнер
            if (caseTables.Length == 0)
            {
                logger.LogInformation("Пытаемся найти дела альтернативным способом...");
                caseTables = await page.QuerySelectorAllAsync("table");
                logger.LogInformation("Альтернативный поиск нашел таблиц: {Count}", caseTables.Length);
            }

            foreach (var table in caseTables)
            {
                try
                {
                    var caseData = await ParseCaseTableAsync(table);
                    if (caseData != null)
                    {
                        cases.Add(caseData);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ошибка при парсинге таблицы дела");
                }
            }

            // Если все еще не нашли дела, пробуем другой подход
            if (cases.Count == 0)
            {
                cases = await ParseAlternativeResultsAsync(page);
            }

            logger.LogInformation("Успешно спарсено дел: {Count}", cases.Count);
            return cases;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при парсинге результатов поиска");
            
            // Пробуем альтернативный парсинг при ошибке
            try
            {
                var alternativeCases = await ParseAlternativeResultsAsync(page);
                logger.LogInformation("Альтернативный парсинг нашел дел: {Count}", alternativeCases.Count);
                return alternativeCases;
            }
            catch (Exception altEx)
            {
                logger.LogError(altEx, "Ошибка при альтернативном парсинге");
                return cases;
            }
        }
    }

    private async Task<CourtCase?> ParseCaseTableAsync(IElementHandle table)
    {
        try
        {
            // Первая строка - заголовок с судом и номером дела
            var headerRow = await table.QuerySelectorAsync("tr.active");
            if (headerRow == null) return null;

            var headerCells = await headerRow.QuerySelectorAllAsync("td");
            if (headerCells.Length < 2) return null;

            // Суд (первая ячейка)
            var courtElement = headerCells[0];
            var courtName = await courtElement.EvaluateFunctionAsync<string>("el => el.textContent");
        
            // Номер дела и ссылка (вторая ячейка)
            var caseElement = headerCells[1];
            var linkElement = await caseElement.QuerySelectorAsync("a");
            if (linkElement == null) return null;

            var caseNumber = await linkElement.EvaluateFunctionAsync<string>("el => el.textContent");
            var href = await linkElement.EvaluateFunctionAsync<string>("el => el.getAttribute('href')");
            var link = !string.IsNullOrEmpty(href) ? 
                "https://www.xn--90afdbaav0bd1afy6eub5d.xn--p1ai" + href : 
                string.Empty;

            // Вторая строка - детали дела
            var detailsRow = await table.QuerySelectorAsync("tr:not(.active)");
            if (detailsRow == null) return null;

            var detailCells = await detailsRow.QuerySelectorAllAsync("td");
            if (detailCells.Length < 2) return null;

            // Даты (первая ячейка второй строки)
            var datesElement = detailCells[0];
            var datesText = await datesElement.EvaluateFunctionAsync<string>("el => el.textContent");
        
            // Участники дела (вторая ячейка второй строки)
            var partiesElement = detailCells[1];
            var partiesText = await partiesElement.EvaluateFunctionAsync<string>("el => el.textContent");

            // Извлекаем даты
            var dates = ExtractDates(datesText);
        
            // Извлекаем истца и ответчика
            var (plaintiff, defendant) = ExtractParties(partiesText);

            // Очищаем все тексты
            var cleanCourtName = CleanText(courtName);
            var cleanCaseNumber = CleanText(caseNumber);
            var cleanPlaintiff = CleanText(plaintiff);
            var cleanDefendant = CleanText(defendant);

            return new CourtCase
            {
                Title = $"{cleanCourtName} - {cleanCaseNumber}",
                CaseNumber = cleanCaseNumber,
                Link = link,
                CourtType = cleanCourtName,
                Description = $"Поступило: {dates.receivedDate}, Решение: {dates.decisionDate}",
                Subject = $"Истец: {cleanPlaintiff} | Ответчик: {cleanDefendant}"
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при парсинге таблицы дела");
            return null;
        }
    }

    private async Task<List<CourtCase>> ParseAlternativeResultsAsync(IPage page)
    {
        var cases = new List<CourtCase>();
        
        try
        {
            logger.LogInformation("Запуск альтернативного парсинга...");

            // Способ 1: Ищем все строки таблиц
            var allRows = await page.QuerySelectorAllAsync("tr");
            logger.LogInformation("Найдено строк в таблицах: {Count}", allRows.Length);

            foreach (var row in allRows)
            {
                try
                {
                    // Пропускаем заголовки
                    var isActive = await row.EvaluateFunctionAsync<bool>("el => el.classList.contains('active')");
                    if (isActive) continue;

                    var cells = await row.QuerySelectorAllAsync("td");
                    if (cells.Length >= 2)
                    {
                        var caseData = await ParseTableRowAsync(cells);
                        if (caseData != null)
                        {
                            cases.Add(caseData);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Ошибка при парсинге строки таблицы");
                }
            }

            // Способ 2: Ищем ссылки на дела
            if (cases.Count == 0)
            {
                var caseLinks = await page.QuerySelectorAllAsync("a[href*='/extended']");
                logger.LogInformation("Найдено ссылок на дела: {Count}", caseLinks.Length);

                foreach (var link in caseLinks)
                {
                    try
                    {
                        var caseData = await ParseCaseLinkAsync(link);
                        if (caseData != null)
                        {
                            cases.Add(caseData);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Ошибка при парсинге ссылки на дело");
                    }
                }
            }

            // Убираем дубликаты по номеру дела
            cases = cases
                .Where(c => !string.IsNullOrEmpty(c.CaseNumber))
                .GroupBy(c => c.CaseNumber)
                .Select(g => g.First())
                .ToList();

            logger.LogInformation("Альтернативный парсинг завершен. Найдено уникальных дел: {Count}", cases.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при альтернативном парсинге");
        }
        
        return cases;
    }

    private async Task<CourtCase?> ParseTableRowAsync(IElementHandle[] cells)
    {
        try
        {
            // Первая ячейка - даты
            var datesElement = cells[0];
            var datesText = await datesElement.EvaluateFunctionAsync<string>("el => el.textContent");
            
            // Вторая ячейка - участники и возможно ссылка
            var partiesElement = cells[1];
            var partiesText = await partiesElement.EvaluateFunctionAsync<string>("el => el.textContent");
            
            // Ищем ссылку в любой из ячеек
            IElementHandle? linkElement = null;
            foreach (var cell in cells)
            {
                linkElement = await cell.QuerySelectorAsync("a");
                if (linkElement != null) break;
            }

            if (linkElement == null) return null;

            var caseNumber = await linkElement.EvaluateFunctionAsync<string>("el => el.textContent");
            var href = await linkElement.EvaluateFunctionAsync<string>("el => el.getAttribute('href')");
            var link = !string.IsNullOrEmpty(href) ? 
                "https://www.xn--90afdbaav0bd1afy6eub5d.xn--p1ai" + href : 
                string.Empty;

            // Пытаемся найти информацию о суде (может быть в предыдущей строке)
            var courtName = "Не указан";
            var parentRow = await linkElement.EvaluateFunctionAsync<IElementHandle>("el => el.closest('tr').previousElementSibling");
            if (parentRow != null)
            {
                var courtCell = await parentRow.QuerySelectorAsync("td");
                if (courtCell != null)
                {
                    courtName = await courtCell.EvaluateFunctionAsync<string>("el => el.textContent");
                }
            }

            var dates = ExtractDates(datesText);
            var (plaintiff, defendant) = ExtractParties(partiesText);

            return new CourtCase
            {
                Title = $"{CleanText(courtName)} - {CleanText(caseNumber)}",
                CaseNumber = CleanText(caseNumber),
                Link = link,
                CourtType = CleanText(courtName),
                Description = $"Поступило: {dates.receivedDate}, Решение: {dates.decisionDate}",
                Subject = $"Истец: {plaintiff} | Ответчик: {defendant}"
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ошибка при парсинге строки таблицы");
            return null;
        }
    }

    private async Task<CourtCase?> ParseCaseLinkAsync(IElementHandle link)
    {
        try
        {
            var caseNumber = await link.EvaluateFunctionAsync<string>("el => el.textContent");
            var href = await link.EvaluateFunctionAsync<string>("el => el.getAttribute('href')");
            var linkUrl = !string.IsNullOrEmpty(href) ? 
                "https://www.xn--90afdbaav0bd1afy6eub5d.xn--p1ai" + href : 
                string.Empty;

            // Пытаемся найти дополнительную информацию вокруг ссылки
            var parentRow = await link.EvaluateFunctionAsync<IElementHandle>("el => el.closest('tr')");
            string courtName = "Не указан";
            string datesText = "";
            string partiesText = "";

            if (parentRow != null)
            {
                var cells = await parentRow.QuerySelectorAllAsync("td");
                if (cells.Length >= 2)
                {
                    // Первая ячейка - даты
                    datesText = await cells[0].EvaluateFunctionAsync<string>("el => el.textContent");
                    // Вторая ячейка - участники
                    partiesText = await cells[1].EvaluateFunctionAsync<string>("el => el.textContent");
                }

                // Ищем суд в предыдущей строке
                var prevRow = await parentRow.EvaluateFunctionAsync<IElementHandle>("el => el.previousElementSibling");
                if (prevRow != null)
                {
                    var courtCell = await prevRow.QuerySelectorAsync("td");
                    if (courtCell != null)
                    {
                        courtName = await courtCell.EvaluateFunctionAsync<string>("el => el.textContent");
                    }
                }
            }

            var dates = ExtractDates(datesText);
            var (plaintiff, defendant) = ExtractParties(partiesText);

            return new CourtCase
            {
                Title = $"{CleanText(courtName)} - {CleanText(caseNumber)}",
                CaseNumber = CleanText(caseNumber),
                Link = linkUrl,
                CourtType = CleanText(courtName),
                Description = $"Поступило: {dates.receivedDate}, Решение: {dates.decisionDate}",
                Subject = $"Истец: {plaintiff} | Ответчик: {defendant}"
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ошибка при парсинге ссылки на дело");
            return null;
        }
    }

    private (string receivedDate, string decisionDate) ExtractDates(string datesText)
    {
        if (string.IsNullOrWhiteSpace(datesText))
            return ("не указана", "не указана");

        var receivedMatch = Regex.Match(datesText, @"Поступило:\s*([\d\sа-яА-ЯёЁ\.]+)г\.");
        var decisionMatch = Regex.Match(datesText, @"Решение вынесено:\s*([\d\sа-яА-ЯёЁ\.]+)г\.");

        var receivedDate = receivedMatch.Success ? receivedMatch.Groups[1].Value.Trim() : "не указана";
        var decisionDate = decisionMatch.Success ? decisionMatch.Groups[1].Value.Trim() : "не указана";

        return (receivedDate, decisionDate);
    }

    private (string plaintiff, string defendant) ExtractParties(string partiesText)
    {
        if (string.IsNullOrWhiteSpace(partiesText))
            return ("не указан", "не указан");

        // Разделяем по переносам строк
        var lines = partiesText.Split('\n')
            .Select(CleanText)
            .Where(line => !string.IsNullOrEmpty(line))
            .ToList();

        if (lines.Count >= 2)
        {
            // Первая строка - обычно истец (банк/кредитор)
            // Вторая строка - обычно ответчик (должник)
            return (lines[0], lines[1]);
        }
        else if (lines.Count == 1)
        {
            return (lines[0], "не указан");
        }

        return ("не указан", "не указан");
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
    
    
    
    public Task<List<CourtCase>> ParseCasesAsync()
    {
        throw new NotImplementedException();
    }

    public Task<List<CourtCase>> ParseCasesAsync(string keyword ,int page)
    {
        throw new NotImplementedException();
    }
}