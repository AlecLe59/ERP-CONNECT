using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Connectors.Transports;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace ErpConnect.Connectors;

/// <summary>Paramètres communs IMAP et SMTP : serveur, chiffrement, identifiants (mot de passe ou OAuth2).</summary>
internal static class MailCommon
{
    public static IReadOnlyList<ConfigField> ServerFields(string defaultPort, string defaultSecurity) =>
    [
        new("host", "Serveur", ConfigFieldType.Text, Required: true, Help: "Par exemple outlook.office365.com, imap.gmail.com, mail.entreprise.fr."),
        new("port", "Port", ConfigFieldType.Number, Default: defaultPort),
        new("security", "Chiffrement", ConfigFieldType.Choice, Default: defaultSecurity, Choices: ["ssl", "starttls", "none"],
            Help: "ssl : chiffré dès la connexion (993, 465). starttls : chiffrement négocié (587, 143). none : refusé sauf autorisation."),
        new("allowPlain", "Autoriser une connexion non chiffrée", ConfigFieldType.Boolean, Default: "false"),
        new("certificateFingerprint", "Empreinte SHA-256 du certificat (épinglage)", ConfigFieldType.Text, Help: "Pour un serveur à certificat auto-signé. Vide = validation Windows."),
        new("auth", "Authentification", ConfigFieldType.Choice, Default: "password", Choices: ["password", "oauth2"],
            Help: "oauth2 : Microsoft 365 ou Google en « client credentials » (application inscrite par l'administrateur)."),
        new("username", "Adresse ou utilisateur", ConfigFieldType.Text, Required: true),
        new("password", "Mot de passe (ou mot de passe d'application)", ConfigFieldType.Text, Secret: true),
        new("tokenUrl", "OAuth2 : URL du jeton", ConfigFieldType.Url, Help: "Microsoft 365 : https://login.microsoftonline.com/<locataire>/oauth2/v2.0/token"),
        new("clientId", "OAuth2 : identifiant client", ConfigFieldType.Text),
        new("clientSecret", "OAuth2 : secret client", ConfigFieldType.Text, Secret: true),
        new("scope", "OAuth2 : portée", ConfigFieldType.Text, Help: "Microsoft 365 : https://outlook.office365.com/.default"),
        new("timeoutSeconds", "Délai (s)", ConfigFieldType.Number, Default: "60"),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static SecureSocketOptions Security(ConnectorSettings settings, string fallback)
    {
        var security = settings.Get("security") ?? fallback;
        return security switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" when settings.GetBool("allowPlain") => SecureSocketOptions.None,
            "none" => throw new ConnectorException(ErrorKind.Permanent, "Connexion non chiffrée refusée : choisissez ssl ou starttls, ou autorisez-la explicitement."),
            _ => throw new ConnectorException(ErrorKind.Permanent, "Chiffrement : ssl, starttls ou none."),
        };
    }

    public static int Timeout(ConnectorSettings settings) => Math.Clamp(settings.GetInt("timeoutSeconds", 60), 1, 600) * 1000;

    /// <summary>Certificat validé par la chaîne de confiance du système, ou épinglé par son empreinte SHA-256.</summary>
    public static RemoteCertificateValidationCallback CertificateCheck(ConnectorSettings settings, Action<string?> seen)
    {
        var pin = settings.Get("certificateFingerprint");
        return (_, certificate, _, errors) =>
        {
            var actual = certificate is null ? null : FtpTransport.Fingerprint(certificate.GetRawCertData());
            seen(actual);
            return !string.IsNullOrWhiteSpace(pin) ? actual is not null && FtpTransport.FingerprintMatches(pin, actual) : errors == SslPolicyErrors.None;
        };
    }

