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
    /// Суть спора (например: "о несостоятельности (банкротстве) физических лиц")
    /// </summary>
    public string Subject { get; set; } = null!;
}