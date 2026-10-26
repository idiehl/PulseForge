using PulseForge.Contracts;
using Xunit;

namespace PulseForge.Contracts.Tests;

public sealed class DeviceProfileSessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PulseForge.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RapidPolicyChanges_PersistTheLastChoiceAfterFlush()
    {
        var library = new ModeLibrary(root);
        var session = new DeviceProfileSession(library, []);
        var device = Device("speakers");
        var saves = Enumerable.Range(0, 32)
            .Select(index => Save(session, device, [device], (BufferProfileV1)(index % 3), $"mode-{index}"))
            .ToArray();

        await session.FlushAsync();
        await Task.WhenAll(saves);

        var restored = Assert.Single(await library.LoadDeviceProfilesAsync());
        Assert.Equal("mode-31", restored.AssignedModeId);
        Assert.Equal(BufferProfileV1.Balanced, restored.BufferProfile);
        Assert.Equal(restored, Assert.Single(session.Profiles));
    }

    [Fact]
    public async Task RapidDeviceChanges_RetainEveryProfile()
    {
        var library = new ModeLibrary(root);
        var session = new DeviceProfileSession(library, []);
        var devices = Enumerable.Range(0, 12).Select(index => Device($"device-{index}")).ToArray();
        var saves = devices.Select((device, index) =>
            Save(session, device, devices, BufferProfileV1.Safe, $"mode-{index}")).ToArray();

        await Task.WhenAll(saves);

        var restored = await library.LoadDeviceProfilesAsync();
        Assert.Equal(devices.Length, restored.Count);
        for (var index = 0; index < devices.Length; index++)
        {
            var profile = Assert.Single(restored, profile => profile.EndpointId == devices[index].Id);
            Assert.Equal($"mode-{index}", profile.AssignedModeId);
        }
    }

    [Fact]
    public async Task ReenumeratedDevice_PreservesProfileIdentityAndChannelMapping()
    {
        var library = new ModeLibrary(root);
        var existing = new DeviceProfileV1
        {
            Id = "retained-profile",
            EndpointId = "old-endpoint",
            StableEndpointId = "stable-dac",
            HardwareId = "hardware-dac",
            FriendlyName = "Previous name",
            ChannelMapping = "R,L",
        };
        await library.SaveDeviceProfilesAsync([existing]);
        var session = new DeviceProfileSession(library, [existing]);
        var device = Device("dac") with { StableId = existing.StableEndpointId, HardwareId = null };

        await Save(session, device, [device], BufferProfileV1.Safe, "saved-mode");

        var restored = Assert.Single(await library.LoadDeviceProfilesAsync());
        Assert.Equal(existing.Id, restored.Id);
        Assert.Equal(existing.ChannelMapping, restored.ChannelMapping);
        Assert.Equal(existing.HardwareId, restored.HardwareId);
        Assert.Equal(device.Id, restored.EndpointId);
        Assert.Equal(device.Name, restored.FriendlyName);
    }

    [Fact]
    public async Task FailedWrite_LeavesSnapshotIntactAndAllowsRetry()
    {
        Directory.CreateDirectory(root);
        var blockedRoot = Path.Combine(root, "blocked-root");
        await File.WriteAllTextAsync(blockedRoot, "A file cannot contain the device-profile directory.");
        var library = new ModeLibrary(blockedRoot);
        var session = new DeviceProfileSession(library, []);
        var device = Device("speakers");

        await Assert.ThrowsAnyAsync<IOException>(() => Save(session, device, [device], BufferProfileV1.Safe, "failed"));
        await Assert.ThrowsAnyAsync<IOException>(() => session.FlushAsync());
        Assert.Empty(session.Profiles);

        File.Delete(blockedRoot);
        await Save(session, device, [device], BufferProfileV1.Balanced, "retry");
        await session.FlushAsync();

        Assert.Equal("retry", Assert.Single(await library.LoadDeviceProfilesAsync()).AssignedModeId);
        Assert.Equal("retry", Assert.Single(session.Profiles).AssignedModeId);
    }

    private static AudioDeviceV1 Device(string name) => new()
    {
        Id = $"endpoint-{name}",
        StableId = $"stable-{name}",
        HardwareId = $"hardware-{name}",
        Name = name,
    };

    private static Task Save(
        DeviceProfileSession session,
        AudioDeviceV1 device,
        IReadOnlyList<AudioDeviceV1> devices,
        BufferProfileV1 buffer,
        string mode) => session.SaveAsync(
            device, devices, OutputTransportV1.RawShared, buffer, 48_000,
            new AudioFormatV1(), mode);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
