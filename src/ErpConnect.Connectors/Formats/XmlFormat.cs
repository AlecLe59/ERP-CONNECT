using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors.Formats;

/// <summary>
/// XML en flux : chaque élément <c>recordElement</c> devient un enregistrement. Les DTD sont refusées et aucune
/// ressource externe n'est résolue (protection XXE et « milliard de rires »).
/// Attributs → champs « @nom », éléments enfants → champs (imbriqués), éléments répétés → listes.
/// </summary>
public sealed class XmlFormat : IRecordFormat
{
    public string Id => "xml";

    public IReadOnlyList<ConfigField> Options { get; } =
    [
        new("recordElement", "Élément enregistrement", ConfigFieldType.Text, Default: "Record"),
        new("rootElement", "Élément racine (écriture)", ConfigFieldType.Text, Default: "Records"),
    ];

    internal static XmlReaderSettings SecureSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        Async = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = 0,
    };

    public async IAsyncEnumerable<Record> ReadAsync(Stream input, IReadOnlyDictionary<string, string> options, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var recordName = options.GetValueOrDefault("recordElement") is { Length: > 0 } n ? n : "Record";
        using var reader = XmlReader.Create(input, SecureSettings());
        while (true)
        {
            XElement? element;
            try
            {
                element = await NextAsync(reader, recordName, ct).ConfigureAwait(false);
            }
            catch (XmlException ex)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"XML invalide ou refusé (ligne {ex.LineNumber}) : {ex.Message}", ex);
            }

            ct.ThrowIfCancellationRequested();
            if (element is null)
            {
                yield break;
            }

            yield return (Record)Convert(element, 0)!;
        }
    }

    private static async Task<XElement?> NextAsync(XmlReader reader, string recordName, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == recordName)
            {
                return (XElement)await XNode.ReadFromAsync(reader, ct).ConfigureAwait(false);
            }

            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    private static object? Convert(XElement e, int depth)
    {
        if (depth > Record.MaxDepth)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Imbrication XML trop profonde.");
        }

        if (!e.HasElements && !e.HasAttributes)
        {
            return e.IsEmpty ? null : e.Value;
        }

        var r = new Record();
        foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            r.Set("@" + a.Name.LocalName, a.Value);
        }

        foreach (var group in e.Elements().GroupBy(c => c.Name.LocalName, StringComparer.Ordinal))
        {
            var values = group.Select(c => Convert(c, depth + 1)).ToList();
            r.Set(group.Key, values.Count == 1 ? values[0] : values);
        }

        if (!e.HasElements && e.Value.Length > 0)
        {
            r.Set("#text", e.Value);
        }

        return r;
    }

    public IFormatWriter CreateWriter(Stream output, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Writer(output, options.GetValueOrDefault("rootElement") is { Length: > 0 } root ? root : "Records",
            options.GetValueOrDefault("recordElement") is { Length: > 0 } rec ? rec : "Record");
    }

    private sealed class Writer : IFormatWriter
    {
        private readonly XmlWriter _xml;
        private readonly string _root;
        private readonly string _record;
        private bool _started;

        public Writer(Stream output, string root, string record)
        {
            _xml = XmlWriter.Create(output, new XmlWriterSettings { Async = true, Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false });
            _root = XmlConvert.VerifyNCName(root);
            _record = XmlConvert.VerifyNCName(record);
        }

        private async Task StartAsync()
        {
            if (!_started)
            {
                await _xml.WriteStartDocumentAsync().ConfigureAwait(false);
                await _xml.WriteStartElementAsync(null, _root, null).ConfigureAwait(false);
                _started = true;
            }
        }

        public async Task WriteAsync(Record record, CancellationToken ct)
        {
            await StartAsync().ConfigureAwait(false);
            await WriteElementAsync(_record, record).ConfigureAwait(false);
        }

        private async Task WriteElementAsync(string name, object? value)
        {
            if (value is IReadOnlyList<object?> list)
            {
                foreach (var item in list)
                {
                    await WriteElementAsync(name, item).ConfigureAwait(false);
                }

                return;
            }

            await _xml.WriteStartElementAsync(null, XmlConvert.EncodeLocalName(name), null).ConfigureAwait(false);
            if (value is Record r)
            {
                foreach (var (k, v) in r.Where(p => p.Key.StartsWith('@')))
                {
                    await _xml.WriteAttributeStringAsync(null, XmlConvert.EncodeLocalName(k[1..]), null, Record.ToInvariantString(v)).ConfigureAwait(false);
                }

                foreach (var (k, v) in r.Where(p => !p.Key.StartsWith('@')))
                {
                    if (k == "#text")
                    {
                        await _xml.WriteStringAsync(Record.ToInvariantString(v)).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteElementAsync(k, v).ConfigureAwait(false);
                    }
                }
            }
            else if (value is not null)
            {
                await _xml.WriteStringAsync(Record.ToInvariantString(value)).ConfigureAwait(false);
            }

            await _xml.WriteEndElementAsync().ConfigureAwait(false);
        }

        public async Task FinishAsync(CancellationToken ct)
        {
            await StartAsync().ConfigureAwait(false);
            await _xml.WriteEndElementAsync().ConfigureAwait(false);
            await _xml.WriteEndDocumentAsync().ConfigureAwait(false);
            await _xml.FlushAsync().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _xml.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