    /// <summary>Connexion et authentification, erreurs classées : identifiants et certificat = permanent, réseau = transitoire.</summary>
    public static async Task ConnectAsync(MailService client, ConnectorSettings settings, string defaultSecurity, int defaultPort, CancellationToken ct)
    {
        string? fingerprint = null;
        client.Timeout = Timeout(settings);
        client.ServerCertificateValidationCallback = CertificateCheck(settings, f => fingerprint = f);
        var security = Security(settings, defaultSecurity);
        try
        {
            await client.ConnectAsync(settings.Require("host"), settings.GetInt("port", defaultPort), security, ct).ConfigureAwait(false);
            if (settings.Get("auth") == "oauth2")
            {
                var token = await TokenAsync(settings, ct).ConfigureAwait(false);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(settings.Require("username"), token), ct).ConfigureAwait(false);
            }
            else
            {
                await client.AuthenticateAsync(settings.Require("username"), settings.GetSecret("password") ?? string.Empty, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (Map(ex, fingerprint) is { } mapped)
        {
            throw mapped;
        }
    }

    public static ConnectorException? Map(Exception ex, string? fingerprint = null) => ex switch
    {
        ConnectorException => null,
        OperationCanceledException => null,
        AuthenticationException => new ConnectorException(ErrorKind.Permanent, "Identifiants refusés par le serveur de messagerie.", ex),
        SslHandshakeException => new ConnectorException(ErrorKind.Permanent, $"Certificat du serveur refusé{(fingerprint is null ? string.Empty : $" (SHA-256 {fingerprint})")}.", ex),
        FolderNotFoundException f => new ConnectorException(ErrorKind.Permanent, $"Dossier de messagerie introuvable : {f.FolderName}.", ex),
        SmtpCommandException s when (int)s.StatusCode >= 500 => new ConnectorException(ErrorKind.Permanent, $"Envoi refusé par le serveur : {s.Message}", ex),
        SmtpCommandException s => new ConnectorException(ErrorKind.Transient, $"Envoi différé par le serveur : {s.Message}", ex),
        ImapCommandException i when i.Response == ImapCommandResponse.No => new ConnectorException(ErrorKind.Permanent, $"Commande IMAP refusée : {i.Message}", ex),
        System.Net.Sockets.SocketException or IOException or TimeoutException or ProtocolException or ServiceNotConnectedException or ImapCommandException
            => new ConnectorException(ErrorKind.Transient, $"Serveur de messagerie injoignable ou interrompu : {ex.Message}", ex),
        _ => null,
    };

    /// <summary>Jeton OAuth2 « client credentials » (Microsoft 365, Google Workspace). HTTPS obligatoire.</summary>
    internal static async Task<string> TokenAsync(ConnectorSettings settings, CancellationToken ct)
    {
        if (!Uri.TryCreate(settings.Require("tokenUrl"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ConnectorException(ErrorKind.Permanent, "URL du jeton invalide (HTTPS obligatoire).");
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", settings.Require("clientId")),
            new("client_secret", settings.GetSecret("clientSecret") ?? throw new ConnectorException(ErrorKind.Permanent, "Secret client manquant.")),
        };
        if (settings.Get("scope") is { } scope)
        {
            form.Add(new("scope", scope));
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                throw new ConnectorException(code is 408 or 429 or >= 500 ? ErrorKind.Transient : ErrorKind.Permanent,
                    $"Jeton OAuth2 refusé : HTTP {code} {(body.Length > 300 ? body[..300] : body)}");
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("access_token", out var t) && t.GetString() is { Length: > 0 } token
                ? token
                : throw new ConnectorException(ErrorKind.Permanent, "Réponse OAuth2 sans access_token.");
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(ErrorKind.Transient, $"Jeton OAuth2 : erreur réseau : {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ErrorKind.Transient, "Jeton OAuth2 : délai dépassé.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Réponse OAuth2 illisible.", ex);
        }
    }

    public static string SafeFileName(string? name, string fallback)
    {
        var raw = Path.GetFileName((name ?? string.Empty).Replace('\\', '/').Split('/')[^1]);
        var clean = new string(raw.Select(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? fallback : clean.Length > 200 ? clean[..200] : clean;
    }
}

/// <summary>Filtres de lecture d'une boîte (options de la source).</summary>
internal sealed record ImapFilter(string Folder, string? From, string? Subject, int SinceDays, bool UnseenOnly, string AfterRead, string? MoveTo)
{
    public static ImapFilter Create(ConnectorSettings settings, IReadOnlyDictionary<string, string>? options)
    {
        string? Opt(string n) => options is not null && options.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var since = int.TryParse(Opt("sinceDays"), NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 30;
        var after = Opt("afterRead") ?? "none";
        if (after is not ("none" or "seen" or "move"))
        {
            throw new ConnectorException(ErrorKind.Permanent, "afterRead : none, seen ou move.");
        }

        var moveTo = Opt("moveTo");
        if (after == "move" && moveTo is null)
        {
            throw new ConnectorException(ErrorKind.Permanent, "afterRead=move demande le dossier de destination (moveTo).");
        }

        return new ImapFilter(Opt("folder") ?? settings.Get("folder") ?? "INBOX", Opt("from"), Opt("subject"), Math.Clamp(since, 0, 3650),
            string.Equals(Opt("unseenOnly"), "true", StringComparison.OrdinalIgnoreCase), after, moveTo);
    }
}

/// <summary>
/// Boîte IMAP vue comme un dossier de fichiers : chaque pièce jointe des messages retenus est un fichier nommé
/// « UID-partie-nom » (ordre d'arrivée). La boîte est ouverte en lecture seule : rien n'est modifié pendant la lecture.
/// Après une exécution réussie, et seulement si l'option le demande, les messages lus sont marqués lus ou déplacés.
/// </summary>
internal sealed class ImapTransport : IFileTransport
{
    private readonly ConnectorSettings _settings;
    private readonly ImapFilter _filter;
    private ImapClient? _client;
    private IMailFolder? _folder;
    private readonly SortedSet<UniqueId> _listed = [];

    public ImapTransport(ConnectorSettings settings, ImapFilter filter)
    {
        _settings = settings;
        _filter = filter;
    }

    public IReadOnlyCollection<UniqueId> Listed => _listed;

    private async Task<IMailFolder> FolderAsync(FolderAccess access, CancellationToken ct)
    {
        if (_client is not { IsConnected: true, IsAuthenticated: true })
        {
            _client?.Dispose();
            _client = new ImapClient();
            _folder = null;
            await MailCommon.ConnectAsync(_client, _settings, "ssl", 993, ct).ConfigureAwait(false);
        }

        try
        {
            _folder ??= await _client.GetFolderAsync(_filter.Folder, ct).ConfigureAwait(false);
            if (!_folder.IsOpen || (access == FolderAccess.ReadWrite && _folder.Access != FolderAccess.ReadWrite))
            {
                await _folder.OpenAsync(access, ct).ConfigureAwait(false);
            }

            return _folder;
        }
        catch (Exception ex) when (MailCommon.Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<int> CountAsync(CancellationToken ct) => (await FolderAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false)).Count;

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string pattern, CancellationToken ct)
    {
        var namePattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        if (namePattern.Contains('/', StringComparison.Ordinal) || namePattern.Contains('\\', StringComparison.Ordinal))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Ressource : un motif de nom de pièce jointe (par exemple *.csv), sans dossier.");
        }

        var folder = await FolderAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        try
        {
            SearchQuery query = SearchQuery.All;
            if (_filter.SinceDays > 0)
            {
                query = query.And(SearchQuery.DeliveredAfter(DateTime.UtcNow.Date.AddDays(-_filter.SinceDays)));
            }

            if (_filter.From is { } from)
            {
                query = query.And(SearchQuery.FromContains(from));
            }

            if (_filter.Subject is { } subject)
            {
                query = query.And(SearchQuery.SubjectContains(subject));
            }

            if (_filter.UnseenOnly)
            {
                query = query.And(SearchQuery.NotSeen);
            }

            var uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);
            var files = new List<RemoteFile>();
            _listed.Clear();
            if (uids.Count == 0)
            {
                return files;
            }

            var summaries = await folder.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate, ct).ConfigureAwait(false);
            foreach (var m in summaries.OrderBy(s => s.UniqueId.Id))
            {
                var any = false;
                foreach (var part in m.BodyParts.Where(p => p.FileName is { Length: > 0 } || p.IsAttachment))
                {
                    var name = MailCommon.SafeFileName(part.FileName, $"piece-{part.PartSpecifier}");
                    if (!SafePath.Matches(name, namePattern))
                    {
                        continue;
                    }

                    any = true;
                    files.Add(new RemoteFile(FileName(m.UniqueId, part.PartSpecifier, name), part.Octets, m.InternalDate ?? DateTimeOffset.UnixEpoch));
                }

                if (any)
                {
                    _listed.Add(m.UniqueId);
                }
            }

            return files;
        }
        catch (Exception ex) when (MailCommon.Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    internal static string FileName(UniqueId uid, string part, string name) => string.Create(CultureInfo.InvariantCulture, $"{uid.Id:D10}-{part}-{name}");

    internal static (UniqueId Uid, string Part) Parse(string path)
    {
        var parts = path.Split('-', 3);
        if (parts.Length != 3 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid == 0
            || parts[1].Length == 0 || parts[1].Any(c => !char.IsAsciiDigit(c) && c != '.'))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Pièce jointe inconnue : {path}.");
        }

        return (new UniqueId(uid), parts[1]);
    }

    /// <summary>Pièce jointe décodée dans un fichier temporaire supprimé à la fermeture. Le message n'est pas marqué lu (BODY.PEEK).</summary>
    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var (uid, part) = Parse(path);
        var folder = await FolderAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        try
        {
            var summaries = await folder.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, ct).ConfigureAwait(false);
            var body = summaries.FirstOrDefault()?.BodyParts.FirstOrDefault(p => p.PartSpecifier == part)
                ?? throw new ConnectorException(ErrorKind.Permanent, $"Pièce jointe introuvable (message supprimé ?) : {path}.");
            // Contenu brut de la partie (BODY.PEEK[n]) puis décodage selon son Content-Transfer-Encoding : ne dépend pas
            // de la prise en charge de BODY[n.MIME], absente de certains serveurs.
            var encoding = body is BodyPartBasic basic && MimeUtils.TryParse(basic.ContentTransferEncoding, out ContentEncoding e) ? e : ContentEncoding.Default;
            var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                using (var raw = await folder.GetStreamAsync(uid, part, ct).ConfigureAwait(false))
                {
                    await new MimeContent(raw, encoding).DecodeToAsync(temp, ct).ConfigureAwait(false);
                }

                temp.Position = 0;
                return temp;
            }
            catch
            {
                await temp.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (MailCommon.Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Marque comme lus ou déplace les messages listés par la dernière lecture (et seulement eux).</summary>
    public async Task AcknowledgeAsync(CancellationToken ct)
    {
        if (_filter.AfterRead == "none" || _listed.Count == 0)
        {
            return;
        }

        var folder = await FolderAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        try
        {
            var uids = _listed.ToList();
            if (_filter.AfterRead == "seen")
            {
                await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, ct).ConfigureAwait(false);
            }
            else
            {
                var target = await _client!.GetFolderAsync(_filter.MoveTo!, ct).ConfigureAwait(false);
                await folder.MoveToAsync(uids, target, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (MailCommon.Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    private static ConnectorException ReadOnly() => new(ErrorKind.Permanent, "Une boîte IMAP est une source : elle ne s'écrit pas. Pour envoyer un fichier, utilisez une connexion SMTP.");

    public Task WriteAtomicAsync(string path, WriteMode mode, Func<Stream, CancellationToken, Task> write, CancellationToken ct) => throw ReadOnly();

    public async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var s = await OpenReadAsync(path, ct).ConfigureAwait(false);
            return true;
        }
        catch (ConnectorException ex) when (ex.Kind == ErrorKind.Permanent)
        {
            return false;
        }
    }

    public Task DeleteAsync(string path, CancellationToken ct) => throw ReadOnly();

    public Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct) => throw ReadOnly();

    public async ValueTask DisposeAsync()
    {
        if (_client is { IsConnected: true })
        {
            try
            {
                await _client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ImapProtocolException or ImapCommandException or OperationCanceledException or System.Net.Sockets.SocketException)
            {
            }
        }

        _client?.Dispose();
    }
}

/// <summary>
/// Envoi par SMTP : le fichier produit par le flux (CSV, XLSX…) part en pièce jointe d'un seul courriel par exécution,
/// vers les destinataires fixés dans la connexion. Le courriel n'est envoyé qu'une fois le fichier entièrement produit.
/// </summary>
internal sealed class SmtpTransport : IFileTransport
{
    private readonly ConnectorSettings _settings;
    private readonly IReadOnlyDictionary<string, string>? _options;
    private readonly string? _runId;

    public SmtpTransport(ConnectorSettings settings, IReadOnlyDictionary<string, string>? options, string? runId)
    {
        _settings = settings;
        _options = options;
        _runId = runId;
    }

    public int Sent { get; private set; }

    internal static List<MailboxAddress> Addresses(string? raw, string field)
    {
        var list = new List<MailboxAddress>();
        foreach (var item in (raw ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailboxAddress.TryParse(item, out var address) || address.Address.IndexOf('@', StringComparison.Ordinal) <= 0 || item.Any(char.IsControl))
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Adresse invalide dans « {field} » : {item}.");
            }

            list.Add(address);
        }

        return list;
    }

    private string? Option(string name) => _options is not null && _options.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    internal MimeMessage Build(string attachmentName, Stream content)
    {
        var from = Addresses(_settings.Require("from"), "from");
        var to = Addresses(_settings.Require("to"), "to");
        var cc = Addresses(_settings.Get("cc"), "cc");
        if (from.Count != 1 || to.Count == 0)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Un expéditeur et au moins un destinataire sont nécessaires.");
        }

        var now = DateTimeOffset.UtcNow;
        string Expand(string text) => new(FileConnector.ExpandTarget(text, _runId, now).Replace("{fichier}", attachmentName, StringComparison.Ordinal).Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        var subject = Expand(Option("subject") ?? _settings.Get("subject") ?? "ERP Connect : {fichier}").Replace('\r', ' ').Replace('\n', ' ');
        var text = Expand(Option("body") ?? _settings.Get("body") ?? "Fichier produit par ERP Connect (exécution {runId}) : {fichier}.");
        var message = new MimeMessage();
        message.From.AddRange(from);
        message.To.AddRange(to);
        message.Cc.AddRange(cc);
        message.Subject = subject;
        var builder = new BodyBuilder { TextBody = text };
        builder.Attachments.Add(attachmentName, content);
        message.Body = builder.ToMessageBody();
        message.Headers.Add("X-ErpConnect-Run", _runId ?? "manuel");
        return message;
    }

    public async Task WriteAtomicAsync(string path, WriteMode mode, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        var name = MailCommon.SafeFileName(SafePath.Normalize(path), "export.csv");
        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await using (temp.ConfigureAwait(false))
        {
            await write(temp, ct).ConfigureAwait(false);
            await temp.FlushAsync(ct).ConfigureAwait(false);
            temp.Position = 0;
            var message = Build(name, temp);
            using var client = new SmtpClient();
            await MailCommon.ConnectAsync(client, _settings, "starttls", 587, ct).ConfigureAwait(false);
            try
            {
                await client.SendAsync(message, ct).ConfigureAwait(false);
                Sent++;
                await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (MailCommon.Map(ex) is { } mapped)
            {
                throw mapped;
            }
        }
    }

    public async Task TestAsync(CancellationToken ct)
    {
        _ = Addresses(_settings.Require("from"), "from");
        _ = Addresses(_settings.Require("to"), "to");
        using var client = new SmtpClient();
        await MailCommon.ConnectAsync(client, _settings, "starttls", 587, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string pattern, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteFile>>([]);

    private static ConnectorException WriteOnly() => new(ErrorKind.Permanent, "Une connexion SMTP sert seulement à envoyer : pour lire des pièces jointes, utilisez une connexion IMAP.");

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => throw WriteOnly();

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(false);

    public Task DeleteAsync(string path, CancellationToken ct) => throw WriteOnly();

    public Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct) => throw WriteOnly();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Lire les pièces jointes d'une boîte IMAP (factures, relevés, commandes reçus par courriel).</summary>
public sealed class ImapConnector : FileConnector
{
    public override ConnectorManifest Manifest { get; } = new(
        "mail.imap", "Boîte e-mail (IMAP) : lire les pièces jointes", "1.0.0",
        ConnectorCapabilities.Read | ConnectorCapabilities.Discover | ConnectorCapabilities.FileTransport,
        [
            .. MailCommon.ServerFields("993", "ssl"),
            new("folder", "Dossier", ConfigFieldType.Text, Default: "INBOX"),
        ],
        "Chaque pièce jointe (CSV, XLSX, XML…) des courriels reçus devient un fichier à lire. Options de la source : from, subject, sinceDays, unseenOnly, afterRead (none, seen, move), moveTo.");

    public override IFileTransport CreateTransport(ConnectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ImapTransport(settings, ImapFilter.Create(settings, null));
    }

    public override async Task<ConnectionTestResult> TestAsync(ConnectorSettings settings, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var t = new ImapTransport(settings, ImapFilter.Create(settings, null));
            var count = await t.CountAsync(ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"Connexion réussie, {count} message(s) dans le dossier.", sw.Elapsed);
        }
        catch (ConnectorException ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    public override IRecordReader CreateReader(ConnectorSettings settings, ReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transport = new ImapTransport(settings, ImapFilter.Create(settings, request.Options));
        return new AcknowledgingReader(CreateFileReader(transport, request), transport);
    }

    private sealed class AcknowledgingReader(IRecordReader inner, ImapTransport transport) : IRecordReader, ISourceAcknowledgement
    {
        public IAsyncEnumerable<RecordBatch> ReadAsync(string? cursor, CancellationToken ct) => inner.ReadAsync(cursor, ct);

        public Task AcknowledgeAsync(CancellationToken ct) => transport.AcknowledgeAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>Envoyer le fichier produit par un flux en pièce jointe d'un courriel.</summary>
public sealed class SmtpConnector : FileConnector
{
    public override ConnectorManifest Manifest { get; } = new(
        "mail.smtp", "E-mail (SMTP) : envoyer un fichier", "1.0.0",
        ConnectorCapabilities.Write | ConnectorCapabilities.FileTransport,
        [
            .. MailCommon.ServerFields("587", "starttls"),
            new("from", "Expéditeur", ConfigFieldType.Text, Required: true),
            new("to", "Destinataires", ConfigFieldType.Text, Required: true, Help: "Adresses séparées par des virgules. Fixées dans la connexion : un flux ne peut pas en ajouter."),
            new("cc", "Copie", ConfigFieldType.Text),
            new("subject", "Objet par défaut", ConfigFieldType.Text, Default: "ERP Connect : {fichier}"),
            new("body", "Texte par défaut", ConfigFieldType.Text),
            new("sendWhenEmpty", "Envoyer même sans aucune ligne", ConfigFieldType.Boolean, Default: "false"),
        ],
        "Un courriel par exécution, avec le fichier produit (CSV, XLSX…) en pièce jointe. La ressource est le nom du fichier joint ({date}, {timestamp}, {runId}).");

    public override IFileTransport CreateTransport(ConnectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SmtpTransport(settings, null, null);
    }

    public override async Task<ConnectionTestResult> TestAsync(ConnectorSettings settings, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await new SmtpTransport(settings, null, null).TestAsync(ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, "Connexion et authentification SMTP réussies (aucun courriel envoyé).", sw.Elapsed);
        }
        catch (ConnectorException ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    public override IRecordWriter CreateWriter(ConnectorSettings settings, WriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        var inner = CreateFileWriter(new SmtpTransport(settings, request.Options, request.RunId), request);
        return new EmptyAwareWriter(inner, settings.GetBool("sendWhenEmpty"));
    }

    /// <summary>Sans aucune ligne écrite, rien n'est envoyé (sauf si la connexion le demande).</summary>
    private sealed class EmptyAwareWriter(IRecordWriter inner, bool sendWhenEmpty) : IRecordWriter
    {
        private long _records;

        public async Task<WriteResult> WriteBatchAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            var result = await inner.WriteBatchAsync(batch, ct).ConfigureAwait(false);
            _records += result.Outcomes.Count(o => o.Success);
            return result;
        }

        public Task CompleteAsync(CancellationToken ct) => _records == 0 && !sendWhenEmpty ? Task.CompletedTask : inner.CompleteAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
