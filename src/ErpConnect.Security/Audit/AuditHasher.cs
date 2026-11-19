using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpConnect.Core.Audit;

namespace ErpConnect.Security.Audit;

/// <summary>Chaîne d'empreintes du journal d'audit : toute modification ou suppression d'une entrée est détectable.</summary>
public static class AuditHasher
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(string previousHash, long sequence, DateTimeOffset timestamp, AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sb = new StringBuilder();
        void Field(string? value)
        {
            var v = value ?? "\0null";
            sb.Append(v.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(v).Append('|');
        }

        Field(previousHash);
        Field(sequence.ToString(CultureInfo.InvariantCulture));
        Field(FormatTimestamp(timestamp));
        Field(entry.ActorSid);
        Field(entry.ActorName);
        Field(entry.Action);
        Field(entry.Target);
        Field(entry.Outcome);
        Field(entry.DetailsJson);
        Field(entry.CorrelationId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Vérifie une suite d'entrées ordonnées par séquence, depuis la première.</summary>
    public static AuditVerification Verify(IEnumerable<AuditEvent> orderedEvents)
    {
        ArgumentNullException.ThrowIfNull(orderedEvents);
        var previous = GenesisHash;
        long expectedSequence = 1;
        long count = 0;
        foreach (var e in orderedEvents)
        {
            if (e.Sequence != expectedSequence)
            {
                return new AuditVerification(false, count, expectedSequence, "Entrée manquante ou séquence rompue.");
            }

            if (!string.Equals(e.PreviousHash, previous, StringComparison.Ordinal))
            {
                return new AuditVerification(false, count, e.Sequence, "Empreinte précédente incohérente.");
            }

            var entry = new AuditEntry(e.ActorSid, e.ActorName, e.Action, e.Target, e.Outcome, e.DetailsJson, e.CorrelationId);
            var expected = Compute(previous, e.Sequence, e.Timestamp, entry);
            if (!string.Equals(expected, e.Hash, StringComparison.Ordinal))
            {
                return new AuditVerification(false, count, e.Sequence, "Contenu modifié.");
            }

            previous = e.Hash;
            expectedSequence++;
            count++;
        }

        return new AuditVerification(true, count, null, null);
    }
}
