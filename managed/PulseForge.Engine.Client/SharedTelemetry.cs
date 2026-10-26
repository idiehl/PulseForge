using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using PulseForge.Contracts;

namespace PulseForge.Engine.Client;

public sealed class SharedTelemetryWriter : IDisposable
{
    private const int Capacity = 256 * 1024;
    private const int HeaderSize = 64;
    private const int SlotSize = (Capacity - HeaderSize) / 2;
    private const int Magic = 0x31544650;
    private readonly MemoryMappedFile file;
    private readonly MemoryMappedViewAccessor accessor;
    private int publishedSlot;

    public SharedTelemetryWriter()
    {
        file = MemoryMappedFile.CreateOrOpen(IpcNames.TelemetryMap, Capacity, MemoryMappedFileAccess.ReadWrite);
        accessor = file.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
        accessor.Write(0, Magic);
        accessor.Write(4, IpcNames.ProtocolVersion);
        accessor.Write(8, 0);
    }

    public void Write(TelemetryFrameV1 frame)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, ContractJson.Options);
        if (payload.Length > SlotSize - 24) throw new InvalidOperationException("Telemetry payload exceeds the bounded shared-memory slot.");
        var target = 1 - publishedSlot;
        var offset = HeaderSize + target * SlotSize;
        var sequence = frame.Sequence * 2 + 2;
        accessor.Write(offset, sequence - 1);
        accessor.Write(offset + 8, payload.Length);
        accessor.WriteArray(offset + 16, payload, 0, payload.Length);
        accessor.Write(offset + SlotSize - 8, sequence);
        accessor.Write(offset, sequence);
        accessor.Flush();
        accessor.Write(8, target);
        publishedSlot = target;
    }

    public void Dispose()
    {
        accessor.Dispose();
        file.Dispose();
    }
}

public sealed class SharedTelemetryReader : IDisposable
{
    private const int Capacity = 256 * 1024;
    private const int HeaderSize = 64;
    private const int SlotSize = (Capacity - HeaderSize) / 2;
    private const int Magic = 0x31544650;
    private readonly MemoryMappedFile file;
    private readonly MemoryMappedViewAccessor accessor;

    private SharedTelemetryReader(MemoryMappedFile file)
    {
        this.file = file;
        accessor = file.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.Read);
    }

    public static SharedTelemetryReader? TryOpen()
    {
        try
        {
            return new SharedTelemetryReader(MemoryMappedFile.OpenExisting(IpcNames.TelemetryMap, MemoryMappedFileRights.Read));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public TelemetryFrameV1? TryRead()
    {
        if (accessor.ReadInt32(0) != Magic || accessor.ReadInt32(4) != IpcNames.ProtocolVersion) return null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var slot = accessor.ReadInt32(8);
            if (slot is < 0 or > 1) return null;
            var offset = HeaderSize + slot * SlotSize;
            var before = accessor.ReadInt64(offset);
            if ((before & 1) != 0 || before == 0) continue;
            var length = accessor.ReadInt32(offset + 8);
            if (length <= 0 || length > SlotSize - 24) return null;
            var payload = new byte[length];
            accessor.ReadArray(offset + 16, payload, 0, length);
            var after = accessor.ReadInt64(offset + SlotSize - 8);
            if (before != after) continue;
            try
            {
                return JsonSerializer.Deserialize<TelemetryFrameV1>(payload, ContractJson.Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    public void Dispose()
    {
        accessor.Dispose();
        file.Dispose();
    }
}
