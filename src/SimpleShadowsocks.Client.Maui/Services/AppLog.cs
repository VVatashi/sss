using SimpleShadowsocks.Client.Core.Diagnostics;
using System.Text;

namespace SimpleShadowsocks.Client.Maui.Services;

public static class AppLog
{
    private const int MaxMessages = 512;
    private static readonly object Sync = new();
    private static readonly FixedLogBuffer Buffer = new(MaxMessages);
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;
    private static bool _initialized;

    public static event Action<string>? LineAdded;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _originalOut = Console.Out;
            _originalError = Console.Error;
            Console.SetOut(new RelayTextWriter(_originalOut, WriteRaw));
            Console.SetError(new RelayTextWriter(_originalError, WriteRaw));
            _initialized = true;
        }
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        AppendLine(line);
        _originalOut?.WriteLine(line);
    }

    public static string GetText()
    {
        lock (Sync)
        {
            return Buffer.BuildText();
        }
    }

    private static void WriteRaw(string message)
    {
        AppendLine(message);
    }

    private static void AppendLine(string message)
    {
        Action<string>? handlers;

        lock (Sync)
        {
            Buffer.Add(message);
            handlers = LineAdded;
        }

        handlers?.Invoke(message);
    }

    private sealed class RelayTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly Action<string> _sink;
        private readonly StringBuilder _buffer = new();

        public RelayTextWriter(TextWriter inner, Action<string> sink)
        {
            _inner = inner;
            _sink = sink;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            if (value == '\n')
            {
                FlushBuffered();
                return;
            }

            if (value != '\r')
            {
                _buffer.Append(value);
            }
        }

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            _sink(value ?? string.Empty);
        }

        private void FlushBuffered()
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            var line = _buffer.ToString();
            _buffer.Clear();
            _sink(line);
        }
    }
}
