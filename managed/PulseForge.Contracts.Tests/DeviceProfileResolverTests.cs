using PulseForge.Contracts;
using Xunit;

namespace PulseForge.Contracts.Tests;

public sealed class DeviceProfileResolverTests
{
    [Fact]
    public void ExactEndpointId_WinsEvenWhenHardwareFallbackIsAmbiguous()
    {
        var selected = Device("endpoint-b", "stable-b", "codec-1", "Speakers B");
        var profiles = new[]
        {
            Profile("profile-a", "endpoint-a", "stable-a", "codec-1"),
            Profile("profile-b", "endpoint-b", "stable-b", "codec-1"),
        };

        var resolved = DeviceProfileResolver.Resolve(selected, profiles, [
            Device("endpoint-a", "stable-a", "codec-1", "Speakers A"),
            selected,
        ]);

        Assert.Equal("profile-b", resolved?.Id);
    }

    [Fact]
    public void StableEndpointId_RecallsProfileWhenCurrentIdDiffers()
    {
        var selected = Device("endpoint-new", "stable-known", "codec-1", "Speakers");
        var profile = Profile("profile", "endpoint-old", "stable-known", "codec-1");

        Assert.Same(profile, DeviceProfileResolver.Resolve(selected, [profile], [selected]));
    }

    [Fact]
    public void UniqueHardwareId_RecallsProfileAfterEndpointReenumeration()
    {
        var selected = Device("endpoint-new", "stable-new", "codec-1", "Speakers");
        var profile = Profile("profile", "endpoint-old", "stable-old", "codec-1");

        Assert.Same(profile, DeviceProfileResolver.Resolve(selected, [profile], [selected]));
    }

    [Fact]
    public void SharedHardwareIdAcrossCurrentOutputs_DoesNotRecallAutomatically()
    {
        var selected = Device("endpoint-new-a", "stable-new-a", "codec-1", "Speakers A");
        var sibling = Device("endpoint-new-b", "stable-new-b", "codec-1", "Speakers B");
        var profile = Profile("profile", "endpoint-old", "stable-old", "codec-1");

        Assert.Null(DeviceProfileResolver.Resolve(selected, [profile], [selected, sibling]));
    }

    [Fact]
    public void SharedHardwareIdAcrossProfiles_DoesNotRecallAutomatically()
    {
        var selected = Device("endpoint-new", "stable-new", "codec-1", "Speakers");
        var profiles = new[]
        {
            Profile("profile-a", "endpoint-old-a", "stable-old-a", "codec-1"),
            Profile("profile-b", "endpoint-old-b", "stable-old-b", "codec-1"),
        };

        Assert.Null(DeviceProfileResolver.Resolve(selected, profiles, [selected]));
    }

    private static AudioDeviceV1 Device(string id, string stableId, string hardwareId, string name) => new()
    {
        Id = id,
        StableId = stableId,
        HardwareId = hardwareId,
        Name = name,
    };

    private static DeviceProfileV1 Profile(string id, string endpointId, string stableEndpointId, string hardwareId) => new()
    {
        Id = id,
        EndpointId = endpointId,
        StableEndpointId = stableEndpointId,
        HardwareId = hardwareId,
        FriendlyName = "Fixture",
    };
}
