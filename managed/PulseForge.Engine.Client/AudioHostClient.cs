using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PulseForge.Contracts;

namespace PulseForge.Engine.Client;

public sealed class AudioHostClient
{
    public Task<HandshakeResponseV1> HandshakeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HandshakeResponseV1>("handshake", null, cancellationToken);

    public async Task<EngineStateV1> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return await GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsHostUnavailable(exception))
        {
            LaunchHost();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                try
                {
                    await HandshakeAsync(cancellationToken).ConfigureAwait(false);
                    return await GetStateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception retryException) when (!cancellationToken.IsCancellationRequested && IsHostUnavailable(retryException))
                {
                    last = retryException;
                }
            }
            throw new IOException("PulseForge Audio Host did not become ready.", last);
        }
    }

    public Task<EngineStateV1> GetStateAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("getState", null, cancellationToken);

    public Task<OutputModeV1> GetModeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<OutputModeV1>("getMode", null, cancellationToken);

    public Task<EngineStateV1> RefreshDevicesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("refreshDevices", null, cancellationToken);

    public Task<EngineStateV1> ApplyModeAsync(OutputModeV1 mode, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("applyMode", mode, cancellationToken);

    public Task<EngineStateV1> UpdateParameterAsync(ParameterUpdateV1 update, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("updateParameter", update, cancellationToken);

    public Task<EngineStateV1> SelectOutputAsync(SelectOutputRequestV1 request, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("selectOutput", request, cancellationToken);

    public Task<EngineStateV1> SetBypassAsync(bool bypass, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("setBypass", bypass, cancellationToken);

    public Task<EngineStateV1> SetDemoAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("setDemo", enabled, cancellationToken);

    public Task<EngineStateV1> StartBridgeAsync(StartBridgeRequestV1 request, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("startBridge", request, cancellationToken);

    public Task<EngineStateV1> StopBridgeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("stopBridge", null, cancellationToken);

    public Task<EngineStateV1> SetTelemetryFrozenAsync(bool frozen, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("freezeTelemetry", frozen, cancellationToken);

    public Task<EngineStateV1> ResetTelemetryAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStateV1>("resetTelemetry", null, cancellationToken);

    private static async Task<T> SendAsync<T>(string command, object? payload, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", IpcNames.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var request = new ControlRequestV1
        {
            Command = command,
            Payload = payload is null
                ? JsonSerializer.SerializeToElement<object?>(null, ContractJson.Options)
                : JsonSerializer.SerializeToElement(payload, ContractJson.Options),
        };
        var json = JsonSerializer.Serialize(request, ContractJson.CompactOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("PulseForge Audio Host closed the control pipe without a response.");
        var response = JsonSerializer.Deserialize<ControlResponseV1>(line, ContractJson.CompactOptions)
            ?? throw new IOException("PulseForge Audio Host returned an empty response.");
        if (response.Version != IpcNames.ProtocolVersion) throw new IOException($"Unsupported host protocol {response.Version}.");
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "PulseForge Audio Host rejected the command.");
        return response.Payload.Deserialize<T>(ContractJson.Options)
            ?? throw new IOException("PulseForge Audio Host returned no command payload.");
    }

    private static void LaunchHost()
    {
        var executable = LocateHostExecutable();
        ProcessStartInfo startInfo;
        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo("dotnet", $"\"{executable}\"");
        }
        else
        {
            startInfo = new ProcessStartInfo(executable);
        }
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.WorkingDirectory = Path.GetDirectoryName(executable)!;
        Process.Start(startInfo)?.Dispose();
    }

    private static bool IsHostUnavailable(Exception exception) =>
        exception is IOException or TimeoutException or OperationCanceledException;

    private static string LocateHostExecutable()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "AudioHost", "PulseForge.AudioHost.exe");
        if (File.Exists(packaged)) return packaged;
        var packagedDll = Path.ChangeExtension(packaged, ".dll");
        if (File.Exists(packagedDll)) return packagedDll;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var managed = Path.Combine(directory.FullName, "managed", "PulseForge.AudioHost", "bin");
            if (Directory.Exists(managed))
            {
                var candidate = Directory.EnumerateFiles(managed, "PulseForge.AudioHost.dll", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (candidate is not null) return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("PulseForge.AudioHost could not be located beside the editor or in this development checkout.");
    }
}
