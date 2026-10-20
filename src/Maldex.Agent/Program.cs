using Maldex.Agent.Windows;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Maldex.Agent runs only on Windows");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
return await AgentRunner.RunAsync(cts.Token);
