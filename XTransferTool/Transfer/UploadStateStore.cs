using System;
using System.Collections.Concurrent;
using System.IO;

namespace XTransferTool.Transfer;

public sealed class UploadStateStore
{
    private readonly ConcurrentDictionary<(string TransferId, string ItemId), UploadItemState> _items = new();

    public UploadItemState GetOrCreate(string transferId, string itemId, Func<UploadItemState> factory)
        => _items.GetOrAdd((transferId, itemId), _ => factory());

    public bool TryGet(string transferId, string itemId, out UploadItemState state)
        => _items.TryGetValue((transferId, itemId), out state!);

    public string? TryGetPath(string transferId, string itemId)
        => _items.TryGetValue((transferId, itemId), out var state) ? state.Path : null;

    public void Remove(string transferId, string itemId)
    {
        if (_items.TryRemove((transferId, itemId), out var state))
            state.Dispose();
    }
}

public sealed class UploadItemState : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _stream;

    public UploadItemState(string path)
    {
        Path = path;
    }

    public string Path { get; }
    public long AcceptedBytes { get; private set; }

    public void EnsureOpen()
    {
        lock (_gate)
        {
            if (_stream is not null)
                return;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            _stream = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
            _stream.Seek(AcceptedBytes, SeekOrigin.Begin);
        }
    }

    public long ExpectedOffset()
    {
        lock (_gate) return AcceptedBytes;
    }

    public void Accept(byte[] data, int count)
    {
        lock (_gate)
        {
            if (_stream is null)
                throw new InvalidOperationException("stream not open");

            _stream.Write(data, 0, count);
            AcceptedBytes += count;
        }
    }

    public void Flush()
    {
        lock (_gate)
            _stream?.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}

