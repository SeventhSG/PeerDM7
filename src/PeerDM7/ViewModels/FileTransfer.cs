using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PeerDM7.ViewModels;

public enum TransferState
{
    Preparing,
    Offered,
    Waiting,
    Transferring,
    Verifying,
    Done,
    Declined,
    Cancelled,
    Failed,
}

/// <summary>
/// One file going to or coming from one person. The sender offers it with its size and SHA-256,
/// the receiver accepts, chunks flow paced by the transport's in-flight count, and the receiver
/// checks the hash before the file appears in Downloads.
/// </summary>
public sealed class FileTransfer : ObservableObject
{
    public const int ChunkSize = 16 * 1024;
    public const long MaxSize = 2L * 1024 * 1024 * 1024;
    const int Window = 24;

    readonly MainViewModel _main;
    readonly object _io = new();
    FileStream? _stream;
    bool[]? _got;
    int _gotCount;
    CancellationTokenSource? _sendCts;

    public FileTransfer(MainViewModel main, string id, string peerId, string peerName, string name, long size, bool incoming)
    {
        _main = main;
        Id = id;
        PeerId = peerId;
        PeerName = peerName;
        Name = name;
        Size = size;
        Incoming = incoming;
        AcceptCommand = new RelayCommand(Accept, _ => State == TransferState.Offered);
        DeclineCommand = new RelayCommand(Decline, _ => State == TransferState.Offered);
        CancelCommand = new RelayCommand(() => Cancel(notify: true), _ => State is TransferState.Waiting or TransferState.Transferring or TransferState.Preparing);
        OpenCommand = new RelayCommand(Open, _ => State == TransferState.Done && Incoming);
        ShowCommand = new RelayCommand(ShowInFolder, _ => State == TransferState.Done && Incoming);
    }

    public string Id { get; }
    public string PeerId { get; }
    public string PeerName { get; }
    public string Name { get; }
    public long Size { get; }
    public bool Incoming { get; }
    public string? SourcePath { get; init; }
    public string? Hash { get; set; }
    public string? SavedPath { get; private set; }
    public int Chunks => (int)Math.Max(1, (Size + ChunkSize - 1) / ChunkSize);

    public RelayCommand AcceptCommand { get; }
    public RelayCommand DeclineCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand ShowCommand { get; }

