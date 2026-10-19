using System.Globalization;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Thread-safe console writer with elapsed-time stamps; colour is skipped when redirected or NO_COLOR is set.</summary>
internal sealed class ConsoleOutput
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private readonly bool _useColor;

    public ConsoleOutput(TimeProvider time)
    {
        _time = time;
        _startedAt = time.GetTimestamp();
        _useColor = !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    }

    /// <summary>One elevator event: elapsed time, a short tag and a description.</summary>
    public void Event(string tag, ConsoleColor color, string message)
    {
        var elapsed = _time.GetElapsedTime(_startedAt).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
        Write(color, $"  +{elapsed}  {tag,-12} {message}");
    }

    public void Heading(string text) => Write(ConsoleColor.Cyan, $"{Environment.NewLine}== {text}");

    public void Narrate(string text) => Write(ConsoleColor.Gray, $"   {text}");

    public void Result(string text) => Write(ConsoleColor.White, $"   {text}");

    private void Write(ConsoleColor color, string line)
    {
        lock (_gate)
        {
            if (_useColor)
            {
                Console.ForegroundColor = color;
            }

            Console.WriteLine(line);
            if (_useColor)
            {
                Console.ResetColor();
            }
        }
    }
}
