using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PulseForge.Contracts;
using PulseForge.Engine.Client;

namespace PulseForge.Editor;

public sealed partial class MainWindow : Window
{
    private readonly ModeLibrary modeLibrary = new();
    private readonly AudioHostClient host = new();
    private readonly ObservableCollection<ModeListItem> modeItems = [];
    private readonly ObservableCollection<DeviceChoice> sourceDevices = [];
    private readonly ObservableCollection<DeviceChoice> outputDevices = [];
    private readonly DispatcherTimer telemetryTimer;
    private DeviceProfileSession? deviceSession;
    private IReadOnlyList<DeviceProfileV1> DeviceProfiles => deviceSession?.Profiles ?? [];
    private SharedTelemetryReader? telemetryReader;
    private OutputModeV1? currentMode;
    private EngineStateV1? engineState;
    private TelemetryFrameV1? telemetry;
    private CancellationTokenSource? applyDebounce;
    private bool showingLeft = true;
    private bool updatingControls;
    private bool pollingState;
    private bool flushingClose;
    private bool closeApproved;
    private Task pendingModeSave = Task.CompletedTask;
    private long lastTelemetrySequence = -1;
    private DateTime nextStatePoll = DateTime.MinValue;

    public MainWindow() : this(connectRuntime: true)
    {
    }

