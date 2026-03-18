using System.Text;

namespace SimpleShadowsocks.Client.Core.Diagnostics;

public sealed class FixedLogBuffer
{
    private readonly string?[] _items;
    private readonly StringBuilder _builder;
    private int _head;
    private int _count;

    public FixedLogBuffer(int capacity, int initialTextCapacity = 4096)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (initialTextCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialTextCapacity));
        }

        _items = new string[capacity];
        _builder = new StringBuilder(initialTextCapacity);
    }

    public int Capacity => _items.Length;

    public int Count => _count;

    public void Add(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_count < _items.Length)
        {
            var tailIndex = (_head + _count) % _items.Length;
            _items[tailIndex] = message;
            _count++;
            return;
        }

        _items[_head] = message;
        _head = (_head + 1) % _items.Length;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
        _builder.Clear();
    }

    public string BuildText()
    {
        _builder.Clear();

        for (var i = 0; i < _count; i++)
        {
            var index = (_head + i) % _items.Length;
            var message = _items[index];
            if (message is null)
            {
                continue;
            }

            _builder.AppendLine(message);
        }

        return _builder.ToString();
    }
}
