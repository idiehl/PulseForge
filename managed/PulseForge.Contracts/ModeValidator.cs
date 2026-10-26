namespace PulseForge.Contracts;

public static class ModeValidator
{
    public static IReadOnlyList<string> Validate(OutputModeV1? mode, int sampleRate = 48_000)
    {
        var errors = new List<string>();
        if (mode is null)
        {
            errors.Add("Mode is required.");
            return errors;
        }

        if (mode.SchemaVersion != OutputModeV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported mode schema {mode.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(mode.Id)) errors.Add("Mode ID is required.");
        if (string.IsNullOrWhiteSpace(mode.Name) || mode.Name.Length > 80) errors.Add("Mode name must contain 1–80 characters.");
        CheckRange(mode.MasterGainDb, -24, 18, "Master gain", errors);
        ValidateChannel(mode.Left, "Left", sampleRate, errors);
        ValidateChannel(mode.Right, "Right", sampleRate, errors);
        ValidateDamping(mode.Damping, sampleRate, errors);
        CheckRange(mode.Stereo.Balance, -1, 1, "Balance", errors);
        CheckRange(mode.Stereo.WidthPercent, 0, 200, "Stereo width", errors);
        CheckRange(mode.Limiter.CeilingDbTp, -12, 0, "Limiter ceiling", errors);
        CheckRange(mode.Limiter.ReleaseMs, 20, 1_000, "Limiter release", errors);
        CheckRange(mode.Limiter.LookaheadMs, 0.5, 5, "Limiter lookahead", errors);
        if (mode.MasterGainDb > 0 && !mode.Limiter.Enabled && !mode.Limiter.ExpertBypassConfirmed)
        {
            errors.Add("Positive master gain requires the limiter or explicit expert bypass confirmation.");
        }

        return errors;
    }

    public static void ThrowIfInvalid(OutputModeV1 mode, int sampleRate = 48_000)
    {
        var errors = Validate(mode, sampleRate);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void ValidateChannel(ChannelChainV1 channel, string label, int sampleRate, List<string> errors)
    {
        CheckRange(channel.InputTrimDb, -24, 12, $"{label} input trim", errors);
        CheckRange(channel.OutputTrimDb, -24, 12, $"{label} output trim", errors);
        CheckRange(channel.DelayMs, 0, 20, $"{label} delay", errors);
        ValidatePass(channel.HighPass, $"{label} high-pass", sampleRate, errors);
        ValidatePass(channel.LowPass, $"{label} low-pass", sampleRate, errors);
        if (channel.ParametricFilters.Count > 12) errors.Add($"{label} has more than 12 parametric filters.");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filter in channel.ParametricFilters)
        {
            if (string.IsNullOrWhiteSpace(filter.Id) || !identifiers.Add(filter.Id))
            {
                errors.Add($"{label} filter IDs must be present and unique.");
            }
            CheckRange(filter.FrequencyHz, 10, Math.Min(24_000, sampleRate * 0.45), $"{label} filter frequency", errors);
            CheckRange(filter.GainDb, -24, 18, $"{label} filter gain", errors);
            CheckRange(filter.Q, 0.1, 24, $"{label} filter Q", errors);
        }
    }

    private static void ValidatePass(PassFilterV1 filter, string label, int sampleRate, List<string> errors)
    {
        CheckRange(filter.FrequencyHz, 10, Math.Min(24_000, sampleRate * 0.45), label, errors);
        if (!Enum.IsDefined(filter.Slope)) errors.Add($"{label} slope is invalid.");
    }

    private static void ValidateDamping(DampingSettingsV1 damping, int sampleRate, List<string> errors)
    {
        CheckRange(damping.SensitivityDb, 0, 18, "Damping sensitivity", errors);
        CheckRange(damping.MinimumFrequencyHz, 20, Math.Min(20_000, sampleRate * 0.45), "Damping minimum frequency", errors);
        CheckRange(damping.MaximumFrequencyHz, 20, Math.Min(20_000, sampleRate * 0.45), "Damping maximum frequency", errors);
        if (damping.MaximumFrequencyHz <= damping.MinimumFrequencyHz) errors.Add("Damping maximum frequency must exceed its minimum.");
        CheckRange(damping.AttackMs, 1, 100, "Damping attack", errors);
        CheckRange(damping.ReleaseMs, 20, 1_000, "Damping release", errors);
        CheckRange(damping.MaximumReductionDb, 0, 12, "Damping maximum reduction", errors);
    }

    private static void CheckRange(double value, double minimum, double maximum, string label, List<string> errors)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            errors.Add($"{label} must be finite and between {minimum} and {maximum}.");
        }
    }
}
