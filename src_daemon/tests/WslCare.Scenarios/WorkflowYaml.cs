using System.Text.RegularExpressions;

namespace WslCare.Scenarios;

/// <summary>A node of a workflow file as <see cref="WorkflowYaml"/> reads it: a scalar, a sequence or a map.</summary>
internal abstract record YamlNode
{
    /// <summary>This node as a scalar's text; anything else is a test failure naming what it was.</summary>
    public string Text => this is YamlScalar s ? s.Value : throw new InvalidOperationException($"expected a scalar, found {GetType().Name}");

    public YamlMap Map => this as YamlMap ?? throw new InvalidOperationException($"expected a map, found {GetType().Name}");

    public IReadOnlyList<YamlNode> Items => this is YamlSequence s ? s.Values : throw new InvalidOperationException($"expected a sequence, found {GetType().Name}");
}

internal sealed record YamlScalar(string Value) : YamlNode;

internal sealed record YamlSequence(IReadOnlyList<YamlNode> Values) : YamlNode;

internal sealed record YamlMap(IReadOnlyList<KeyValuePair<string, YamlNode>> Entries) : YamlNode
{
    public IEnumerable<string> Keys => Entries.Select(e => e.Key);

    public bool Has(string key) => Entries.Any(e => e.Key == key);

    public YamlNode this[string key] =>
        Entries.FirstOrDefault(e => e.Key == key).Value ?? throw new KeyNotFoundException($"no key '{key}' (has: {string.Join(", ", Keys)})");

    public YamlNode? Find(string key) => Entries.FirstOrDefault(e => e.Key == key).Value;

    /// <summary>A map of scalars as a dictionary (a job's <c>permissions</c>, a step's <c>with</c>).</summary>
    public IReadOnlyDictionary<string, string> Scalars() => Entries.ToDictionary(e => e.Key, e => e.Value.Text, StringComparer.Ordinal);
}

/// <summary>
/// A reader for the YAML subset this repository's workflow files are written in (E4.S2) — so the release workflow's
/// STRUCTURE (permissions per job, needs, the matrix, the steps and their order) can be asserted rather than grepped,
/// without a YAML package (the family's NuGet policy asks for a reason per package; twenty lines of a subset need none).
/// </summary>
/// <remarks>
/// <para>Block maps and sequences by indentation (spaces only), a sequence item that opens a map (<c>- uses: …</c>),
/// plain / single-quoted / double-quoted scalars, trailing comments, flow sequences of scalars (<c>[main]</c>), the
/// empty flow map <c>{}</c>, and literal block scalars (<c>|</c>, <c>|-</c>, <c>|+</c>).</para>
/// <para>Anything outside the subset — a tab, an anchor or alias, a tag, a folded scalar, a flow map with content, a
/// plain scalar continued on the next line, a duplicate key, a document marker — THROWS, naming the file and line. A
/// reader that guessed would let a check pass against a structure the file does not have; every workflow is parsed by
/// <c>ReleaseWorkflowTests.Every_workflow_parses_with_the_reader_its_checks_use</c>, so a construct the reader does not
/// know fails the suite where it was written.</para>
/// </remarks>
internal sealed partial class WorkflowYaml
{
    private readonly string[] _lines;
    private readonly string _source;
    private int _at;

    private WorkflowYaml(string text, string source)
    {
        _lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        _source = source;
    }