    public MainWindow(bool connectRuntime)
    {
        InitializeComponent();
        ModeList.ItemsSource = modeItems;
        SourceDeviceBox.ItemsSource = sourceDevices;
        OutputDeviceBox.ItemsSource = outputDevices;
        EqGraph.FilterEdited += OnGraphFilterEdited;
        EqGraph.SelectedBandChanged += OnGraphSelectedBandChanged;
        telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        telemetryTimer.Tick += OnTelemetryTick;
        if (connectRuntime) Opened += OnOpened;
        else InitializeVisualPreview();
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void InitializeVisualPreview()
    {
        updatingControls = true;
        var modes = BuiltInModes.All;
        foreach (var mode in modes) modeItems.Add(new ModeListItem(mode));
        currentMode = modes[1];
        ModeList.SelectedItem = modeItems[1];
        updatingControls = false;

        var previewDevices = new AudioDeviceV1[]
        {
            new() { Id = "pulseforge-preview", StableId = "pulseforge-preview", HardwareId = "SWD\\PulseForgeVirtualAudio", Name = "PulseForge Virtual Output", IsDefault = true, IsPulseForgeVirtual = true },
            new() { Id = "studio-preview", StableId = "studio-preview", HardwareId = "HDAUDIO\\Preview", Name = "Studio Headphones · Preview", IsDefault = false },
        };
        ApplyEngineState(new EngineStateV1
        {
            Status = "Visual preview · no audio takeover",
            SampleRate = 48_000,
            PeriodFrames = 128,
            AddedLatencyMs = 7.3,
            IsDemoSignal = true,
            TelemetryFrozen = false,
            Devices = previewDevices,
        }, rebuildDevices: true);

        var preLeft = new float[TelemetryFrameV1.WaveformCapacity];
        var preRight = new float[TelemetryFrameV1.WaveformCapacity];
        var postLeft = new float[TelemetryFrameV1.WaveformCapacity];
        var postRight = new float[TelemetryFrameV1.WaveformCapacity];
        for (var index = 0; index < preLeft.Length; index++)
        {
            var phase = index * Math.PI * 2 / 64.0;
            preLeft[index] = (float)(Math.Sin(phase) * 0.42 + Math.Sin(phase * 2.31) * 0.12);
            preRight[index] = (float)(Math.Sin(phase + 0.42) * 0.38 + Math.Sin(phase * 1.77) * 0.1);
            postLeft[index] = preLeft[index] * 0.72F;
            postRight[index] = preRight[index] * 0.74F;
        }
        var spectrumLeft = new float[TelemetryFrameV1.SpectrumCapacity];
        var spectrumRight = new float[TelemetryFrameV1.SpectrumCapacity];
        for (var index = 0; index < spectrumLeft.Length; index++)
        {
            var position = index / (double)(spectrumLeft.Length - 1);
            spectrumLeft[index] = (float)(-22 - position * 62 + Math.Sin(index * 0.19) * 4);
            spectrumRight[index] = spectrumLeft[index] - (float)(2 * Math.Sin(index * 0.11));
        }
        telemetry = new TelemetryFrameV1
        {
            Sequence = 1,
            PreLeft = preLeft,
            PreRight = preRight,
            PostLeft = postLeft,
            PostRight = postRight,
            PreSpectrumLeftDb = spectrumLeft.Select(value => value - 7).ToArray(),
            PreSpectrumRightDb = spectrumRight.Select(value => value - 5).ToArray(),
            SpectrumLeftDb = spectrumLeft,
            SpectrumRightDb = spectrumRight,
            PeakLeftDbFs = -4.2F,
            PeakRightDbFs = -4.8F,
            RmsLeftDbFs = -13.1F,
            RmsRightDbFs = -13.7F,
            TruePeakDbTp = -1.1F,
            MomentaryLufs = -15.4F,
            LimiterReductionDb = 1.7F,
            Correlation = 0.71F,
        };
        RefreshControls();
        EqGraph.SetData(currentMode, showingLeft, telemetry);
        WaveformView.SetTelemetry(telemetry);
        PhaseView.SetTelemetry(telemetry);
        OutputMeter.SetTelemetry(telemetry);
        UpdateTelemetryLabels(telemetry);
        SetStatus("Deterministic visual QA frame · no audio host or device state was changed.");
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            updatingControls = true;
            try
            {
                await ReloadModesAsync();
                if (ModeList.SelectedItem is ModeListItem initialMode) currentMode = initialMode.Mode;
            }
            finally
            {
                updatingControls = false;
            }
            RefreshControls();
            deviceSession = new DeviceProfileSession(modeLibrary, await modeLibrary.LoadDeviceProfilesAsync());
            engineState = await host.EnsureRunningAsync();
            ApplyEngineState(engineState, rebuildDevices: true);
            await RecallSelectedDeviceAsync();
            telemetryReader = SharedTelemetryReader.TryOpen();
            telemetryTimer.Start();
            SetStatus("Audio Host connected · saved device settings loaded.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
            HeaderStateText.Text = "AUDIO HOST UNAVAILABLE";
            EngineBadgeText.Text = "ENGINE FAULT";
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closeApproved) return;
        var pending = Task.WhenAll(deviceSession?.FlushAsync() ?? Task.CompletedTask, pendingModeSave);
        if (pending.IsCompletedSuccessfully) return;
        e.Cancel = true;
        if (flushingClose) return;
        flushingClose = true;
        try
        {
            await pending;
            closeApproved = true;
            Close();
        }
        catch (Exception exception)
        {
            SetStatus($"Device settings could not be saved: {exception.Message}", isError: true);
        }
        finally
        {
            flushingClose = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        telemetryTimer.Stop();
        telemetryReader?.Dispose();
        applyDebounce?.Cancel();
        applyDebounce?.Dispose();
    }

    private async Task ReloadModesAsync(string? selectId = null)
    {
        var modes = await modeLibrary.LoadModesAsync();
        modeItems.Clear();
        foreach (var mode in modes)
        {
            modeItems.Add(new ModeListItem(mode));
        }
        var selected = modeItems.FirstOrDefault(item => item.Mode.Id == selectId)
            ?? modeItems.FirstOrDefault(item => item.Mode.Id == currentMode?.Id)
            ?? modeItems.FirstOrDefault();
        if (selected is not null) ModeList.SelectedItem = selected;
    }

    private async void OnTelemetryTick(object? sender, EventArgs e)
    {
        telemetryReader ??= SharedTelemetryReader.TryOpen();
        var frame = telemetryReader?.TryRead();
        if (frame is not null && frame.Sequence != lastTelemetrySequence)
        {
            telemetry = frame;
            lastTelemetrySequence = frame.Sequence;
            EqGraph.SetData(currentMode, showingLeft, telemetry);
            WaveformView.SetTelemetry(telemetry);
            PhaseView.SetTelemetry(telemetry);
            OutputMeter.SetTelemetry(telemetry);
            UpdateTelemetryLabels(telemetry);
        }
        if (!pollingState && DateTime.UtcNow >= nextStatePoll)
        {
            pollingState = true;
            nextStatePoll = DateTime.UtcNow.AddSeconds(1);
            try
            {
                var state = await host.GetStateAsync();
                ApplyEngineState(state, rebuildDevices: false);
            }
            catch (Exception exception)
            {
                SetStatus(exception.Message, isError: true);
            }
            finally
            {
                pollingState = false;
            }
        }
    }

    private void UpdateTelemetryLabels(TelemetryFrameV1 frame)
    {
        TruePeakText.Text = $"{frame.TruePeakDbTp:0.0} dBTP";
        LoudnessText.Text = $"{frame.MomentaryLufs:0.0} LUFS";
        ReductionText.Text = $"{frame.LimiterReductionDb:0.0} dB";
        CorrelationText.Text = $"{frame.Correlation:+0.00;-0.00;0.00} correlation";
        if (frame.Correlation < 0)
        {
            CorrelationText.Foreground = new SolidColorBrush(Color.Parse("#E76F78"));
            MonoCompatibilityText.Text = "Negative correlation · mono cancellation risk";
            MonoCompatibilityText.Foreground = new SolidColorBrush(Color.Parse("#E89A9F"));
        }
        else
        {
            CorrelationText.Foreground = new SolidColorBrush(Color.Parse("#65D9CC"));
            MonoCompatibilityText.Text = frame.Correlation < 0.25 ? "Wide field · verify mono compatibility" : "Strong mono compatibility";
            MonoCompatibilityText.Foreground = new SolidColorBrush(Color.Parse("#91A5BD"));
        }
    }

    private void ApplyEngineState(EngineStateV1 state, bool rebuildDevices)
    {
        engineState = state;
        HeaderStateText.Text = state.Status.ToUpperInvariant();
        var routePeriods = state.SourcePeriodFrames > 0 && state.OutputPeriodFrames > 0
            ? $"{state.SourcePeriodFrames}→{state.OutputPeriodFrames} frames"
            : $"{state.PeriodFrames} frames";
        HeaderFormatText.Text = $"{state.SampleRate / 1_000.0:0.#} kHz · {routePeriods} · {(state.AddedLatencyMs > 0 ? $"{state.AddedLatencyMs:0.0} ms" : "no route")}";
        EngineBadgeText.Text = state.Failure is not null ? "ENGINE FAULT" : state.IsDemoSignal ? "DEMO · SILENT" : state.IsProcessing ? "ENGINE LIVE" : "ENGINE READY";
        BypassButton.Content = state.IsBypassed ? "◉  RESUME" : "◉  BYPASS";
        if (FreezeScopeBox.IsChecked != state.TelemetryFrozen)
        {
            updatingControls = true;
            FreezeScopeBox.IsChecked = state.TelemetryFrozen;
            updatingControls = false;
            WaveformView.Frozen = state.TelemetryFrozen;
        }
        BufferHealthText.Text = $"{state.Underruns} / {state.Overruns}";
        RoutePeriodsText.Text = state.SourcePeriodFrames > 0 && state.OutputPeriodFrames > 0
            ? $"{state.SourcePeriodFrames} / {state.OutputPeriodFrames} f"
            : "—";
        LatencyText.Text = $"{state.AddedLatencyMs:0.0} ms";
        if (rebuildDevices)
        {
            var selectedOutputId = (OutputDeviceBox.SelectedItem as DeviceChoice)?.Device.Id ?? state.OutputDeviceId;
            var wasUpdating = updatingControls;
            updatingControls = true;
            try
            {
                sourceDevices.Clear();
                outputDevices.Clear();
                foreach (var device in state.Devices)
                {
                    var choice = new DeviceChoice(device);
                    if (device.IsPulseForgeVirtual) sourceDevices.Add(choice);
                    else outputDevices.Add(choice);
                }
                SourceDeviceBox.SelectedItem = sourceDevices.FirstOrDefault(device => device.Device.Id == state.SourceDeviceId) ?? sourceDevices.FirstOrDefault();
                OutputDeviceBox.SelectedItem = outputDevices.FirstOrDefault(device => device.Device.Id == selectedOutputId)
                    ?? outputDevices.FirstOrDefault(device => device.Device.IsDefault)
                    ?? outputDevices.FirstOrDefault();
            }
            finally
            {
                updatingControls = wasUpdating;
            }
        }
        var virtualSource = sourceDevices.FirstOrDefault()?.Device;
        var hasVirtual = virtualSource is not null;
        var isVerifiedDefault = virtualSource?.IsDefault == true;
        ActivationButton.Content = isVerifiedDefault ? "◆  ACTIVE DEFAULT" : "◇  ACTIVATE";
        RouteButton.IsEnabled = hasVirtual && outputDevices.Count > 0 && (!state.IsProcessing || state.IsDemoSignal);
        RoutingHintText.Text = !hasVirtual
            ? "PulseForge Virtual Output is not installed. System takeover is unavailable; the visual demo does not play audio."
            : isVerifiedDefault
                ? "Verified: PulseForge Virtual Output is the current Windows default render endpoint."
                : "PulseForge Virtual Output is available but is not the current Windows default. Activate it deliberately in Sound settings.";
        if (state.Failure is not null) SetStatus(state.Failure, isError: true);
    }

    private async void OnModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || ModeList.SelectedItem is not ModeListItem item) return;
        currentMode = item.Mode;
        var selectedMode = currentMode;
        RefreshControls();
        try
        {
            var save = SaveSelectedDeviceRecallAsync();
            var apply = host.ApplyModeAsync(selectedMode);
            await Task.WhenAll(save, apply);
            ApplyEngineState(await apply, rebuildDevices: false);
            SetStatus(selectedMode.IsBuiltIn ? "Built-in starting point applied. Duplicate it before editing." : $"Mode applied · {selectedMode.Name}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnDuplicateModeClicked(object? sender, RoutedEventArgs e)
    {
        if (currentMode is null) return;
        try
        {
            var duplicate = ModeLibrary.Duplicate(currentMode);
            pendingModeSave = modeLibrary.SaveModeAsync(duplicate);
            await pendingModeSave;
            await ReloadModesAsync(duplicate.Id);
            SetStatus($"Editable mode created · {duplicate.Name}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnSaveModeClicked(object? sender, RoutedEventArgs e)
    {
        if (currentMode is null) return;
        if (currentMode.IsBuiltIn)
        {
            SetStatus("Built-in modes are immutable. Duplicate this starting point before saving.", isError: true);
            return;
        }
        try
        {
            pendingModeSave = modeLibrary.SaveModeAsync(currentMode);
            await pendingModeSave;
            SetStatus($"Saved atomically · {currentMode.Name}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnImportModeClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PulseForge output mode",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PulseForge mode") { Patterns = ["*.pulseforge-mode"] }],
        });
        if (files.Count == 0) return;
        try
        {
            var imported = await modeLibrary.ImportModeAsync(files[0].Path.LocalPath);
            await ReloadModesAsync(imported.Id);
            SetStatus($"Imported without overwriting existing modes · {imported.Name}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnExportModeClicked(object? sender, RoutedEventArgs e)
    {
        if (currentMode is null) return;
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export portable PulseForge mode",
            SuggestedFileName = SanitizeFileName(currentMode.Name) + ".pulseforge-mode",
            DefaultExtension = "pulseforge-mode",
            FileTypeChoices = [new FilePickerFileType("PulseForge mode") { Patterns = ["*.pulseforge-mode"] }],
        });
        if (destination is null) return;
        try
        {
            await modeLibrary.ExportModeAsync(currentMode, destination.Path.LocalPath);
            SetStatus($"Portable mode exported · {destination.Name}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            updatingControls = true;
            try { await ReloadModesAsync(currentMode?.Id); }
            finally { updatingControls = false; }
            var state = await host.RefreshDevicesAsync();
            ApplyEngineState(state, rebuildDevices: true);
            await RecallSelectedDeviceAsync();
            SetStatus("Modes and audio endpoints refreshed.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private void OnActivationClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true })?.Dispose();
            SetStatus("Windows Sound settings opened. Select PulseForge Virtual Output deliberately; PulseForge does not use undocumented default-device APIs.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnRouteClicked(object? sender, RoutedEventArgs e)
    {
        if (SourceDeviceBox.SelectedItem is not DeviceChoice source || OutputDeviceBox.SelectedItem is not DeviceChoice target)
        {
            SetStatus("A PulseForge virtual source and a distinct physical output are required.", isError: true);
            return;
        }
        var request = new StartBridgeRequestV1
        {
            SourceDeviceId = source.Device.Id,
            TargetDeviceId = target.Device.Id,
            Transport = (OutputTransportV1)Math.Clamp(TransportBox.SelectedIndex, 0, 2),
            BufferProfile = (BufferProfileV1)Math.Clamp(BufferBox.SelectedIndex, 0, 2),
            PreferredSampleRate = 48_000,
        };
        try
        {
            var state = await host.StartBridgeAsync(request);
            ApplyEngineState(state, rebuildDevices: false);
            await SaveDeviceRecallAsync(
                target.Device,
                request.Transport,
                request.BufferProfile,
                request.PreferredSampleRate,
                state.NegotiatedFormat);
            SetStatus($"Live route started · {target.Device.Name}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnStopRouteClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var state = await host.StopBridgeAsync();
            ApplyEngineState(state, rebuildDevices: false);
            SetStatus("Route stopped. Output remains silent until demo visualization or a valid route is selected.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnBypassClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var state = await host.SetBypassAsync(!(engineState?.IsBypassed ?? false));
            ApplyEngineState(state, rebuildDevices: false);
            SetStatus(state.IsBypassed ? "Unity bypass active." : "DSP processing resumed.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnDemoClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var state = await host.SetDemoAsync(!(engineState?.IsDemoSignal ?? false));
            ApplyEngineState(state, rebuildDevices: false);
            SetStatus(state.IsDemoSignal ? "Visual demonstration active · no audio is played." : "Visual demonstration stopped.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void OnOutputDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || OutputDeviceBox is null || OutputDeviceBox.SelectedItem is not DeviceChoice choice) return;
        try
        {
            await RecallSelectedDeviceAsync();
            await SaveSelectedDeviceRecallAsync();
            SetStatus($"Device settings ready · {choice.Device.Name}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async Task RecallSelectedDeviceAsync()
    {
        if (OutputDeviceBox.SelectedItem is not DeviceChoice choice) return;
        var profile = DeviceProfileResolver.Resolve(
            choice.Device,
            DeviceProfiles,
            outputDevices.Select(item => item.Device).ToArray());
        if (profile is null) return;
        updatingControls = true;
        try
        {
            TransportBox.SelectedIndex = (int)profile.Transport;
            BufferBox.SelectedIndex = (int)profile.BufferProfile;
            var assigned = modeItems.FirstOrDefault(item => item.Mode.Id == profile.AssignedModeId);
            if (assigned is not null) ModeList.SelectedItem = assigned;
        }
        finally
        {
            updatingControls = false;
        }
        if (profile.AssignedModeId is not null)
        {
            var assigned = modeItems.FirstOrDefault(item => item.Mode.Id == profile.AssignedModeId);
            if (assigned is not null)
            {
                currentMode = assigned.Mode;
                RefreshControls();
                var applied = await host.ApplyModeAsync(currentMode);
                ApplyEngineState(applied, rebuildDevices: false);
            }
        }
        if (engineState?.IsProcessing != true || engineState.IsDemoSignal)
        {
            var selected = await host.SelectOutputAsync(new SelectOutputRequestV1
            {
                TargetDeviceId = choice.Device.Id,
                Transport = profile.Transport,
                BufferProfile = profile.BufferProfile,
                PreferredSampleRate = profile.PreferredSampleRate,
            });
            ApplyEngineState(selected, rebuildDevices: false);
        }
        SetStatus($"Recalled device profile · {choice.Device.Name}");
    }

    private async void OnDevicePolicyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || OutputDeviceBox is null || OutputDeviceBox.SelectedItem is not DeviceChoice choice) return;
        try
        {
            await SaveSelectedDeviceRecallAsync();
            SetStatus($"Saved device policy · {choice.Device.Name}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private Task SaveSelectedDeviceRecallAsync()
    {
        if (OutputDeviceBox.SelectedItem is not DeviceChoice target) return Task.CompletedTask;
        var existing = DeviceProfileResolver.Resolve(
            target.Device,
            DeviceProfiles,
            outputDevices.Select(item => item.Device).ToArray());
        var preferredSampleRate = existing?.PreferredSampleRate ?? 48_000;
        var supportedFormat = engineState?.IsProcessing == true &&
            string.Equals(engineState.OutputDeviceId, target.Device.Id, StringComparison.OrdinalIgnoreCase)
                ? engineState.NegotiatedFormat
                : existing?.SupportedFormat ?? new AudioFormatV1 { SampleRate = preferredSampleRate };
        return SaveDeviceRecallAsync(
            target.Device,
            (OutputTransportV1)Math.Clamp(TransportBox.SelectedIndex, 0, 2),
            (BufferProfileV1)Math.Clamp(BufferBox.SelectedIndex, 0, 2),
            preferredSampleRate,
            supportedFormat);
    }

    private Task SaveDeviceRecallAsync(
        AudioDeviceV1 device,
        OutputTransportV1 transport,
        BufferProfileV1 bufferProfile,
        int preferredSampleRate,
        AudioFormatV1 supportedFormat)
    {
        if (deviceSession is null) return Task.CompletedTask;
        return deviceSession.SaveAsync(
            device,
            outputDevices.Select(item => item.Device).ToArray(),
            transport,
            bufferProfile,
            preferredSampleRate,
            supportedFormat,
            currentMode?.Id);
    }

    private void OnLeftChannelClicked(object? sender, RoutedEventArgs e)
    {
        showingLeft = true;
        RefreshControls();
    }

    private void OnRightChannelClicked(object? sender, RoutedEventArgs e)
    {
        showingLeft = false;
        RefreshControls();
    }

    private void OnLinkChannelsChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        var linked = LinkChannelsBox.IsChecked == true;
        if (linked)
        {
            currentMode = showingLeft
                ? currentMode with { Right = CopyChannel(currentMode.Left, currentMode.Right), Stereo = currentMode.Stereo with { ChannelsLinked = true } }
                : currentMode with { Left = CopyChannel(currentMode.Right, currentMode.Left), Stereo = currentMode.Stereo with { ChannelsLinked = true } };
            SetStatus(showingLeft ? "Linked edits · copied Left settings to Right." : "Linked edits · copied Right settings to Left.");
        }
        else currentMode = currentMode with { Stereo = currentMode.Stereo with { ChannelsLinked = false } };
        UpdateCurrentItem();
        RefreshControls();
        QueueApply();
    }

    private void OnGraphSelectedBandChanged(object? sender, int index)
    {
        EqGraph.SelectedBand = index;
        RefreshBandControls();
    }

    private void OnGraphFilterEdited(object? sender, FilterGraphEditEventArgs e)
    {
        if (currentMode is null || !EnsureEditable()) return;
        UpdateFilter(e.Index, filter => filter with { FrequencyHz = e.FrequencyHz, GainDb = e.GainDb });
    }

    private void OnBandEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        UpdateFilter(EqGraph.SelectedBand, filter => filter with { Enabled = BandEnabledBox.IsChecked == true });
    }

    private void OnBandShapeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || currentMode is null || BandShapeBox.SelectedIndex < 0 || !EnsureEditable()) return;
        UpdateFilter(EqGraph.SelectedBand, filter => filter with { Shape = (FilterShapeV1)BandShapeBox.SelectedIndex });
    }

    private void OnBandNumericCommitted(object? sender, RoutedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        if (!TryDouble(BandFrequencyBox.Text, out var frequency) || !TryDouble(BandGainBox.Text, out var gain) || !TryDouble(BandQBox.Text, out var q))
        {
            SetStatus("Frequency, gain, and Q require finite numeric values.", isError: true);
            RefreshBandControls();
            return;
        }
        UpdateFilter(EqGraph.SelectedBand, filter => filter with
        {
            FrequencyHz = Math.Clamp(frequency, 10, 20_000),
            GainDb = Math.Clamp(gain, -24, 18),
            Q = Math.Clamp(q, 0.1, 24),
        });
    }

    private void UpdateFilter(int index, Func<ParametricFilterV1, ParametricFilterV1> transform)
    {
        if (currentMode is null) return;
        var sourceChannel = showingLeft ? currentMode.Left : currentMode.Right;
        if (index < 0 || index >= sourceChannel.ParametricFilters.Count) return;
        var editedChannel = ReplaceFilter(sourceChannel, index, transform(sourceChannel.ParametricFilters[index]));
        if (showingLeft)
        {
            var right = currentMode.Stereo.ChannelsLinked
                ? ReplaceFilter(currentMode.Right, index, CopyFilter(editedChannel.ParametricFilters[index], currentMode.Right.ParametricFilters[index].Id))
                : currentMode.Right;
            currentMode = currentMode with { Left = editedChannel, Right = right };
        }
        else
        {
            var left = currentMode.Stereo.ChannelsLinked
                ? ReplaceFilter(currentMode.Left, index, CopyFilter(editedChannel.ParametricFilters[index], currentMode.Left.ParametricFilters[index].Id))
                : currentMode.Left;
            currentMode = currentMode with { Right = editedChannel, Left = left };
        }
        UpdateCurrentItem();
        RefreshBandControls();
        EqGraph.SetData(currentMode, showingLeft, telemetry);
        QueueApply();
    }

    private void OnPassFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        var channel = showingLeft ? currentMode.Left : currentMode.Right;
        if (!TryDouble(HighPassFrequencyBox.Text, out var high) || !TryDouble(LowPassFrequencyBox.Text, out var low))
        {
            RefreshPassControls();
            return;
        }
        var edited = channel with
        {
            HighPass = new PassFilterV1 { Enabled = HighPassEnabledBox.IsChecked == true, FrequencyHz = Math.Clamp(high, 10, 20_000), Slope = SlopeFromIndex(HighPassSlopeBox.SelectedIndex) },
            LowPass = new PassFilterV1 { Enabled = LowPassEnabledBox.IsChecked == true, FrequencyHz = Math.Clamp(low, 10, 20_000), Slope = SlopeFromIndex(LowPassSlopeBox.SelectedIndex) },
        };
        ApplyChannelEdit(edited);
    }

    private void OnDampingChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        if (!TryDouble(DampingMinimumBox.Text, out var minimum) || !TryDouble(DampingMaximumBox.Text, out var maximum) ||
            !TryDouble(DampingAttackBox.Text, out var attack) || !TryDouble(DampingReleaseBox.Text, out var release)) return;
        minimum = Math.Clamp(minimum, 20, 19_000);
        maximum = Math.Clamp(maximum, minimum + 10, 20_000);
        currentMode = currentMode with
        {
            Damping = currentMode.Damping with
            {
                Enabled = DampingEnabledBox.IsChecked == true,
                StereoLinked = DampingLinkBox.IsChecked == true,
                SensitivityDb = DampingSensitivitySlider.Value,
                MaximumReductionDb = DampingReductionSlider.Value,
                MinimumFrequencyHz = minimum,
                MaximumFrequencyHz = maximum,
                AttackMs = Math.Clamp(attack, 1, 100),
                ReleaseMs = Math.Clamp(release, 20, 1_000),
            },
        };
        DampingSensitivityText.Text = $"{currentMode.Damping.SensitivityDb:0.0} dB";
        DampingReductionText.Text = $"{currentMode.Damping.MaximumReductionDb:0.0} dB";
        UpdateCurrentItem();
        QueueApply();
    }

    private async void OnLimiterChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        var requestedEnabled = LimiterEnabledBox.IsChecked == true;
        var expertConfirmed = false;
        if (!requestedEnabled && currentMode.MasterGainDb > 0)
        {
            expertConfirmed = await new SafetyConfirmationDialog().ShowDialog<bool>(this);
            if (!expertConfirmed)
            {
                updatingControls = true;
                LimiterEnabledBox.IsChecked = true;
                updatingControls = false;
                return;
            }
        }
        currentMode = currentMode with
        {
            Limiter = currentMode.Limiter with
            {
                Enabled = requestedEnabled,
                ExpertBypassConfirmed = expertConfirmed,
                CeilingDbTp = LimiterCeilingSlider.Value,
                ReleaseMs = LimiterReleaseSlider.Value,
            },
        };
        LimiterCeilingText.Text = $"{currentMode.Limiter.CeilingDbTp:0.0} dBTP";
        LimiterReleaseText.Text = $"{currentMode.Limiter.ReleaseMs:0} ms";
        RefreshBoostWarning();
        UpdateCurrentItem();
        QueueApply();
    }

    private void OnMasterGainChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        var gain = MasterGainSlider.Value;
        var limiter = gain > 0
            ? currentMode.Limiter with { Enabled = true, ExpertBypassConfirmed = false }
            : currentMode.Limiter;
        currentMode = currentMode with { MasterGainDb = gain, Limiter = limiter };
        if (gain > 0)
        {
            updatingControls = true;
            LimiterEnabledBox.IsChecked = true;
            updatingControls = false;
        }
        MasterGainText.Text = $"{gain:+0.0;-0.0;0.0} dB";
        RefreshBoostWarning();
        UpdateCurrentItem();
        QueueApply();
    }

    private void OnChannelUtilityChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        if (!TryDouble(LeftDelayBox.Text, out var leftDelay) || !TryDouble(RightDelayBox.Text, out var rightDelay)) return;
        currentMode = currentMode with
        {
            Left = currentMode.Left with
            {
                InputTrimDb = LeftInputSlider.Value,
                OutputTrimDb = LeftOutputSlider.Value,
                DelayMs = Math.Clamp(leftDelay, 0, 20),
                Mute = LeftMuteBox.IsChecked == true,
                Solo = LeftSoloBox.IsChecked == true,
                PolarityInverted = LeftPolarityBox.IsChecked == true,
            },
            Right = currentMode.Right with
            {
                InputTrimDb = RightInputSlider.Value,
                OutputTrimDb = RightOutputSlider.Value,
                DelayMs = Math.Clamp(rightDelay, 0, 20),
                Mute = RightMuteBox.IsChecked == true,
                Solo = RightSoloBox.IsChecked == true,
                PolarityInverted = RightPolarityBox.IsChecked == true,
            },
        };
        UpdateChannelLabels();
        UpdateCurrentItem();
        QueueApply();
    }

    private void OnStereoChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingControls || currentMode is null || !EnsureEditable()) return;
        currentMode = currentMode with
        {
            Stereo = currentMode.Stereo with
            {
                Balance = BalanceSlider.Value,
                WidthPercent = WidthSlider.Value,
                MonoAudition = MonoAuditionBox.IsChecked == true,
            },
        };
        BalanceText.Text = $"{currentMode.Stereo.Balance:+0.00;-0.00;CENTER}";
        WidthText.Text = $"{currentMode.Stereo.WidthPercent:0}%";
        UpdateCurrentItem();
        QueueApply();
    }

    private async void OnFreezeChanged(object? sender, RoutedEventArgs e)
    {
        if (updatingControls) return;
        var frozen = FreezeScopeBox.IsChecked == true;
        WaveformView.Frozen = frozen;
        try
        {
            var state = await host.SetTelemetryFrozenAsync(frozen);
            ApplyEngineState(state, rebuildDevices: false);
            SetStatus(frozen ? "Telemetry frozen at the current shared-memory frame." : "Live telemetry resumed.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private void OnScopeViewChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (WaveformView is null || ScopeTimebaseSlider is null || ScopeGainSlider is null) return;
        WaveformView.HorizontalZoom = ScopeTimebaseSlider.Value;
        WaveformView.VerticalGain = ScopeGainSlider.Value;
    }

    private async void OnResetTelemetryClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var state = await host.ResetTelemetryAsync();
            ApplyEngineState(state, rebuildDevices: false);
            SetStatus("DSP state and telemetry history reset without changing the active mode.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private void ApplyChannelEdit(ChannelChainV1 edited)
    {
        if (currentMode is null) return;
        if (showingLeft)
        {
            currentMode = currentMode with { Left = edited, Right = currentMode.Stereo.ChannelsLinked ? CopyChannel(edited, currentMode.Right) : currentMode.Right };
        }
        else
        {
            currentMode = currentMode with { Right = edited, Left = currentMode.Stereo.ChannelsLinked ? CopyChannel(edited, currentMode.Left) : currentMode.Left };
        }
        UpdateCurrentItem();
        RefreshPassControls();
        EqGraph.SetData(currentMode, showingLeft, telemetry);
        QueueApply();
    }

    private void RefreshControls()
    {
        if (currentMode is null) return;
        updatingControls = true;
        try
        {
            LeftChannelButton.Classes.Set("accent", showingLeft);
            RightChannelButton.Classes.Set("accent", !showingLeft);
            LinkChannelsBox.Content = showingLeft ? "LINK L→R" : "LINK R→L";
            LinkChannelsBox.IsChecked = currentMode.Stereo.ChannelsLinked;
            RefreshBandControls();
            RefreshPassControls();
            DampingEnabledBox.IsChecked = currentMode.Damping.Enabled;
            DampingLinkBox.IsChecked = currentMode.Damping.StereoLinked;
            DampingSensitivitySlider.Value = currentMode.Damping.SensitivityDb;
            DampingReductionSlider.Value = currentMode.Damping.MaximumReductionDb;
            DampingSensitivityText.Text = $"{currentMode.Damping.SensitivityDb:0.0} dB";
            DampingReductionText.Text = $"{currentMode.Damping.MaximumReductionDb:0.0} dB";
            DampingMinimumBox.Text = currentMode.Damping.MinimumFrequencyHz.ToString("0", CultureInfo.InvariantCulture);
            DampingMaximumBox.Text = currentMode.Damping.MaximumFrequencyHz.ToString("0", CultureInfo.InvariantCulture);
            DampingAttackBox.Text = currentMode.Damping.AttackMs.ToString("0.0", CultureInfo.InvariantCulture);
            DampingReleaseBox.Text = currentMode.Damping.ReleaseMs.ToString("0", CultureInfo.InvariantCulture);
            LimiterEnabledBox.IsChecked = currentMode.Limiter.Enabled;
            LimiterCeilingSlider.Value = currentMode.Limiter.CeilingDbTp;
            LimiterReleaseSlider.Value = currentMode.Limiter.ReleaseMs;
            LimiterCeilingText.Text = $"{currentMode.Limiter.CeilingDbTp:0.0} dBTP";
            LimiterReleaseText.Text = $"{currentMode.Limiter.ReleaseMs:0} ms";
            MasterGainSlider.Value = currentMode.MasterGainDb;
            MasterGainText.Text = $"{currentMode.MasterGainDb:+0.0;-0.0;0.0} dB";
            RefreshBoostWarning();
            LeftInputSlider.Value = currentMode.Left.InputTrimDb;
            LeftOutputSlider.Value = currentMode.Left.OutputTrimDb;
            LeftDelayBox.Text = currentMode.Left.DelayMs.ToString("0.00", CultureInfo.InvariantCulture);
            LeftMuteBox.IsChecked = currentMode.Left.Mute;
            LeftSoloBox.IsChecked = currentMode.Left.Solo;
            LeftPolarityBox.IsChecked = currentMode.Left.PolarityInverted;
            RightInputSlider.Value = currentMode.Right.InputTrimDb;
            RightOutputSlider.Value = currentMode.Right.OutputTrimDb;
            RightDelayBox.Text = currentMode.Right.DelayMs.ToString("0.00", CultureInfo.InvariantCulture);
            RightMuteBox.IsChecked = currentMode.Right.Mute;
            RightSoloBox.IsChecked = currentMode.Right.Solo;
            RightPolarityBox.IsChecked = currentMode.Right.PolarityInverted;
            BalanceSlider.Value = currentMode.Stereo.Balance;
            WidthSlider.Value = currentMode.Stereo.WidthPercent;
            MonoAuditionBox.IsChecked = currentMode.Stereo.MonoAudition;
            BalanceText.Text = $"{currentMode.Stereo.Balance:+0.00;-0.00;CENTER}";
            WidthText.Text = $"{currentMode.Stereo.WidthPercent:0}%";
            UpdateChannelLabels();
            EqGraph.SetData(currentMode, showingLeft, telemetry);
        }
        finally
        {
            updatingControls = false;
        }
    }

    private void RefreshBandControls()
    {
        if (currentMode is null) return;
        var channel = showingLeft ? currentMode.Left : currentMode.Right;
        var index = Math.Clamp(EqGraph.SelectedBand, 0, Math.Max(0, channel.ParametricFilters.Count - 1));
        if (channel.ParametricFilters.Count == 0) return;
        var filter = channel.ParametricFilters[index];
        SelectedBandTitle.Text = $"BAND {index + 1:00} · {(showingLeft ? "LEFT" : "RIGHT")}";
        BandEnabledBox.IsChecked = filter.Enabled;
        BandShapeBox.SelectedIndex = (int)filter.Shape;
        BandFrequencyBox.Text = filter.FrequencyHz.ToString("0.##", CultureInfo.InvariantCulture);
        BandGainBox.Text = filter.GainDb.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        BandQBox.Text = filter.Q.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void RefreshPassControls()
    {
        if (currentMode is null) return;
        var channel = showingLeft ? currentMode.Left : currentMode.Right;
        HighPassEnabledBox.IsChecked = channel.HighPass.Enabled;
        HighPassFrequencyBox.Text = channel.HighPass.FrequencyHz.ToString("0.##", CultureInfo.InvariantCulture);
        HighPassSlopeBox.SelectedIndex = SlopeToIndex(channel.HighPass.Slope);
        LowPassEnabledBox.IsChecked = channel.LowPass.Enabled;
        LowPassFrequencyBox.Text = channel.LowPass.FrequencyHz.ToString("0.##", CultureInfo.InvariantCulture);
        LowPassSlopeBox.SelectedIndex = SlopeToIndex(channel.LowPass.Slope);
    }

    private void UpdateChannelLabels()
    {
        if (currentMode is null) return;
        LeftInputText.Text = $"{currentMode.Left.InputTrimDb:+0.0;-0.0;0.0}";
        LeftOutputText.Text = $"{currentMode.Left.OutputTrimDb:+0.0;-0.0;0.0}";
        RightInputText.Text = $"{currentMode.Right.InputTrimDb:+0.0;-0.0;0.0}";
        RightOutputText.Text = $"{currentMode.Right.OutputTrimDb:+0.0;-0.0;0.0}";
    }

    private void RefreshBoostWarning()
    {
        if (currentMode is null) return;
        var boosted = currentMode.MasterGainDb > 0;
        var unguarded = boosted && !currentMode.Limiter.Enabled;
        BoostWarningBorder.IsVisible = boosted;
        BoostWarningBorder.Background = new SolidColorBrush(Color.Parse(unguarded ? "#421E25" : "#382A16"));
        BoostWarningBorder.BorderBrush = new SolidColorBrush(Color.Parse(unguarded ? "#B94C58" : "#8E6A2A"));
        BoostWarningText.Foreground = new SolidColorBrush(Color.Parse(unguarded ? "#FFD2D6" : "#F2C76D"));
        BoostWarningText.Text = unguarded
            ? "UNGUARDED BOOST · CLIPPING + HEARING RISK · limiter expert bypass is persistent"
            : "BOOST ACTIVE · LIMITER AUTO-ARMED · verify listening level before playback";
    }

    private bool EnsureEditable()
    {
        if (currentMode is null) return false;
        if (!currentMode.IsBuiltIn) return true;
        SetStatus("Built-in modes are immutable. Press Duplicate to create an editable mode.", isError: true);
        RefreshControls();
        return false;
    }

    private void UpdateCurrentItem()
    {
        if (currentMode is null || ModeList.SelectedItem is not ModeListItem item) return;
        item.Mode = currentMode;
    }

    private void QueueApply()
    {
        if (currentMode is null) return;
        applyDebounce?.Cancel();
        applyDebounce?.Dispose();
        var cancellation = new CancellationTokenSource();
        applyDebounce = cancellation;
        var mode = currentMode;
        _ = ApplyAfterDelayAsync(mode, cancellation.Token);
    }

    private async Task ApplyAfterDelayAsync(OutputModeV1 mode, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(45, cancellationToken);
            var state = await host.ApplyModeAsync(mode, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyEngineState(state, rebuildDevices: false));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetStatus(exception.Message, isError: true));
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(isError ? "#E89A9F" : "#89A0B8"));
    }

    private static ChannelChainV1 ReplaceFilter(ChannelChainV1 channel, int index, ParametricFilterV1 replacement)
    {
        var filters = channel.ParametricFilters.ToArray();
        filters[index] = replacement;
        return channel with { ParametricFilters = filters };
    }

    private static ParametricFilterV1 CopyFilter(ParametricFilterV1 source, string targetId) => source with { Id = targetId };

    private static ChannelChainV1 CopyChannel(ChannelChainV1 source, ChannelChainV1 target)
    {
        var filters = source.ParametricFilters.Select((filter, index) =>
            CopyFilter(filter, index < target.ParametricFilters.Count ? target.ParametricFilters[index].Id : $"C{index + 1:00}")).ToArray();
        return source with { ParametricFilters = filters };
    }

    private static FilterSlopeV1 SlopeFromIndex(int index) => index switch
    {
        0 => FilterSlopeV1.Db12,
        2 => FilterSlopeV1.Db36,
        3 => FilterSlopeV1.Db48,
        _ => FilterSlopeV1.Db24,
    };

    private static int SlopeToIndex(FilterSlopeV1 slope) => slope switch
    {
        FilterSlopeV1.Db12 => 0,
        FilterSlopeV1.Db36 => 2,
        FilterSlopeV1.Db48 => 3,
        _ => 1,
    };

    private static bool TryDouble(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private sealed class ModeListItem(OutputModeV1 mode)
    {
        public OutputModeV1 Mode { get; set; } = mode;
        public string Name => Mode.Name;
        public string Description => Mode.IsBuiltIn ? "BUILT-IN · " + Mode.Description : Mode.Description;
        public IBrush Accent { get; } = new SolidColorBrush(Color.Parse(mode.IsBuiltIn ? "#446B70" : "#64D8CB"));
    }

    private sealed class DeviceChoice(AudioDeviceV1 device)
    {
        public AudioDeviceV1 Device { get; } = device;
        public override string ToString() => Device.IsDefault ? $"{Device.Name}  · DEFAULT" : Device.Name;
    }
}
