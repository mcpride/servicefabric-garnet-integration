using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace ClusterManager;

/// <summary>
/// A tiny, dependency-free RESP client used to drive Garnet's CLUSTER administration
/// commands. It is intentionally simple and synchronous: the control loop issues a
/// handful of commands every reconcile cycle, so a battle-tested client library and
/// its cluster auto-discovery heuristics would only get in the way.
/// </summary>
internal sealed class RespClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly Stream _stream;

    public RespClient(string host, int port, int timeoutMs = 5000)
    {
        _tcp = new TcpClient { NoDelay = true };
        // ReSharper disable once AsyncConverter.AsyncWait
        if (!_tcp.ConnectAsync(host, port).Wait(timeoutMs))
        {
            SafeDispose();
            throw new TimeoutException($"Timed out connecting to {host}:{port}.");
        }

        _stream = _tcp.GetStream();
        _stream.ReadTimeout = timeoutMs;
        _stream.WriteTimeout = timeoutMs;
    }

    public void Authenticate(string? user, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        var reply = string.IsNullOrEmpty(user)
            ? Execute("AUTH", password)
            : Execute("AUTH", user, password);

        if (reply.IsError)
        {
            throw new InvalidOperationException($"AUTH failed: {reply.Text}");
        }
    }

    public RespValue Execute(params string[] args)
    {
        WriteCommand(args);
        _stream.Flush();
        return ReadReply();
    }

    private void WriteCommand(string[] args)
    {
        using var buffer = new MemoryStream();

        void WriteAscii(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            buffer.Write(bytes, 0, bytes.Length);
        }

        WriteAscii($"*{args.Length.ToString(CultureInfo.InvariantCulture)}\r\n");
        foreach (var arg in args)
        {
            var payload = Encoding.UTF8.GetBytes(arg);
            WriteAscii($"${payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n");
            buffer.Write(payload, 0, payload.Length);
            WriteAscii("\r\n");
        }

        var all = buffer.ToArray();
        _stream.Write(all, 0, all.Length);
    }

    private RespValue ReadReply()
    {
        var prefix = _stream.ReadByte();
        if (prefix < 0)
        {
            throw new IOException("Connection closed by server.");
        }

        switch ((char)prefix)
        {
            case '+':
                return new RespValue { Kind = RespKind.SimpleString, Text = ReadLine() };
            case '-':
                return new RespValue { Kind = RespKind.Error, Text = ReadLine() };
            case ':':
                return new RespValue
                {
                    Kind = RespKind.Integer,
                    Integer = long.Parse(ReadLine(), CultureInfo.InvariantCulture),
                };
            case '$':
            {
                var length = int.Parse(ReadLine(), CultureInfo.InvariantCulture);
                if (length < 0)
                {
                    return new RespValue { Kind = RespKind.Null };
                }

                return new RespValue { Kind = RespKind.BulkString, Text = ReadBulk(length) };
            }

            case '*':
            {
                var count = int.Parse(ReadLine(), CultureInfo.InvariantCulture);
                if (count < 0)
                {
                    return new RespValue { Kind = RespKind.Null };
                }

                var items = new List<RespValue>(count);
                for (var i = 0; i < count; i++)
                {
                    items.Add(ReadReply());
                }

                return new RespValue { Kind = RespKind.Array, Items = items };
            }

            default:
                throw new IOException($"Unexpected RESP prefix '{(char)prefix}'.");
        }
    }

    private string ReadLine()
    {
        var sb = new StringBuilder();
        int b;
        while ((b = _stream.ReadByte()) >= 0)
        {
            if (b == '\r')
            {
                _stream.ReadByte(); // consume the trailing '\n'
                break;
            }

            sb.Append((char)b);
        }

        return sb.ToString();
    }

    private string ReadBulk(int length)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = _stream.Read(buffer, read, length - read);
            if (n <= 0)
            {
                throw new IOException("Connection closed in the middle of a bulk reply.");
            }

            read += n;
        }

        _stream.ReadByte(); // '\r'
        _stream.ReadByte(); // '\n'
        return Encoding.UTF8.GetString(buffer);
    }

    public void Dispose() => SafeDispose();

    private void SafeDispose()
    {
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            _tcp.Dispose();
        }
        catch
        {
            // ignored
        }
    }
}
