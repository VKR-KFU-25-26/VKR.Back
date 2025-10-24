using System.Text;
using CourtParser.Core.Interfaces;
using CourtParser.Infrastructure;
using CourtParser.Infrastructure.Hangfire.Initializer;
using CourtParser.Infrastructure.Hangfire.Services;
using Hangfire;
using Hangfire.Dashboard.BasicAuthorization;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// Hangfire + PostgreSQL
builder.Services.AddHangfire(config =>
{
    config.UsePostgreSqlStorage(builder.Configuration.GetConnectionString("DefaultConnection"));
});
builder.Services.AddHangfireServer();

builder.Services.AddScoped<IRegionJobService, RegionJobService>();
//builder.Services.AddHostedService<Worker>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddControllers();

var app = builder.Build();

app.UseRouting();

app.UseEndpoints(endpoints =>
{
    // Hangfire Dashboard с базовой авторизацией
    endpoints.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization =
        [
            new BasicAuthAuthorizationFilter(new BasicAuthAuthorizationFilterOptions
            {
                RequireSsl = false,
                SslRedirect = false,
                LoginCaseSensitive = true,
                Users =
                [
                    new BasicAuthAuthorizationUser
                    {
                        Login = "admin",
                        PasswordClear = "admin"
                    }
                ]
            })
        ]
    });
});

using (app.Services.CreateScope())
{
    Console.WriteLine("📅 Регистрация Hangfire задач...");
    HangfireRegionInitializer.ScheduleRegionJobs();
}

app.Run("http://0.0.0.0:5000");