using System.Text;

namespace Anchor.Core;

/// <summary>
/// A process's output, held to a fixed size as it arrives: the first and the last lines are kept and the ones between are
/// counted, so a command that prints without end can't use up memory. Lines are kept or left out whole, so a secret value on
/// a line is either all there, to be masked, or not there at all.
/// </summary>
/// <param name="limit">Roughly how many characters to keep, half from the start and half from the end.</param>
public sealed class ProcessOutput(int limit = Toolbox.MaxResultChars - 1_000)
{
    readonly Lock _lock = new();
    readonly StringBuilder _head = new();
    readonly Queue<string> _tail = new();
    int _tailChars;
    long _omitted;

    public bool IsEmpty
    {
        get
        {
            lock (_lock)
                return _head.Length == 0 && _tail.Count == 0 && _omitted == 0;
        }
    }

    public void Add(string line)
    {
        line = line.Length < limit / 2 ? line + "\n" : $"[a line of {line.Length:N0} characters, left out]\n";
        lock (_lock)
        {
            // Once a line has gone to the tail, later ones follow it there, so the order is kept.
            if (_tail.Count == 0 && _head.Length + line.Length <= limit / 2)
            {
                _head.Append(line);
                return;
            }
            _tail.Enqueue(line);
            _tailChars += line.Length;
            while (_tailChars > limit / 2)
            {
                var dropped = _tail.Dequeue();
                _tailChars -= dropped.Length;
                _omitted += dropped.Length;
            }
        }
    }

    public override string ToString()
    {
        lock (_lock)
            return _omitted == 0
                ? _head + string.Concat(_tail)
                : $"{_head}[... {_omitted:N0} characters of output left out ...]\n{string.Concat(_tail)}";
    }
}
