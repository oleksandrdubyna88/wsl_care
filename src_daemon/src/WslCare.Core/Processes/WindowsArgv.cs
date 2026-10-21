using System.Text;

namespace WslCare.Core.Processes;

/// <summary>
/// E15.S7 design §2 (the Windows entry point): a Windows process's command line is ONE string; this gives it back as arguments exactly
/// as the C runtime's <c>CommandLineToArgvW</c> splits it — the inverse of <see cref="WindowsCommandLine.Of"/>, and what the one
/// redaction rule then judges. argv[0] runs to the first space or tab outside its quotes, with no escape processing; every other
/// argument follows the backslash-and-quote rules: <c>2n</c> backslashes before a quote give <c>n</c> and the quote toggles the quoted
/// state, <c>2n+1</c> give <c>n</c> and a literal quote, and inside a quoted run <c>""</c> is one literal quote. Checked against the
/// real <c>CommandLineToArgvW</c> by <c>WindowsRedactionTests</c> (Windows legs).
/// </summary>
internal static class WindowsArgv
{
    public static IReadOnlyList<string> Split(string line)
    {
        if (line.Length == 0)
        {
            return [];
        }

        var (program, end) = Program(line);
        return [program, .. new ArgumentParser(line, end).Parse()];
    }

    private static bool InProgram(char c, bool quoted) => quoted || !ArgumentParser.IsSpace(c);

    /// <summary>argv[0]: up to the first space or tab outside a quoted run; the quotes themselves are not part of it.</summary>
    private static (string Program, int End) Program(string line)
    {
        var program = new StringBuilder();
        var quoted = false;
        var at = 0;
        while (at < line.Length && InProgram(line[at], quoted))
        {
            quoted ^= line[at] == '"';
            if (line[at] != '"')
            {
                program.Append(line[at]);
            }

            at++;
        }
        return (program.ToString(), at);
    }
}

/// <summary>The arguments after argv[0], one pass over the line. A parser holds its position, so it is a small class used once and
/// never shared.</summary>
internal sealed class ArgumentParser(string line, int start)
{
    private readonly List<string> _arguments = [];
    private readonly StringBuilder _current = new();
    private bool _started;
    private bool _quoted;
    private int _at = start;

    /// <summary>The whitespace of a command line: space and tab, nothing else.</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t';

    public IReadOnlyList<string> Parse()
    {
        while (_at < line.Length)
        {
            Step();
        }

        Flush();
        return _arguments;
    }

    private void Step()
    {
        var c = line[_at];
        if (!_quoted && IsSpace(c))
        {
            Flush();
            _at++;
        }
        else
        {
            _started = true;
            Consume(c);
        }
    }

    private void Consume(char c)
    {
        switch (c)
        {
            case '\\':
                Backslashes();
                break;
            case '"':
                Quote();
                break;
            default:
                _current.Append(c);
                _at++;
                break;
        }
    }

    /// <summary>A run of backslashes: literal unless a quote follows; before a quote, half of them stay, and an odd one out makes the
    /// quote a literal character.</summary>
    private void Backslashes()
    {
        var run = RunLength();
        if (!QuoteAfter(run))
        {
            _current.Append('\\', run);
            _at += run;
            return;
        }

        _current.Append('\\', run / 2);
        if (run % 2 == 1)
        {
            _current.Append('"');
            _at += run + 1;
        }
        else
        {
            _at += run;
        }
    }

    private int RunLength()
    {
        var run = 0;
        while (_at + run < line.Length && line[_at + run] == '\\')
        {
            run++;
        }

        return run;
    }

    private bool QuoteAfter(int run) => _at + run < line.Length && line[_at + run] == '"';

    /// <summary>A quote that is not escaped: inside a quoted run a second one right after it is one literal quote; otherwise it
    /// toggles the quoted state.</summary>
    private void Quote()
    {
        if (_quoted && _at + 1 < line.Length && line[_at + 1] == '"')
        {
            _current.Append('"');
            _at += 2;
        }
        else
        {
            _quoted = !_quoted;
            _at++;
        }
    }

    private void Flush()
    {
        if (_started)
        {
            _arguments.Add(_current.ToString());
            _current.Clear();
            _started = false;
        }
    }
}
