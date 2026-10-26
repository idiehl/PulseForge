using System.Text.Json;
using PulseForge.Contracts;
using Xunit;

namespace PulseForge.Contracts.Tests;

public sealed class ModeLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PulseForge.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuiltInModes_AreValidAndImmutable()
    {
        Assert.Equal(4, BuiltInModes.All.Count);
        Assert.All(BuiltInModes.All, mode =>
        {
            Assert.True(mode.IsBuiltIn);
            Assert.Empty(ModeValidator.Validate(mode));
            Assert.Equal(12, mode.Left.ParametricFilters.Count);
            Assert.Equal(12, mode.Right.ParametricFilters.Count);
        });
    }

    [Fact]
    public void PositiveBoost_RequiresLimiterOrExpertConfirmation()
    {
        var mode = ModeFactory.CreateEditable() with
        {
            MasterGainDb = 8,
            Limiter = new LimiterSettingsV1 { Enabled = false },
        };
        Assert.Contains(ModeValidator.Validate(mode), error => error.Contains("Positive master gain", StringComparison.Ordinal));
        var confirmed = mode with { Limiter = mode.Limiter with { ExpertBypassConfirmed = true } };
        Assert.Empty(ModeValidator.Validate(confirmed));
    }

    [Fact]
    public async Task UserMode_RoundTripsAtomically()
    {
        var library = new ModeLibrary(root);
        var mode = ModeFactory.CreateEditable("Precise phones") with { MasterGainDb = -1.5 };
        await library.SaveModeAsync(mode);
        var loaded = await library.LoadModesAsync();
        var restored = Assert.Single(loaded, candidate => candidate.Id == mode.Id);
        Assert.Equal(mode.Name, restored.Name);
        Assert.Equal(mode.MasterGainDb, restored.MasterGainDb);
        Assert.True(File.Exists(Path.Combine(root, "Modes", mode.Id + ".pulseforge-mode")));
    }

    [Fact]
    public async Task Import_DoesNotOverwriteAnExistingMode()
    {
        var library = new ModeLibrary(root);
        var mode = ModeFactory.CreateEditable("Imported profile");
        await library.SaveModeAsync(mode);
        var source = Path.Combine(root, "incoming.pulseforge-mode");
        await File.WriteAllTextAsync(source, JsonSerializer.Serialize(mode, ContractJson.Options));
        var imported = await library.ImportModeAsync(source);
        Assert.NotEqual(mode.Id, imported.Id);
        Assert.NotEqual(mode.Name, imported.Name);
        var all = await library.LoadModesAsync();
        Assert.Equal(2, all.Count(candidate => candidate.Name.StartsWith("Imported profile", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DeviceProfiles_StayMachineLocalAndRoundTrip()
    {
        var library = new ModeLibrary(root);
        var profiles = new[]
        {
            new DeviceProfileV1
            {
                Id = Guid.NewGuid().ToString("D"),
                StableEndpointId = "stable-speakers",
                HardwareId = "hardware-speakers",
                EndpointId = "endpoint-speakers",
                FriendlyName = "Studio monitors",
                AssignedModeId = "builtin.reference-flat",
            },
        };
        await library.SaveDeviceProfilesAsync(profiles);
        var restored = Assert.Single(await library.LoadDeviceProfilesAsync());
        Assert.Equal("stable-speakers", restored.StableEndpointId);
        Assert.Equal("hardware-speakers", restored.HardwareId);
        Assert.Equal(BufferProfileV1.Balanced, restored.BufferProfile);
    }

    [Fact]
    public async Task CorruptPrimaryMode_RecoversFromLastGoodCopy()
    {
        var library = new ModeLibrary(root);
        var original = ModeFactory.CreateEditable("Recovery fixture") with { MasterGainDb = -2 };
        await library.SaveModeAsync(original);
        await library.SaveModeAsync(original with { MasterGainDb = -4 });
        var path = Path.Combine(root, "Modes", original.Id + ".pulseforge-mode");
        await File.WriteAllTextAsync(path, "{not valid JSON");
        var recovered = Assert.Single(await library.LoadModesAsync(), mode => mode.Id == original.Id);
        Assert.Equal(-2, recovered.MasterGainDb);
    }

    [Fact]
    public async Task CorruptDeviceProfiles_RecoverFromLastGoodCopy()
    {
        var library = new ModeLibrary(root);
        var profile = new DeviceProfileV1
        {
            Id = Guid.NewGuid().ToString("D"),
            StableEndpointId = "stable-dac",
            EndpointId = "endpoint-dac",
            FriendlyName = "Reference DAC",
        };
        await library.SaveDeviceProfilesAsync([profile]);
        await library.SaveDeviceProfilesAsync([profile with { BufferProfile = BufferProfileV1.Safe }]);
        await File.WriteAllTextAsync(Path.Combine(root, "device-profiles.json"), "[] trailing");
        var recovered = Assert.Single(await library.LoadDeviceProfilesAsync());
        Assert.Equal(BufferProfileV1.Balanced, recovered.BufferProfile);
    }

    [Fact]
    public async Task MalformedImport_IsRejectedWithoutCreatingAMode()
    {
        var library = new ModeLibrary(root);
        var source = Path.Combine(root, "malformed.pulseforge-mode");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "{\"schemaVersion\":999}");
        await Assert.ThrowsAnyAsync<Exception>(() => library.ImportModeAsync(source));
        Assert.Equal(BuiltInModes.All.Count, (await library.LoadModesAsync()).Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
