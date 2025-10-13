using System.Text;
using CourtParser.Infrastructure;
using CourtParser.Worker;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<Worker>();
builder.Services.AddInfrastructure(builder.Configuration);

var host = builder.Build();

host.Run();