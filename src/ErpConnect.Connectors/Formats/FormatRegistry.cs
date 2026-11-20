using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors.Formats;

public static class FormatRegistry
{
    public static IReadOnlyList<IRecordFormat> All { get; } = [new CsvFormat(), new JsonFormat(), new XmlFormat(), new XlsxFormat()];

    public static IRecordFormat Get(string? id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal))
        ?? throw new ConnectorException(ErrorKind.Permanent, $"Format inconnu : {id}.");
}
