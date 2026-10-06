using System.Text.Json;

namespace WslCare.Core.Config;

/// <summary>A leaf of a configuration file: its dotted key, its raw value, and the line it is written on.</summary>
public sealed record RawEntry(string Key, JsonElement Value, int Line);

/// <summary>A configuration file read into leaves, or the reason it could not be.</summary>
public abstract record ConfigDocumentResult
{
    private ConfigDocumentResult()
    {
    }

    public sealed record Parsed(IReadOnlyList<RawEntry> Entries) : ConfigDocumentResult;

    /// <summary>Not JSON, or JSON that is not an object. <paramref name="Line"/> is 1-based; 0 when unknown.</summary>
    public sealed record Malformed(int Line, string Message) : ConfigDocumentResult;
}

/// <summary>
/// Reads one configuration layer: a JSON object whose nested objects spell dotted keys
/// (<c>{"volumes":{"anonymousMaxGb":20}}</c> is <c>volumes.anonymousMaxGb</c>), with the LINE of
/// every leaf kept, so a refusal can say where (plan §15a #1: <c>configError {file, line, message}</c>).
/// </summary>
/// <remarks>Two passes over the same bytes: <see cref="JsonDocument"/> for the values, and a
/// <see cref="Utf8JsonReader"/> walk for the lines, which the document does not keep. Both use the
/// same reader options, so what one accepts the other accepts. A top-level <c>$schema</c> member is
/// ignored, for editors that want one. Arrays are leaves.</remarks>
public static class ConfigDocument
{
    private const string SchemaMember = "$schema";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ConfigDocumentResult Parse(ReadOnlyMemory<byte> bytes)
    {
        var body = WithoutBom(bytes);
        try
        {
            using var document = JsonDocument.Parse(body, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ConfigDocumentResult.Malformed(1, "the configuration must be a JSON object");
            }

            var lines = LeafLines(body.Span);
            var entries = new List<RawEntry>();
            Flatten(document.RootElement, string.Empty, lines, entries);
            return new ConfigDocumentResult.Parsed(entries);
        }
        catch (JsonException e)
        {
            return new ConfigDocumentResult.Malformed((int)(e.LineNumber ?? -1) + 1, Sentence(e.Message));
        }
    }

    private static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> bytes) =>
        bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;

    private static void Flatten(JsonElement element, string prefix, IReadOnlyDictionary<string, int> lines, List<RawEntry> entries)
    {
        foreach (var property in element.EnumerateObject().Where(p => prefix.Length > 0 || p.Name != SchemaMember))
        {
            AddMember(property, prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}", lines, entries);
        }
    }

    /// <summary>An object spells a namespace of dotted keys — unless a SETTING of that name exists: then it is that setting's
    /// value, a wrong one, and becomes an entry so validation refuses it. Retro gate over PR #4: <c>{"auto":{"A4":{}}}</c>
    /// flattened to nothing, the layer read as valid, and A4 silently kept the value below it.</summary>
    private static void AddMember(JsonProperty property, string key, IReadOnlyDictionary<string, int> lines, List<RawEntry> entries)
    {
        if (property.Value.ValueKind == JsonValueKind.Object && ConfigKeys.Find(key) is null)
        {
            Flatten(property.Value, key, lines, entries);
            return;
        }

        entries.Add(new RawEntry(key, property.Value.Clone(), lines.GetValueOrDefault(key)));
    }

    /// <summary>The 1-based line of every member's property name, by dotted key.</summary>
    private static Dictionary<string, int> LeafLines(ReadOnlySpan<byte> body)
    {
        var walk = new LineWalk(LineStarts(body));
        var reader = new Utf8JsonReader(body, ReaderOptions);
        while (reader.Read())
        {
            walk.Step(ref reader);
        }

        return walk.Lines;
    }

    /// <summary>The reader walk behind <see cref="LeafLines"/>: the open objects' names, and the name waiting for its value.
    /// Split into one small method per token kind (C# doctrine 6, retro gate over PR #4).</summary>
    private sealed class LineWalk(List<long> lineStarts)
    {
        private readonly List<string> _path = [];
        private string _pendingName = string.Empty;
        private int _pendingLine;

        public Dictionary<string, int> Lines { get; } = new(StringComparer.Ordinal);

        public void Step(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _pendingName = reader.GetString() ?? string.Empty;
                _pendingLine = LineAt(lineStarts, reader.TokenStartIndex);
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                Leaf();
                reader.Skip();
            }
            else
            {
                Structure(reader.TokenType);
            }
        }

        private void Structure(JsonTokenType token)
        {
            if (token == JsonTokenType.StartObject)
            {
                Open();
            }
            else if (token == JsonTokenType.EndObject)
            {
                Close();
            }
            else if (IsScalar(token))
            {
                Leaf();
            }
        }

        /// <summary>A named object: its own line is kept too (it may be a setting's wrong value), then it is entered.</summary>
        private void Open()
        {
            if (_pendingName.Length == 0)
            {
                return;
            }

            var name = _pendingName;
            Leaf();
            _path.Add(name);
        }

        private void Close()
        {
            if (_path.Count > 0)
            {
                _path.RemoveAt(_path.Count - 1);
            }
        }

        private void Leaf()
        {
            RecordLeaf(Lines, _path, _pendingName, _pendingLine);
            _pendingName = string.Empty;
        }

        private static bool IsScalar(JsonTokenType token) =>
            token is JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null;
    }

    private static void RecordLeaf(Dictionary<string, int> lines, List<string> path, string name, int line)
    {
        if (name.Length > 0)
        {
            lines[path.Count == 0 ? name : $"{string.Join('.', path)}.{name}"] = line;
        }
    }

    private static List<long> LineStarts(ReadOnlySpan<byte> body)
    {
        var starts = new List<long> { 0 };
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineAt(List<long> lineStarts, long byteIndex)
    {
        var index = lineStarts.BinarySearch(byteIndex);
        return index >= 0 ? index + 1 : ~index;
    }

    /// <summary>The reader's message without its trailing <c>Path: … | LineNumber: … | BytePositionInLine: …</c>
    /// — the line is reported in its own field, and the rest is noise to a person.</summary>
    private static string Sentence(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        cut = cut < 0 ? message.IndexOf(" LineNumber:", StringComparison.Ordinal) : cut;
        return (cut < 0 ? message : message[..cut]).TrimEnd('.', ' ');
    }
}
