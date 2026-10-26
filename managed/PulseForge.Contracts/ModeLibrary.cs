using System.Text.Json;

namespace PulseForge.Contracts;

public sealed class ModeLibrary
{
    private readonly string rootDirectory;
    private readonly string modesDirectory;
    private readonly string devicesPath;

    public ModeLibrary(string? rootDirectory = null)
    {
        this.rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseForge");
        modesDirectory = Path.Combine(this.rootDirectory, "Modes");
        devicesPath = Path.Combine(this.rootDirectory, "device-profiles.json");
    }

    public string RootDirectory => rootDirectory;

    public async Task<IReadOnlyList<OutputModeV1>> LoadModesAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(modesDirectory);
        var modes = new List<OutputModeV1>(BuiltInModes.All);
        foreach (var path in Directory.EnumerateFiles(modesDirectory, "*.pulseforge-mode").Order(StringComparer.OrdinalIgnoreCase))
        {
            var mode = await TryReadModeAsync(path, cancellationToken).ConfigureAwait(false);
            if (mode is null)
            {
                mode = await TryReadModeAsync(path + ".last-good", cancellationToken).ConfigureAwait(false);
            }
            if (mode is not null) modes.Add(mode with { IsBuiltIn = false });
        }
        return modes;
    }

    public async Task SaveModeAsync(OutputModeV1 mode, CancellationToken cancellationToken = default)
    {
        if (mode.IsBuiltIn) throw new InvalidOperationException("Built-in modes must be duplicated before editing.");
        ModeValidator.ThrowIfInvalid(mode);
        Directory.CreateDirectory(modesDirectory);
        var path = Path.Combine(modesDirectory, SafeFileName(mode.Id) + ".pulseforge-mode");
        await WriteAtomicAsync(path, JsonSerializer.Serialize(mode, ContractJson.Options), cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportModeAsync(OutputModeV1 mode, string destinationPath, CancellationToken cancellationToken = default)
    {
        ModeValidator.ThrowIfInvalid(mode);
        var portable = mode with { IsBuiltIn = false };
        await WriteAtomicAsync(destinationPath, JsonSerializer.Serialize(portable, ContractJson.Options), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutputModeV1> ImportModeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var imported = JsonSerializer.Deserialize<OutputModeV1>(json, ContractJson.Options)
            ?? throw new InvalidDataException("The selected mode file is empty.");
        ModeValidator.ThrowIfInvalid(imported);
        var existing = await LoadModesAsync(cancellationToken).ConfigureAwait(false);
        var usedIds = existing.Select(mode => mode.Id).ToHashSet(StringComparer.Ordinal);
        var usedNames = existing.Select(mode => mode.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uniqueName = imported.Name;
        var suffix = 2;
        while (usedNames.Contains(uniqueName)) uniqueName = $"{imported.Name} (Imported {suffix++})";
        var result = imported with
        {
            Id = usedIds.Contains(imported.Id) ? Guid.NewGuid().ToString("D") : imported.Id,
            Name = uniqueName,
            IsBuiltIn = false,
        };
        await SaveModeAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<DeviceProfileV1>> LoadDeviceProfilesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { devicesPath, devicesPath + ".last-good" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var profiles = JsonSerializer.Deserialize<List<DeviceProfileV1>>(json, ContractJson.Options);
                if (profiles is not null && profiles.All(IsValidDeviceProfile)) return profiles;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Continue to the last-good copy when the primary file is unreadable.
            }
        }
        return [];
    }

    public Task SaveDeviceProfilesAsync(IReadOnlyList<DeviceProfileV1> profiles, CancellationToken cancellationToken = default)
    {
        foreach (var profile in profiles)
        {
            if (!IsValidDeviceProfile(profile)) throw new InvalidDataException("A device profile is invalid.");
        }
        return WriteAtomicAsync(devicesPath, JsonSerializer.Serialize(profiles, ContractJson.Options), cancellationToken);
    }

    public static OutputModeV1 Duplicate(OutputModeV1 source, string? name = null) => source with
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = name ?? $"{source.Name} Copy",
        IsBuiltIn = false,
        Left = source.Left with { ParametricFilters = source.Left.ParametricFilters.Select(filter => filter with { }).ToArray() },
        Right = source.Right with { ParametricFilters = source.Right.ParametricFilters.Select(filter => filter with { }).ToArray() },
    };

    private static async Task WriteAtomicAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("A destination directory is required.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken).ConfigureAwait(false);
            if (File.Exists(path))
            {
                var backup = path + ".last-good";
                File.Copy(path, backup, overwrite: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(id.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static async Task<OutputModeV1?> TryReadModeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var mode = JsonSerializer.Deserialize<OutputModeV1>(json, ContractJson.Options);
            return mode is not null && ModeValidator.Validate(mode).Count == 0 ? mode : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsValidDeviceProfile(DeviceProfileV1 profile) =>
        profile.SchemaVersion == DeviceProfileV1.CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(profile.Id) &&
        !string.IsNullOrWhiteSpace(profile.EndpointId) &&
        profile.PreferredSampleRate is >= 44_100 and <= 192_000 &&
        profile.SupportedFormat.Channels == 2 &&
        profile.SupportedFormat.SampleRate is >= 44_100 and <= 192_000 &&
        Enum.IsDefined(profile.Transport) &&
        Enum.IsDefined(profile.BufferProfile);
}
