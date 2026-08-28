using System.Collections.Concurrent;
using System.Net.Sockets;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;


namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Minimal wire-level client for the AAEmu 1.2 login/game protocols.
///
/// Framing (verified against the server parsers):
///   login : [len u16 LE][type u16 LE][body]
///   game  : [len u16 LE][0xdd][level][(level==1: hash 0, count 0)][type u16 LE][body]
///
/// Bodies use the same primitive layout as PacketStream (strings: [i16 len][UTF8]).
/// The E2E runner needs no real game client — it speaks the real bytes over
/// real TCP to the real servers.
///
/// One background reader task owns all socket reads and pushes parsed frames
/// into a queue; callers wait on the queue for specific opcodes (flow) or
/// drain it (keep-alive). Writes are locked.
/// </summary>
public sealed class BotTcpLink : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly ConcurrentQueue<(ushort Type, byte[] Body)> _frames = new();
    private readonly object _writeLock = new();
    private readonly bool _isGame;
    private CancellationTokenSource _readerCts = new();
    private Task _readerTask;
    private volatile bool _closed;

    public string Name { get; }

    private static readonly object WireDumpLock = new();
    private static string WireDumpPath => Environment.GetEnvironmentVariable("E2E_WIRE_DUMP") ?? "";

    private static void DumpWire(string direction, string name, byte[] bytes)
    {
        var path = WireDumpPath;
        if (string.IsNullOrEmpty(path))
            return;
        lock (WireDumpLock)
        {
            var hex = Convert.ToHexString(bytes);
            File.AppendAllText(path, $"{DateTime.UtcNow:HH:mm:ss.fff} {direction} {name} [{bytes.Length}B] {hex}\n");
        }
    }

    public BotTcpLink(string name, string host, int port, bool isGame)
    {
        Name = name;
        _isGame = isGame;
        _tcp = new TcpClient { NoDelay = true };
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
        _readerTask = Task.Run(ReaderLoopAsync);
    }

    public bool Connected => !_closed && _tcp.Connected;

    /// <summary>Game-protocol frame write: [len u16][0xdd][level][hash? count?][type u16][body].</summary>
    public void SendGameFrame(ushort type, byte level, Action<PacketStream> writeBody)
    {
        var body = new PacketStream();
        body.Write(type);
        writeBody(body);

        var packet = new PacketStream()
            .Write((byte)0xdd)
            .Write(level);
        if (level == 1)
        {
            packet.Write((byte)0); // hash
            packet.Write((byte)0); // count
        }

        packet.Write(body, false);

        var bytes = packet.GetBytes();
        var frame = new byte[2 + bytes.Length];
        BitConverter.TryWriteBytes(frame, (ushort)bytes.Length);
        Buffer.BlockCopy(bytes, 0, frame, 2, bytes.Length);
        SendRaw(frame);
    }

    /// <summary>Login-protocol frame write: [len u16][type u16][body].</summary>
    public void SendLoginFrame(ushort type, Action<PacketStream> writeBody)
    {
        var body = new PacketStream();
        body.Write(type);
        writeBody(body);

        var bytes = body.GetBytes();
        var frame = new byte[2 + bytes.Length];
        BitConverter.TryWriteBytes(frame, (ushort)bytes.Length);
        Buffer.BlockCopy(bytes, 0, frame, 2, bytes.Length);
        SendRaw(frame);
    }

    public void SendRaw(byte[] bytes)
    {
        lock (_writeLock)
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        DumpWire("TX", Name, bytes);
    }

    /// <summary>Waits for a frame of the given type (game or login per link mode).</summary>
    public byte[] ReadFrameUntil(ushort wantType, int timeoutMs = 15000)
        => ReadAnyOf(new[] { wantType }, timeoutMs).Body;

    /// <summary>Waits for one of the given types; returns (matchedType, body).</summary>
    public (ushort Type, byte[] Body) ReadAnyOf(ushort[] wantTypes, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (_frames.TryDequeue(out var frame))
            {
                if (wantTypes.Contains(frame.Type))
                    return frame;
                continue;
            }

            if (_closed)
                throw new IOException($"{Name}: connection closed while waiting for frame");

            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"{Name}: timed out waiting for frame {string.Join("/", wantTypes.Select(t => $"0x{t:X3}"))}");

            Thread.Sleep(20);
        }
    }

    /// <summary>Returns and drains all currently queued frames (discard path).</summary>
    public List<(ushort Type, byte[] Body)> DrainAll()
    {
        var list = new List<(ushort, byte[])>();
        while (_frames.TryDequeue(out var frame))
            list.Add(frame);
        return list;
    }

    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            _readerCts.Cancel();
        }
        catch
        {
        }

        try
        {
            _stream?.Dispose();
        }
        catch
        {
        }

        try
        {
            _tcp?.Dispose();
        }
        catch
        {
        }
    }

    private async Task ReaderLoopAsync()
    {
        var buf = new byte[65536];
        var start = 0;
        var end = 0;
        try
        {
            while (!_readerCts.IsCancellationRequested)
            {
                if (end == buf.Length)
                {
                    var len = end - start;
                    Buffer.BlockCopy(buf, start, buf, 0, len);
                    start = 0;
                    end = len;
                    if (end == buf.Length)
                        throw new IOException($"{Name}: frame larger than read buffer");
                }

                var read = await _stream.ReadAsync(buf.AsMemory(end, buf.Length - end), _readerCts.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                end += read;

                while (true)
                {
                    if (_isGame)
                    {
                        var parsedList = new List<(ushort Type, byte[] Body)>();
                        var consumed = ParseGameFrames(buf, start, end, parsedList);
                        if (consumed < 0)
                            break;
                        foreach (var (gType, gBody) in parsedList)
                        {
                            _frames.Enqueue((gType, gBody));
                            DumpWire("RX", $"{Name}[0x{gType:X3}]", gBody);
                        }
                        start += consumed;
                    }
                    else
                    {
                        var consumed = ParseLoginFrame(buf, start, end, out var lType, out var lBody);
                        if (consumed < 0)
                            break;
                        _frames.Enqueue((lType, lBody));
                        DumpWire("RX", $"{Name}[0x{lType:X3}]", lBody);
                        start += consumed;
                    }

                    if (start == end)
                    {
                        start = 0;
                        end = 0;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Socket errors surface as _closed for the waiters.
        }
        finally
        {
            _closed = true;
        }
    }

    /// <summary>Parses one login frame; returns bytes consumed or -1 when incomplete.</summary>
    public static int ParseLoginFrame(byte[] buf, int start, int end, out ushort type, out byte[] body)
    {
        type = 0;
        body = null;
        if (end - start < 4)
            return -1;

        var len = BitConverter.ToUInt16(buf, start);
        if (end - start < 2 + len)
            return -1;

        type = BitConverter.ToUInt16(buf, start + 2);
        body = new byte[len - 2];
        Buffer.BlockCopy(buf, start + 4, body, 0, len - 2);
        return 2 + len;
    }

    /// <summary>Parses game frame(s); returns bytes consumed or -1 when incomplete.
    /// Handles standard Level 1/2 frames and decompresses Level 4
    /// (CompressedGamePackets) frames. Compressed payloads contain a packet
    /// count followed by repeated [ushort 0][ushort type][body] records; the
    /// body end is decoded from known wire layouts where possible and otherwise
    /// bounded by the next inner header.</summary>
    public static int ParseGameFrames(byte[] buf, int start, int end, List<(ushort Type, byte[] Body)> outputFrames)
    {
        if (end - start < 4)
            return -1;

        var len = BitConverter.ToUInt16(buf, start);
        if (end - start < 2 + len)
            return -1;

        var level = buf[start + 3];
        if (level == 4)
        {
            if (len < 4)
                return -1;

            var packetCount = BitConverter.ToUInt16(buf, start + 4);
            var compressedLen = len - 4;
            if (compressedLen > 0)
            {
                try
                {
                    using var input = new MemoryStream(buf, start + 6, compressedLen);
                    using var output = new MemoryStream();
                    using (var deflate = new System.IO.Compression.DeflateStream(
                               input, System.IO.Compression.CompressionMode.Decompress))
                    {
                        deflate.CopyTo(output);
                    }

                    ParseCompressedFrames(output.ToArray(), packetCount, outputFrames);
                }
                catch
                {
                    // Ignore malformed compressed payloads and preserve framing.
                }
            }

            return 2 + len;
        }

        var header = level == 1 ? 6 : 4; // 0xdd + level + (hash+count for lvl1) + type
        if (len < header)
            return -1;

        var type = BitConverter.ToUInt16(buf, start + 2 + header - 2);
        var body = new byte[len - header];
        Buffer.BlockCopy(buf, start + 2 + header, body, 0, body.Length);
        outputFrames.Add((type, body));
        return 2 + len;
    }

    private static readonly HashSet<ushort> KnownInnerTypes = typeof(SCOffsets)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(ushort))
        .Select(field => (ushort)field.GetRawConstantValue()!)
        .ToHashSet();

    private static void ParseCompressedFrames(
        byte[] data, int packetCount, List<(ushort Type, byte[] Body)> outputFrames)
    {
        var cursor = 0;
        for (var index = 0; index < packetCount && cursor + 4 <= data.Length; index++)
        {
            if (!TryReadInnerHeader(data, cursor, out var type))
            {
                var next = FindInnerHeader(data, cursor + 1);
                if (next < 0)
                    break;
                cursor = next;
                if (!TryReadInnerHeader(data, cursor, out type))
                    break;
            }

            var bodyStart = cursor + 4;
            var bodyLength = TryGetKnownInnerBodyLength(type, data, bodyStart, out var knownLength)
                ? knownLength
                : FindInnerHeader(data, bodyStart) is var nextHeader && nextHeader >= 0
                    ? nextHeader - bodyStart
                    : data.Length - bodyStart;
            if (bodyLength < 0 || bodyStart + bodyLength > data.Length)
                break;

            var body = new byte[bodyLength];
            Buffer.BlockCopy(data, bodyStart, body, 0, bodyLength);
            outputFrames.Add((type, body));
            cursor = bodyStart + bodyLength;
        }
    }

    private static bool TryReadInnerHeader(byte[] data, int offset, out ushort type)
    {
        type = 0;
        if (offset < 0 || offset + 4 > data.Length ||
            data[offset] != 0 || data[offset + 1] != 0 ||
            !KnownInnerTypes.Contains(type = BitConverter.ToUInt16(data, offset + 2)))
        {
            type = 0;
            return false;
        }

        return true;
    }

    private static int FindInnerHeader(byte[] data, int offset)
    {
        for (var i = Math.Max(0, offset); i + 4 <= data.Length; i++)
            if (TryReadInnerHeader(data, i, out _))
                return i;
        return -1;
    }

    private static bool TryGetKnownInnerBodyLength(
        ushort type, byte[] data, int bodyStart, out int bodyLength)
    {
        bodyLength = 0;
        if (type == SCOffsets.SCCombatFirstHitPacket)
        {
            bodyLength = 10; // vuId bc, huId bc, htId u32
            return bodyStart + bodyLength <= data.Length;
        }

        if (type == SCOffsets.SCUnitDamagedPacket &&
            TryReadUnitDamagedBody(data, bodyStart, out bodyLength, out _))
            return true;

        if (type == SCOffsets.SCBuffCreatedPacket &&
            TryReadBuffCreatedBody(data, bodyStart, out bodyLength))
            return true;

        return false;
    }

    private static bool TryReadBuffCreatedBody(byte[] data, int bodyStart, out int bodyLength)
    {
        bodyLength = 0;
        var cursor = bodyStart;
        if (!TrySkipSkillCaster(data, ref cursor) ||
            !TryAdvance(data, ref cursor, sizeof(uint) + 3 + sizeof(uint) + sizeof(uint) + 1 + sizeof(short) + sizeof(uint)) ||
            !TrySkipPisc(data, ref cursor, 4))
            return false;

        bodyLength = cursor - bodyStart;
        return true;
    }

    private static bool TryReadUnitDamagedBody(
        byte[] data, int bodyStart, out int bodyLength, out int hitTypeOffset)
    {
        bodyLength = 0;
        hitTypeOffset = 0;
        var cursor = bodyStart;
        if (!TryAdvance(data, ref cursor, 7 + 4 + 3 + 3 + 1) ||
            !TrySkipPisc(data, ref cursor, 3) ||
            !TrySkipPisc(data, ref cursor, 3) ||
            !TryAdvance(data, ref cursor, 1))
            return false;

        hitTypeOffset = cursor;
        if (!TryAdvance(data, ref cursor, sizeof(ushort) + 1 + 1))
            return false;

        bodyLength = cursor - bodyStart;
        return true;
    }

    private static bool TrySkipSkillCaster(byte[] data, ref int cursor)
    {
        var typeOffset = cursor;
        if (!TryAdvance(data, ref cursor, 1 + 3))
            return false;

        var casterType = data[typeOffset];
        return casterType switch
        {
            0 or 1 or 4 => true, // Unit, Doodad, or Gimmick
            2 => TryAdvance(data, ref cursor, 8 + 4 + 1 + 4), // Item
            3 => TryAdvance(data, ref cursor, 4), // Mount
            _ => false
        };
    }

    private static bool TrySkipPisc(byte[] data, ref int cursor, int count)
    {
        if (!TryAdvance(data, ref cursor, 1))
            return false;

        var flags = data[cursor - 1];
        for (var index = 0; index < count; index++)
        {
            var kind = (flags >> (index * 2)) & 0x03;
            var width = kind switch
            {
                0 => 1, // byte
                1 => 2, // ushort
                2 => 3, // bc
                _ => 4 // uint
            };
            if (!TryAdvance(data, ref cursor, width))
                return false;
        }

        return true;
    }

    private static bool TryAdvance(byte[] data, ref int cursor, int count)
    {
        if (count < 0 || cursor < 0 || cursor > data.Length - count)
            return false;
        cursor += count;
        return true;
    }

    /// <summary>Strictly matches SCUnitDamaged's target and excludes the
    /// immune hit type. The target is fixed by the cast/caster wire prefix;
    /// all later fields, including both PISC arrays, are variable-width.</summary>
    public static bool TryReadVictimMatchedNonImmuneUnitDamaged(
        byte[] body, uint victimObjId, out bool immune)
    {
        immune = false;
        if (body == null || body.Length < 17 || ReadBc(body, 14) != victimObjId ||
            !TryReadUnitDamagedBody(body, 0, out _, out var hitTypeOffset))
            return false;

        var hitType = (ushort)(body[hitTypeOffset] | (body[hitTypeOffset + 1] << 8));
        immune = (hitType & 0x1f) == 0x12;
        return !immune;
    }

    private static uint ReadBc(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));

    /// <summary>Parses one game frame; returns bytes consumed or -1 when incomplete.</summary>
    public static int ParseGameFrame(byte[] buf, int start, int end, out ushort type, out byte[] body)
    {
        var list = new List<(ushort, byte[])>();
        var consumed = ParseGameFrames(buf, start, end, list);
        if (consumed > 0 && list.Count > 0)
        {
            type = list[0].Item1;
            body = list[0].Item2;
            return consumed;
        }
        type = 0;
        body = null;
        return consumed;
    }

    public void Dispose() => Close();
}
