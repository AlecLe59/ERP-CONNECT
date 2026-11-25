using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

// Accès direct au serveur de test local (certificat auto-signé) pour préparer et vérifier les boîtes ;
// le connecteur, lui, est toujours testé avec un certificat épinglé ou refusé.
#pragma warning disable CA5359

namespace ErpConnect.Connectors.Tests;

/// <summary>
/// Vrai serveur de messagerie de test (GreenMail en CI) décrit par ERPCONNECT_TEST_MAIL (JSON : host, imapsPort, smtpsPort,
/// smtpPort, domain, password ; comptes « compta » et « fournisseur »). Absent : test ignoré, sauf ERPCONNECT_REQUIRE_DB=1.
/// </summary>
public sealed class MailFactAttribute : FactAttribute
{
    public const string Variable = "ERPCONNECT_TEST_MAIL";

    public MailFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is null && Environment.GetEnvironmentVariable("ERPCONNECT_REQUIRE_DB") != "1")
        {
            Skip = $"Serveur de messagerie de test absent ({Variable}).";
        }
    }
}

public static class MailTestServer
{
    private static JsonElement Config => JsonDocument.Parse(Environment.GetEnvironmentVariable(MailFactAttribute.Variable)
        ?? throw new InvalidOperationException($"{MailFactAttribute.Variable} absente : serveur de messagerie de test non configuré.")).RootElement;

    public static string Host => Config.GetProperty("host").GetString()!;

    public static int Port(string name) => int.Parse(Config.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    public static string Password => Config.GetProperty("password").GetString()!;

    public static string Address(string user) => $"{user}@{Config.GetProperty("domain").GetString()}";

    /// <summary>Empreinte SHA-256 du certificat présenté par le serveur (auto-signé en test), pour l'épinglage.</summary>
    public static async Task<string> FingerprintAsync(int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Host, port);
        string? fingerprint = null;
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert!.GetRawCertData()));
            return true;
        });
        await ssl.AuthenticateAsClientAsync("localhost");
        return fingerprint!;
    }

    /// <summary>Paramètres d'une connexion IMAPS (certificat épinglé) au compte donné.</summary>
    public static async Task<ConnectorSettings> ImapAsync(string user = "compta", Dictionary<string, string?>? extra = null, string? password = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["host"] = Host, ["port"] = Port("imapsPort").ToString(System.Globalization.CultureInfo.InvariantCulture), ["security"] = "ssl",
            ["certificateFingerprint"] = await FingerprintAsync(Port("imapsPort")), ["username"] = Address(user), ["timeoutSeconds"] = "15",
        };
        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        return new ConnectorSettings(values, new Dictionary<string, string> { ["password"] = password ?? Password });
    }

    /// <summary>Paramètres d'une connexion SMTPS (certificat épinglé) du compte « fournisseur » vers « compta ».</summary>
    public static async Task<ConnectorSettings> SmtpAsync(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["host"] = Host, ["port"] = Port("smtpsPort").ToString(System.Globalization.CultureInfo.InvariantCulture), ["security"] = "ssl",
            ["certificateFingerprint"] = await FingerprintAsync(Port("smtpsPort")), ["username"] = Address("fournisseur"), ["timeoutSeconds"] = "15",
            ["from"] = Address("fournisseur"), ["to"] = Address("compta"),
        };
        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        return new ConnectorSettings(values, new Dictionary<string, string> { ["password"] = Password });
    }

    /// <summary>Dépose un courriel (avec ses pièces jointes) dans la boîte de « compta », par le SMTP du serveur de test.</summary>
    public static async Task SendAsync(string subject, string from = "fournisseur", params (string Name, string Content)[] attachments)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(Address(from)));
        message.To.Add(MailboxAddress.Parse(Address("compta")));
        message.Subject = subject;
        var builder = new BodyBuilder { TextBody = "Bonjour, ci-joint." };
        foreach (var (name, content) in attachments)
        {
            builder.Attachments.Add(name, Encoding.UTF8.GetBytes(content));
        }

        message.Body = builder.ToMessageBody();
        using var smtp = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await smtp.ConnectAsync(Host, Port("smtpsPort"), SecureSocketOptions.SslOnConnect);
        await smtp.AuthenticateAsync(Address(from), Password);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }

    /// <summary>Accès direct à la boîte de « compta » pour vérifier ce que le connecteur a fait (ou n'a pas fait).</summary>
    public static async Task<T> InboxAsync<T>(Func<ImapClient, Task<T>> action)
    {
        using var imap = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await imap.ConnectAsync(Host, Port("imapsPort"), SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync(Address("compta"), Password);
        try
        {
            return await action(imap);
        }
        finally
        {
            await imap.DisconnectAsync(true);
        }
    }

    /// <summary>Messages du dossier dont l'objet contient le texte, avec leur état « lu ».</summary>
    public static Task<List<(string Subject, bool Seen, MimeMessage Message)>> MessagesAsync(string subjectContains, string folder = "INBOX") => InboxAsync(async imap =>
    {
        var f = await imap.GetFolderAsync(folder);
        await f.OpenAsync(FolderAccess.ReadOnly);
        var uids = await f.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(subjectContains));
        var result = new List<(string, bool, MimeMessage)>();
        foreach (var s in await f.FetchAsync(uids, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId))
        {
            var message = await f.GetMessageAsync(s.UniqueId);
            result.Add((message.Subject ?? string.Empty, s.Flags!.Value.HasFlag(MessageFlags.Seen), message));
        }

        return result;
    });

    public static Task EnsureFolderAsync(string name) => InboxAsync(async imap =>
    {
        var root = imap.GetFolder(imap.PersonalNamespaces[0]);
        if ((await root.GetSubfoldersAsync()).All(f => f.Name != name))
        {
            await root.CreateAsync(name, true);
        }

        return true;
    });

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
}
