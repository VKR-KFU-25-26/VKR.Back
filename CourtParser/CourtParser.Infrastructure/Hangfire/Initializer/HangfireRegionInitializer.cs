using CourtParser.Core.Interfaces;
using CourtParser.Models.Regions;
using Hangfire;

namespace CourtParser.Infrastructure.Hangfire.Initializer;

public static class HangfireRegionInitializer
{
    [Obsolete("Obsolete")]
    public static void ScheduleRegionJobs()
    {
        var allRegions = RussianRegions.GetAllRegions();
        
        foreach (var region in allRegions)
        {
            
            // var jobId = $"region_job_{region.Replace(" ", "_")}";
            //
            // BackgroundJob.Enqueue<IRegionJobService>(
            //     x => x.ProcessRegionAsync(region)
            // );
            //
            // RecurringJob.AddOrUpdate<IRegionJobService>(
            //     jobId,
            //     x => x.ProcessRegionAsync(region),
            //     Cron.Hourly(59)
            // );
        }
    }
}