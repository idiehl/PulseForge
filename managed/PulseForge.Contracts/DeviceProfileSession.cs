namespace PulseForge.Contracts;

public sealed class DeviceProfileSession
{
    private readonly ModeLibrary library;
    private readonly object queueGate = new();
    private IReadOnlyList<DeviceProfileV1> profiles;
    private Task pendingSave = Task.CompletedTask;

    public DeviceProfileSession(ModeLibrary library, IReadOnlyList<DeviceProfileV1> loadedProfiles)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(loadedProfiles);
        this.library = library;
        profiles = Array.AsReadOnly(loadedProfiles.ToArray());
    }

    public IReadOnlyList<DeviceProfileV1> Profiles => Volatile.Read(ref profiles);

    public Task SaveAsync(
        AudioDeviceV1 device,
        IReadOnlyList<AudioDeviceV1> availableDevices,
        OutputTransportV1 transport,
        BufferProfileV1 bufferProfile,
        int preferredSampleRate,
        AudioFormatV1 supportedFormat,
        string? assignedModeId)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(availableDevices);
        ArgumentNullException.ThrowIfNull(supportedFormat);
        var availableSnapshot = availableDevices.ToArray();
        lock (queueGate)
        {
            pendingSave = SaveAfterAsync(
                pendingSave, device, availableSnapshot, transport, bufferProfile,
                preferredSampleRate, supportedFormat, assignedModeId);
            return pendingSave;
        }
    }

    public Task FlushAsync()
    {
        lock (queueGate) return pendingSave;
    }

    private async Task SaveAfterAsync(
        Task previousSave,
        AudioDeviceV1 device,
        IReadOnlyList<AudioDeviceV1> availableDevices,
        OutputTransportV1 transport,
        BufferProfileV1 bufferProfile,
        int preferredSampleRate,
        AudioFormatV1 supportedFormat,
        string? assignedModeId)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
        }
        catch
        {
            // The caller of the failed operation receives its error; subsequent saves can retry.
        }

        var updated = Profiles.ToList();
        var existing = DeviceProfileResolver.Resolve(device, updated, availableDevices);
        var profile = (existing ?? new DeviceProfileV1
        {
            Id = Guid.NewGuid().ToString("D"),
            EndpointId = device.Id,
            FriendlyName = device.Name,
        }) with
        {
            StableEndpointId = device.StableId ?? existing?.StableEndpointId,
            HardwareId = device.HardwareId ?? existing?.HardwareId,
            EndpointId = device.Id,
            FriendlyName = device.Name,
            Transport = transport,
            BufferProfile = bufferProfile,
            PreferredSampleRate = preferredSampleRate,
            SupportedFormat = supportedFormat,
            AssignedModeId = assignedModeId,
        };
        if (existing is null) updated.Add(profile);
        else updated[updated.IndexOf(existing)] = profile;

        await library.SaveDeviceProfilesAsync(updated).ConfigureAwait(false);
        Volatile.Write(ref profiles, Array.AsReadOnly(updated.ToArray()));
    }
}
