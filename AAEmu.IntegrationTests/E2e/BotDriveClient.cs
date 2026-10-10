using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// JSON/TCP client for the game server's BotDriveBridge (127.0.0.1:E2EBridgePort).
/// Executes PlayerBotController ops on real networked bot characters — the
/// bridge itself only acts on sessions that entered the world through the real
/// login flow.
/// </summary>
public sealed class BotDriveClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly int _maximumResponseBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public BotDriveClient(int port, int maximumResponseBytes = 0)
    {
        if (maximumResponseBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        _maximumResponseBytes = maximumResponseBytes;
        _tcp = new TcpClient { NoDelay = true };
        _tcp.Connect("127.0.0.1", port);
        _stream = _tcp.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true
        };
    }

    public JsonElement Call(JsonElement request, int timeoutMs = 30000)
        => CallAsync(request, timeoutMs, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Sends one bridge request and waits for its response without blocking a
    /// worker thread. Cancellation closes the read operation at the socket
    /// boundary; callers own the client and can dispose it after cancellation.
    /// </summary>
    public async Task<JsonElement> CallAsync(
        JsonElement request,
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _writer.WriteLine(request.GetRawText());
        _writer.Flush();

        using var timeoutCts = timeoutMs == Timeout.Infinite
            ? null
            : new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        using var waitCts = timeoutCts == null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var waitToken = waitCts?.Token ?? cancellationToken;

        string? line;
        try
        {
            line = _maximumResponseBytes == 0
                ? await _reader.ReadLineAsync(waitToken).ConfigureAwait(false)
                : await ReadBoundedLineAsync(_stream, _maximumResponseBytes, waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("bridge call timed out: " + request.GetRawText());
        }

        if (line == null)
            throw new IOException("bridge connection closed");

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement.Clone();
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("bridge error: " + root.GetProperty("error").GetString());

        return root.GetProperty("data").Clone();
    }

    // Opt-in ingress bound: reject before an unbounded line or JSON DOM exists.
    // One sequential request has exactly one reply; unsolicited trailing frames
    // fail rather than being mistaken for the next command's response.
    internal static async Task<string?> ReadBoundedLineAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, maximumBytes));
        try
        {
            using var line = new MemoryStream(Math.Min(4096, maximumBytes));
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(4096, maximumBytes)), token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (line.Length != 0) throw new IOException("bridge response ended without its newline");
                    return null;
                }
                if (line.Length + read > maximumBytes) throw new InvalidDataException("bridge response exceeds declared byte bound");
                var newline = System.Array.IndexOf(buffer, (byte)'\n', 0, read);
                var used = newline < 0 ? read : newline;
                if (newline >= 0 && read != used + 1) throw new InvalidDataException("unsolicited trailing bridge frame");
                line.Write(buffer, 0, used);
                if (newline >= 0)
                {
                    var length = checked((int)line.Length);
                    if (length > 0 && line.GetBuffer()[length - 1] == '\r') length--;
                    return StrictUtf8.GetString(line.GetBuffer(), 0, length);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public JsonElement Call(string json, int timeoutMs = 30000)
    {
        using var doc = JsonDocument.Parse(json);
        return Call(doc.RootElement.Clone(), timeoutMs);
    }

    public Task<JsonElement> CallAsync(
        string json,
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(json);
        return CallAsync(doc.RootElement.Clone(), timeoutMs, cancellationToken);
    }


    /// <summary>
    /// Writes a command without waiting for a response (fire-and-forget
    /// trigger — e.g. the bridge "save" command, whose reply only returns
    /// after the save pass completes, which may be after the test kills
    /// the server).
    /// </summary>
    public void Send(string json)
    {
        _writer.WriteLine(json);
        _writer.Flush();
    }

    public void Dispose()
    {
        try
        {
            _tcp?.Dispose();
        }
        catch
        {
        }
    }
}