    public static YamlMap Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path)).Map;

    public static YamlNode Parse(string text, string source)
    {
        var reader = new WorkflowYaml(text, source);
        reader.SkipIgnorable();
        var root = reader.ParseBlock(0);
        reader.SkipIgnorable();
        return reader._at < reader._lines.Length ? throw reader.Unsupported("content after the document's end") : root;
    }

    private NotSupportedException Unsupported(string what) => new($"{_source}:{_at + 1}: {what} — outside the workflow YAML subset");

    private static bool IsIgnorable(string line)
    {
        var trimmed = line.TrimStart(' ');
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    private void SkipIgnorable()
    {
        while (_at < _lines.Length && IsIgnorable(_lines[_at]))
        {
            _at++;
        }
    }

    private int Indent(string line)
    {
        var indent = line.Length - line.TrimStart(' ').Length;
        return indent < line.Length && line[indent] == '\t' ? throw Unsupported("a tab") : indent;
    }

    private static bool IsSequenceItem(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

    private YamlNode ParseBlock(int indent)
    {
        var line = _lines[_at];
        if (Indent(line) != indent)
        {
            throw Unsupported($"indentation {Indent(line)} where {indent} was expected");
        }

        var content = line[indent..];
        return content.StartsWith("---", StringComparison.Ordinal) ? throw Unsupported("a document marker")
            : IsSequenceItem(content) ? ParseSequence(indent) : ParseMap(indent);
    }

    /// <summary>The next significant line's indent and content, or none at the end.</summary>
    private (int Indent, string Content)? Peek()
    {
        SkipIgnorable();
        if (_at >= _lines.Length)
        {
            return null;
        }

        var indent = Indent(_lines[_at]);
        return (indent, _lines[_at][indent..]);
    }

    private YamlMap ParseMap(int indent)
    {
        var entries = new List<KeyValuePair<string, YamlNode>>();
        while (Peek() is { } next && next.Indent == indent && !IsSequenceItem(next.Content))
        {
            var (key, rest) = SplitKey(next.Content);
            if (entries.Any(e => e.Key == key))
            {
                throw Unsupported($"the duplicate key '{key}'");
            }

            _at++;
            entries.Add(new(key, ParseValue(rest, indent)));
        }

        return Peek() is { } after && after.Indent > indent ? throw Unsupported("a line indented deeper than its map") : new YamlMap(entries);
    }

    private YamlSequence ParseSequence(int indent)
    {
        var items = new List<YamlNode>();
        while (Peek() is { } next && next.Indent == indent && IsSequenceItem(next.Content))
        {
            items.Add(ParseItem(indent, next.Content.Length == 1 ? string.Empty : next.Content[2..]));
        }

        return new YamlSequence(items);
    }

    private YamlNode ParseItem(int indent, string rest)
    {
        var body = rest.TrimStart(' ');
        if (body.Length == 0 || body.StartsWith('#'))
        {
            _at++;
            return ParseNested(indent);
        }

        if (KeyPattern().IsMatch(body))
        {
            // `- key: value` opens a map whose entries sit where `key` sits: the line is re-read as that map's first.
            var column = indent + 2 + (rest.Length - body.Length);
            _lines[_at] = new string(' ', column) + body;
            return ParseMap(column);
        }

        _at++;
        return Scalar(body);
    }

    /// <summary>The value after <c>key:</c> — inline, a literal block, or the block on the following lines.</summary>
    private YamlNode ParseValue(string rest, int indent)
    {
        if (rest.Length == 0 || rest.StartsWith('#'))
        {
            return ParseNested(indent);
        }

        return rest[0] == '|' ? ParseLiteral(rest, indent)
            : rest[0] == '>' ? throw Unsupported("a folded scalar")
            : Scalar(rest);
    }

    private YamlNode ParseNested(int indent) => Peek() switch
    {
        { } next when next.Indent > indent => ParseBlock(next.Indent),
        { } next when next.Indent == indent && IsSequenceItem(next.Content) => ParseSequence(indent),
        _ => new YamlScalar(string.Empty),
    };

    private YamlScalar ParseLiteral(string header, int indent)
    {
        var indicator = StripComment(header).Trim();
        if (indicator is not ("|" or "|-" or "|+"))
        {
            throw Unsupported($"the block scalar header '{indicator}'");
        }

        var lines = new List<string>();
        while (_at < _lines.Length && (_lines[_at].Trim().Length == 0 || Indent(_lines[_at]) > indent))
        {
            lines.Add(_lines[_at]);
            _at++;
        }

        return new YamlScalar(Chomp(lines, indicator));
    }

    private static string Chomp(List<string> lines, string indicator)
    {
        var content = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ').Length).DefaultIfEmpty(0).Min();
        var body = lines.Select(l => l.Length >= content ? l[content..] : string.Empty).ToList();
        var text = string.Join('\n', body).TrimEnd('\n');
        return indicator switch
        {
            "|-" => text,
            "|+" => string.Join('\n', body),
            _ => text.Length == 0 ? text : text + "\n",
        };
    }

    private (string Key, string Value) SplitKey(string content)
    {
        var match = KeyPattern().Match(content);
        if (!match.Success)
        {
            throw Unsupported($"'{content}' is not a key: value line");
        }

        var key = match.Groups["key"].Value;
        return (key.Length > 1 && key[0] is '\'' or '"' ? key[1..^1] : key, match.Groups["rest"].Value.Trim());
    }

    private YamlNode Scalar(string raw)
    {
        var text = raw.Trim();
        return text[0] switch
        {
            '\'' or '"' => new YamlScalar(Quoted(text)),
            '[' => FlowSequence(StripComment(text).Trim()),
            '{' => StripComment(text).Trim() == "{}" ? new YamlMap([]) : throw Unsupported("a flow map with content"),
            '&' or '*' or '!' => throw Unsupported("an anchor, alias or tag"),
            _ => PlainScalar(text),
        };
    }

    private YamlScalar PlainScalar(string text)
    {
        var value = StripComment(text).TrimEnd();
        if (Peek() is { } next && _at < _lines.Length && next.Indent > 0 && !IsSequenceItem(next.Content) && !KeyPattern().IsMatch(next.Content))
        {
            // A plain scalar continued on the next line is legal YAML and folded; the subset refuses it.
            throw Unsupported("a plain scalar continued on the next line");
        }

        return new YamlScalar(value);
    }

    private YamlSequence FlowSequence(string text)
    {
        if (!text.EndsWith(']'))
        {
            throw Unsupported("a flow sequence over several lines");
        }

        var inner = text[1..^1].Trim();
        return new YamlSequence(inner.Length == 0 ? [] : [.. inner.Split(',').Select(item => (YamlNode)Scalar(item.Trim()))]);
    }

    private string Quoted(string text)
    {
        var quote = text[0];
        var end = quote == '\'' ? SingleQuotedEnd(text) : text.IndexOf('"', 1);
        while (quote == '"' && end > 0 && text[end - 1] == '\\')
        {
            end = text.IndexOf('"', end + 1);
        }

        if (end < 0 || StripComment(text[(end + 1)..]).Trim().Length > 0)
        {
            throw Unsupported($"the quoted scalar {text}");
        }

        var inner = text[1..end];
        return quote == '\'' ? inner.Replace("''", "'", StringComparison.Ordinal) : inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static int SingleQuotedEnd(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] == '\'' && (i + 1 >= text.Length || text[i + 1] != '\''))
            {
                return i;
            }

            i += text[i] == '\'' ? 1 : 0;
        }

        return -1;
    }

    /// <summary>A comment starts at <c>#</c> at the start or after whitespace.</summary>
    private static string StripComment(string text)
    {
        var at = text.StartsWith('#') ? 0 : text.IndexOf(" #", StringComparison.Ordinal);
        return at < 0 ? text : text[..at];
    }

    [GeneratedRegex("""^(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-/]*|'[^']*'|"[^"]*"):(?:\s+(?<rest>.*))?$""")]
    private static partial Regex KeyPattern();
}
