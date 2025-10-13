using CourtParser.Models.Entities;

namespace CourtParser.Core.Interfaces;

/// <summary>
/// Интерфейс для парсера
/// </summary>
public interface IParser
{
    /// <summary>
    /// Выполняет поиск дел по ключевому слову.
    /// </summary>
    Task<List<CourtCase>> ParseCasesAsync(string keyword, int page = 1);
    
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task<List<CourtCase>> ParseCasesAsync();
    
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task<List<CourtCase>> ParseCasesAsync(int page);
}