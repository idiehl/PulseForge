using PulseForge.AudioHost;

using var singleInstance = new Mutex(initiallyOwned: true, @"Local\PulseForge.AudioHost.v1", out var created);
if (!created) return 0;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    await using var host = await AudioHostRuntime.CreateAsync(cancellation.Token);
    await host.RunAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 0;
}
catch (Exception exception)
{
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseForge");
    Directory.CreateDirectory(root);
    await File.WriteAllTextAsync(Path.Combine(root, "audio-host-failure.txt"), exception.ToString());
    return 1;
}
