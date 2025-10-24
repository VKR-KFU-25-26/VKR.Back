namespace CourtParser.Models.Entities;

/// <summary>
/// Сырое дело, получаемое напрямую с сайта суда
/// </summary>
public class CourtCase
{
    public string Title { get; set; } = null!;
    public string Link { get; set; } = null!;
    public string CaseNumber { get; set; } = null!;
    public string CourtType { get; set; } = null!;
    public string Description { get; set; } = null!;
    
    /// <summary>
    /// Ссылка на оригинальный сайт суда
    /// </summary>
    public string OriginalCaseLink { get; set; } = string.Empty;

    /// <summary>
    /// Суть спора (например: "о несостоятельности (банкротстве) физических лиц")
    /// </summary>
    public string Subject { get; set; } = null!;

    /// <summary>
    /// Есть ли решение
    /// </summary>
    public bool HasDecision { get; set; }
    
    /// <summary>
    /// Ссылка на решение
    /// </summary>
    public string DecisionLink { get; set; } = string.Empty;
    
    /// <summary>
    /// Дата решения
    /// </summary>
    public DateTime? DecisionDate { get; set; }
    
    /// <summary>
    /// Тип решения (мотивированное, определение и т.д.)
    /// </summary>
    public string DecisionType { get; set; } = string.Empty;
    
    /// <summary>
    /// Федеральный округ
    /// </summary>
    public string FederalDistrict { get; set; } = string.Empty;
    
    /// <summary>
    /// Регион (субъект РФ)
    /// </summary>
    public string Region { get; set; } = string.Empty;
    
    /// <summary>
    /// Истец (отдельное поле)
    /// </summary>
    public string Plaintiff { get; set; } = string.Empty;
    
    /// <summary>
    /// Ответчик (отдельное поле)
    /// </summary>
    public string Defendant { get; set; } = string.Empty;
    
    /// <summary>
    /// Третьи лица (если есть)
    /// </summary>
    public string ThirdParties { get; set; } = string.Empty;
    
    /// <summary>
    /// Дата поступления дела
    /// </summary>
    public DateTime? ReceivedDate { get; set; }
    
    /// <summary>
    /// Категория дела
    /// </summary>
    public string CaseCategory { get; set; } = string.Empty;
    
    /// <summary>
    /// Подкатегория дела
    /// </summary>
    public string CaseSubcategory { get; set; } = string.Empty;
    
    /// <summary>
    /// Решение контент
    /// </summary>
    public string? DecisionContent { get; set; }
}