using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PeerDM7.Core;

namespace PeerDM7.Net;

/// <summary>
/// One authenticated, encrypted TCP session with another copy of PeerDM7.
///
/// Handshake: both sides send their long-term public key, a fresh ECDH public key and a
/// signature over it. Session keys come from ECDH + HKDF, one key per direction, and every
/// frame after that is AES-GCM with a counter nonce. Peer IDs are hashes of the long-term
/// key, so a peer cannot claim someone else's ID without their private key.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    const int MaxFrame = 2 * 1024 * 1024;
    static readonly byte[] SigContext = "PeerDM7/handshake/1"u8.ToArray();
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);

    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly AesGcm _sendKey;
    readonly AesGcm _recvKey;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    ulong _sendCounter;
    ulong _recvCounter;
    int _closed;

    public string RemoteId { get; }
    /// <summary>ID of whichever side dialled. Used to break ties when both sides dial at once.</summary>
    public string InitiatorId { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public DateTime LastHeard { get; private set; } = DateTime.UtcNow;
    public bool IsClosed => _closed != 0;

    public event Action<PeerConnection, JsonObject>? FrameReceived;
    public event Action<PeerConnection>? Closed;

    PeerConnection(TcpClient tcp, NetworkStream stream, string remoteId, string initiatorId, byte[] sendKey, byte[] recvKey)
    {
        _tcp = tcp;
        _stream = stream;
        RemoteId = remoteId;
        InitiatorId = initiatorId;
        RemoteEndPoint = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        _sendKey = new AesGcm(sendKey, 16);
        _recvKey = new AesGcm(recvKey, 16);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(recvKey);
    }

    public static async Task<PeerConnection> EstablishAsync(TcpClient tcp, Identity me, bool outbound, CancellationToken ct)
    {
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPub = eph.PublicKey.ExportSubjectPublicKeyInfo();
        var sig = me.Key.SignData(Concat(SigContext, ephPub), HashAlgorithmName.SHA256);

        var hello = new JsonObject
        {
            ["app"] = "PeerDM7",
            ["v"] = 1,
            ["idk"] = Convert.ToBase64String(me.PublicKey),
            ["eph"] = Convert.ToBase64String(ephPub),
            ["sig"] = Convert.ToBase64String(sig),
        };
        await WritePlainAsync(stream, Encoding.UTF8.GetBytes(hello.ToJsonString()), timeout.Token);

        var reply = JsonNode.Parse(await ReadPlainAsync(stream, 8192, timeout.Token)) as JsonObject
                    ?? throw new InvalidDataException("bad hello");
        if ((string?)reply["app"] != "PeerDM7") throw new InvalidDataException("not a PeerDM7 peer");

        var remoteIdKey = Convert.FromBase64String((string?)reply["idk"] ?? "");
        var remoteEph = Convert.FromBase64String((string?)reply["eph"] ?? "");
        var remoteSig = Convert.FromBase64String((string?)reply["sig"] ?? "");

        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(remoteIdKey, out _);
            if (!verifier.VerifyData(Concat(SigContext, remoteEph), remoteSig, HashAlgorithmName.SHA256))
                throw new CryptographicException("handshake signature invalid");
        }

        var remoteId = Identity.IdFromPublicKey(remoteIdKey);

        using var remoteEphKey = ECDiffieHellman.Create();
        remoteEphKey.ImportSubjectPublicKeyInfo(remoteEph, out _);
        var secret = eph.DeriveRawSecretAgreement(remoteEphKey.PublicKey);

        // Salt binds the keys to this exact pair of ephemeral keys, in a stable order.
        var salt = ByteOrder(ephPub, remoteEph) <= 0
            ? SHA256.HashData(Concat(ephPub, remoteEph))
            : SHA256.HashData(Concat(remoteEph, ephPub));
        var sendKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.UTF8.GetBytes("PeerDM7/key/" + me.Id));
        var recvKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.UTF8.GetBytes("PeerDM7/key/" + remoteId));
        CryptographicOperations.ZeroMemory(secret);

        return new PeerConnection(tcp, stream, remoteId, outbound ? me.Id : remoteId, sendKey, recvKey);
    }

    public void Start() => _ = ReadLoop(_cts.Token);

    public async Task<bool> SendAsync(JsonObject frame)
    {
        if (IsClosed) return false;
        var plain = Encoding.UTF8.GetBytes(frame.ToJsonString());
        if (plain.Length > MaxFrame - 64) return false;

        await _writeLock.WaitAsync();
        try
        {
            if (IsClosed) return false;
            var nonce = Nonce(++_sendCounter);
            var buf = new byte[4 + 12 + plain.Length + 16];
            BinaryPrimitives.WriteInt32BigEndian(buf, 12 + plain.Length + 16);
            nonce.CopyTo(buf, 4);
            _sendKey.Encrypt(nonce, plain, buf.AsSpan(16, plain.Length), buf.AsSpan(16 + plain.Length, 16));
            await _stream.WriteAsync(buf, _cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Close();
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task ReadLoop(CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, ct);
                var len = BinaryPrimitives.ReadInt32BigEndian(header);
                if (len < 28 || len > MaxFrame) throw new InvalidDataException("frame size");

                var body = new byte[len];
                await _stream.ReadExactlyAsync(body, ct);

                var nonce = body.AsSpan(0, 12);
                var counter = BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]);
                if (counter <= _recvCounter) throw new CryptographicException("replayed frame");
                _recvCounter = counter;

                var plain = new byte[len - 28];
                _recvKey.Decrypt(nonce, body.AsSpan(12, plain.Length), body.AsSpan(12 + plain.Length, 16), plain);
                LastHeard = DateTime.UtcNow;

                if (JsonNode.Parse(plain) is JsonObject obj)
                    FrameReceived?.Invoke(this, obj);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsClosed && ex is not (EndOfStreamException or IOException or OperationCanceledException or ObjectDisposedException))
                Log.Warn($"Session with {RemoteId} dropped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        try { _tcp.Close(); } catch { }
        Closed?.Invoke(this);
    }

    public void Dispose()
    {
        Close();
        _sendKey.Dispose();
        _recvKey.Dispose();
    }

    static byte[] Nonce(ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    static async Task WritePlainAsync(Stream s, byte[] payload, CancellationToken ct)
    {
        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, payload.Length);
        payload.CopyTo(buf, 4);
        await s.WriteAsync(buf, ct);
    }

    static async Task<byte[]> ReadPlainAsync(Stream s, int max, CancellationToken ct)
    {
        var header = new byte[4];
        await s.ReadExactlyAsync(header, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len <= 0 || len > max) throw new InvalidDataException("handshake size");
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct);
        return body;
    }

    static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    static int ByteOrder(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);
}