    TransferState _state;
    public TransferState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(IsOffered));
            Raise(nameof(IsActive));
            Raise(nameof(IsDone));
            foreach (var c in new[] { AcceptCommand, DeclineCommand, CancelCommand, OpenCommand, ShowCommand }) c.Refresh();
        }
    }

    double _progress;
    public double Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(StatusText)); } }

    public bool IsOffered => State == TransferState.Offered && Incoming;
    public bool IsActive => State is TransferState.Waiting or TransferState.Transferring or TransferState.Preparing or TransferState.Verifying;
    public bool IsDone => State == TransferState.Done && Incoming;
    public string SizeText => FormatSize(Size);

    public string StatusText => State switch
    {
        TransferState.Preparing => "Getting the file ready...",
        TransferState.Offered => Incoming ? $"{PeerName} wants to send you this file." : "",
        TransferState.Waiting => $"Waiting for {PeerName} to accept.",
        TransferState.Transferring => $"{(Incoming ? "Receiving" : "Sending")}... {Progress:P0}",
        TransferState.Verifying => "Checking the file...",
        TransferState.Done => Incoming ? "Saved to your Downloads folder." : $"{PeerName} received the file.",
        TransferState.Declined => Incoming ? "You declined this file." : $"{PeerName} declined the file.",
        TransferState.Cancelled => "The transfer was cancelled.",
        _ => _failure ?? "The transfer failed.",
    };

    string? _failure;
    public void Fail(string reason)
    {
        _failure = reason;
        Cleanup(deletePartial: true);
        State = TransferState.Failed;
    }

    // ---- receiving ----

    void Accept()
    {
        try
        {
            var folder = DownloadsFolder();
            Directory.CreateDirectory(folder);
            SavedPath = UniquePath(folder, Name);
            _stream = new FileStream(SavedPath + ".part", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            _stream.SetLength(Size);
            _got = new bool[Chunks];
            State = TransferState.Transferring;
            _ = _main.SendFileFrame(PeerId, new JsonObject { ["t"] = "fileaccept", ["fid"] = Id });
            if (Size == 0) _ = Finish();
        }
        catch (Exception ex)
        {
            Fail("Could not save to Downloads: " + ex.Message);
            _ = _main.SendFileFrame(PeerId, new JsonObject { ["t"] = "filecancel", ["fid"] = Id });
        }
    }

    void Decline()
    {
        State = TransferState.Declined;
        _ = _main.SendFileFrame(PeerId, new JsonObject { ["t"] = "filedecline", ["fid"] = Id });
    }

    /// <summary>Runs on the network thread so big files do not flood the UI.</summary>
    public void OnChunk(int index, byte[] data)
    {
        bool complete;
        lock (_io)
        {
            if (_stream is null || _got is null || index < 0 || index >= _got.Length || _got[index]) return;
            var expected = index == _got.Length - 1 ? Size - (long)index * ChunkSize : ChunkSize;
            if (data.Length != expected) return;
            _stream.Position = (long)index * ChunkSize;
            _stream.Write(data);
            _got[index] = true;
            _gotCount++;
            complete = _gotCount == _got.Length;
        }
        var pct = (double)_gotCount / Chunks;
        if (complete || _gotCount % 16 == 0) _main.OnUi(() => Progress = pct);
        if (complete) _ = Finish();
    }

    async Task Finish()
    {
        _main.OnUi(() => State = TransferState.Verifying);
        string actual;
        lock (_io)
        {
            if (_stream is null) return;
            _stream.Flush();
            _stream.Position = 0;
        }
        actual = Convert.ToHexString(await SHA256.HashDataAsync(_stream));
        lock (_io)
        {
            _stream.Dispose();
            _stream = null;
        }

        if (!string.Equals(actual, Hash, StringComparison.OrdinalIgnoreCase))
        {
            _main.OnUi(() => Fail("The file arrived damaged and was deleted. Ask them to send it again."));
            return;
        }
        File.Move(SavedPath + ".part", SavedPath!, overwrite: false);
        _ = _main.SendFileFrame(PeerId, new JsonObject { ["t"] = "filegot", ["fid"] = Id });
        _main.OnUi(() => { Progress = 1; State = TransferState.Done; });
    }

    // ---- sending ----

    public void StartSending()
    {
        if (SourcePath is null) return;
        State = TransferState.Transferring;
        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var fs = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[ChunkSize];
                for (var i = 0; i < Chunks; i++)
                {
                    while (_main.InFlight(PeerId) > Window)
                    {
                        if (!_main.IsConnected(PeerId)) throw new IOException($"{PeerName} went offline.");
                        await Task.Delay(4, ct);
                    }
                    var n = await fs.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct);
                    var frame = new JsonObject
                    {
                        ["t"] = "filechunk",
                        ["fid"] = Id,
                        ["i"] = i,
                        ["d"] = Convert.ToBase64String(buf, 0, n),
                    };
                    if (!await _main.SendFileFrame(PeerId, frame)) throw new IOException($"{PeerName} went offline.");
                    if (i % 16 == 0 || i == Chunks - 1)
                    {
                        var pct = (double)(i + 1) / Chunks;
                        _main.OnUi(() => Progress = pct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                var reason = ex.Message;
                _main.OnUi(() => Fail(reason));
                _ = _main.SendFileFrame(PeerId, new JsonObject { ["t"] = "filecancel", ["fid"] = Id });
            }
        }, ct);
    }

    public void Cancel(bool notify)
    {
        if (State is TransferState.Done or TransferState.Failed or TransferState.Declined or TransferState.Cancelled) return;
        _sendCts?.Cancel();
        Cleanup(deletePartial: true);
        State = TransferState.Cancelled;
        if (notify) _ = _main.SendFileFrame(PeerId, new JsonObject { ["t"] = "filecancel", ["fid"] = Id });
    }

    void Cleanup(bool deletePartial)
    {
        lock (_io)
        {
            _stream?.Dispose();
            _stream = null;
        }
        if (deletePartial && SavedPath is not null)
            try { File.Delete(SavedPath + ".part"); } catch { }
    }

    void Open()
    {
        if (SavedPath is null) return;
        try { Process.Start(new ProcessStartInfo(SavedPath) { UseShellExecute = true }); } catch { }
    }

    void ShowInFolder()
    {
        if (SavedPath is null) return;
        try { Process.Start("explorer.exe", $"/select,\"{SavedPath}\""); } catch { }
    }

    // ---- helpers ----

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    public static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "file";
        return name.Length > 120 ? name[..120] : name;
    }

    static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; File.Exists(path) || File.Exists(path + ".part"); i++)
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    static string DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) == 0) return p;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);
}
