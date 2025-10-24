namespace CourtParser.Core.Interfaces;

public interface IRegionJobService
{
    Task ProcessRegionAsync(string regionName);
}