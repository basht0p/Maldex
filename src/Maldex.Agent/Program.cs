using Maldex.Agent.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Maldex.Agent runs only on Windows");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "MaldexAgent");
builder.Services.AddHostedService<AgentService>();
await builder.Build().RunAsync();
return 0;
