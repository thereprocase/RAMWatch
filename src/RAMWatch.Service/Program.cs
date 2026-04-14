using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RAMWatch.Service;
using RAMWatch.Service.Services;

DataDirectory.EnsureCreated();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "RAMWatch";
});

builder.Services.AddSingleton<SettingsManager>();
builder.Services.AddHostedService<RamWatchService>();

var host = builder.Build();
host.Run();
