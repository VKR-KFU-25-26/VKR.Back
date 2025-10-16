namespace CourtParser.Models.DTOs;
public class CaseSearchParams
{
    public List<string>? Regions { get; set; }
    public string CaseType { get; set; } = "gr_first";
    public string Category { get; set; } = "46";
    public string Subcategory { get; set; } = "53";
    public int MaxPages { get; set; } = 1;
}