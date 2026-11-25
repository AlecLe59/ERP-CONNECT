using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ErpConnect.Connectors.Formats;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors;

/// <summary>
/// Service web SOAP 1.1 ou 1.2 (anciens ERP, administrations, banques). Lecture : un appel dont la réponse contient des éléments
/// répétés (option recordElement), chacun devenant un enregistrement. Écriture : un appel par enregistrement, à partir d'un
/// modèle de corps où {champ} est remplacé par la valeur échappée. Mêmes protections que le connecteur REST (HTTPS, hôte unique,
/// nouvelles tentatives) ; XML lu sans DTD ni entité externe. Une faute SOAP « client » est permanente, « serveur » transitoire.
/// </summary>
public sealed partial class SoapConnector : IConnector
{
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string PasswordText = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public SoapConnector(Func<HttpMessageHandler>? handlerFactory = null) =>
        _handlerFactory = handlerFactory ?? (() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

    public ConnectorManifest Manifest { get; } = new(
        "soap", "Service web SOAP", "1.0.0",
        ConnectorCapabilities.Read | ConnectorCapabilities.Write,
        [
            new("baseUrl", "Adresse du service", ConfigFieldType.Url, Required: true),
            new("soapVersion", "Version SOAP", ConfigFieldType.Choice, Default: "1.1", Choices: ["1.1", "1.2"]),
            new("auth", "Authentification", ConfigFieldType.Choice, Default: "none", Choices: ["none", "basic", "bearer", "wsse", "oauth2"],
                Help: "wsse : jeton WS-Security (utilisateur et mot de passe dans l'enveloppe), fréquent sur les anciens services."),
            new("username", "Utilisateur (basic, wsse)", ConfigFieldType.Text),
            new("password", "Mot de passe (basic, wsse)", ConfigFieldType.Text, Secret: true),
            new("token", "Jeton (bearer)", ConfigFieldType.Text, Secret: true),
            new("tokenUrl", "URL du jeton (OAuth2)", ConfigFieldType.Url),
            new("clientId", "Identifiant client (OAuth2)", ConfigFieldType.Text),
            new("clientSecret", "Secret client (OAuth2)", ConfigFieldType.Text, Secret: true),
            new("scope", "Portée (OAuth2)", ConfigFieldType.Text),
            new("oauthClientAuth", "Envoi des identifiants (OAuth2)", ConfigFieldType.Choice, Default: "post", Choices: ["post", "basic"]),
            new("headers", "En-têtes HTTP supplémentaires", ConfigFieldType.Text, Help: "Une paire Nom: valeur par ligne (non secrète)."),
            new("timeoutSeconds", "Délai par requête (s)", ConfigFieldType.Number, Default: "60"),
            new("maxRetries", "Tentatives maximum", ConfigFieldType.Number, Default: "5"),
            new("maxRequestsPerSecond", "Requêtes par seconde (0 = illimité)", ConfigFieldType.Number, Default: "0"),
            new("allowHttp", "Autoriser HTTP non chiffré", ConfigFieldType.Boolean, Default: "false", Help: "Réservé aux tests et réseaux isolés."),
        ],
        "Appels SOAP 1.1 ou 1.2 : lecture des éléments répétés d'une réponse, écriture d'un appel par enregistrement.");

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    private RestConnector.Session Open(ConnectorSettings settings) => new(settings, _handlerFactory(), Delay, () => DateTimeOffset.UtcNow);

    public async Task<ConnectionTestResult> TestAsync(ConnectorSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sw = Stopwatch.StartNew();
        try
        {
            // Un service SOAP publie en général sa description à « ?wsdl » : on vérifie seulement qu'il répond.
            using var s = Open(settings);
            using var r = await s.SendAsync(HttpMethod.Get, "?wsdl", new RestConnector.Session.Payload(null, "text/xml", "*/*", []), ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"HTTP {(int)r.StatusCode} (description du service).", sw.Elapsed);
        }
        catch (ConnectorException ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    public Task<DataStructure> DiscoverAsync(ConnectorSettings settings, string? resource, CancellationToken ct) =>
        Task.FromResult(new DataStructure([], []));

    public IRecordReader CreateReader(ConnectorSettings settings, ReadRequest request) => new Reader(this, settings, request);

    /// <summary>« / » ou « . » désignent l'adresse du service elle-même (un flux exige une ressource non vide).</summary>
    internal static string Path(string resource) => resource.Trim() is "/" or "." ? string.Empty : resource.Trim();

    public IRecordWriter CreateWriter(ConnectorSettings settings, WriteRequest request) => new Writer(this, settings, request);

    /// <summary>Vérifie qu'un fragment de corps est du XML bien formé, sans DTD.</summary>
    internal static void CheckBody(string body)
    {
        try
        {
            var settings = XmlFormat.SecureSettings();
            settings.Async = false;
            settings.ConformanceLevel = ConformanceLevel.Fragment;
            using var r = XmlReader.Create(new StringReader(body), settings);
            while (r.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Corps SOAP invalide : {ex.Message}", ex);
        }
    }

    /// <summary>Enveloppe complète : en-tête WS-Security si demandé, puis le corps fourni.</summary>
    internal static string Envelope(ConnectorSettings settings, string body)
    {
        var ns = settings.Get("soapVersion") == "1.2" ? Soap12 : Soap11;
        var header = string.Empty;
        if (settings.Get("auth") == "wsse")
        {
            var user = SecurityElement.Escape(settings.Require("username"));
            var pwd = SecurityElement.Escape(settings.GetSecret("password") ?? throw new ConnectorException(ErrorKind.Permanent, "Mot de passe manquant."));
            header = $"<wsse:Security xmlns:wsse=\"{Wsse}\" soap:mustUnderstand=\"1\"><wsse:UsernameToken><wsse:Username>{user}</wsse:Username>" +
                     $"<wsse:Password Type=\"{PasswordText}\">{pwd}</wsse:Password></wsse:UsernameToken></wsse:Security>";
        }

        return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"{ns}\"><soap:Header>{header}</soap:Header><soap:Body>{body}</soap:Body></soap:Envelope>";
    }

    internal static RestConnector.Session.Payload Payload(ConnectorSettings settings, string body, string? action)
    {
        var v12 = settings.Get("soapVersion") == "1.2";
        if (action is not null && action.IndexOfAny(['"', '\r', '\n']) >= 0)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Action SOAP invalide.");
        }

        var contentType = v12
            ? "application/soap+xml; charset=utf-8" + (action is null ? string.Empty : $"; action=\"{action}\"")
            : "text/xml; charset=utf-8";
        IReadOnlyList<(string, string)> headers = !v12 ? [("SOAPAction", $"\"{action ?? string.Empty}\"")] : [];
        return new RestConnector.Session.Payload(Encoding.UTF8.GetBytes(Envelope(settings, body)), contentType, v12 ? "application/soap+xml" : "text/xml", headers, FaultAsync);
    }

    /// <summary>Faute SOAP : Client/Sender = erreur de la demande (permanente), Server/Receiver = panne du service (transitoire).</summary>
    internal static async Task<ConnectorException?> FaultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = XmlReader.Create(stream, XmlFormat.SecureSettings());
            var doc = await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
            var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is null)
            {
                return null;
            }

            var code = fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultcode" or "Value")?.Value ?? string.Empty;
            var text = fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultstring" or "Text")?.Value ?? string.Empty;
            var transient = code.EndsWith("Server", StringComparison.OrdinalIgnoreCase) || code.EndsWith("Receiver", StringComparison.OrdinalIgnoreCase);
            if (text.Length > 300)
            {
                text = text[..300];
            }

            return new ConnectorException(transient ? ErrorKind.Transient : ErrorKind.Permanent, $"Faute SOAP {code} : {text}");
        }
        catch (XmlException)
        {
            return null; // pas une réponse SOAP : classement par code HTTP
        }
    }

    private sealed class Reader(SoapConnector owner, ConnectorSettings settings, ReadRequest request) : IRecordReader
    {
        public async IAsyncEnumerable<RecordBatch> ReadAsync(string? cursor, [EnumeratorCancellation] CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (cursor == "fin")
            {
                yield break; // l'appel a déjà été lu en entier
            }

            var body = request.Option("body") ?? throw new ConnectorException(ErrorKind.Permanent, "Lecture SOAP : option body (corps de la requête) obligatoire.");
            var element = request.Option("recordElement") ?? throw new ConnectorException(ErrorKind.Permanent, "Lecture SOAP : option recordElement (élément répété) obligatoire.");
            CheckBody(body);
            using var session = owner.Open(settings);
            using var response = await session.SendAsync(HttpMethod.Post, Path(request.Resource), Payload(settings, body, request.Option("soapAction")), ct).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var batch = new List<Record>(request.BatchSize);
            await foreach (var rec in new XmlFormat().ReadAsync(stream, new Dictionary<string, string> { ["recordElement"] = element }, ct).ConfigureAwait(false))
            {
                batch.Add(rec);
                if (batch.Count == request.BatchSize)
                {
                    // Pas de reprise au milieu d'une réponse : un lot intermédiaire ne donne pas de point de reprise.
                    yield return new RecordBatch(batch, null);
                    batch = new List<Record>(request.BatchSize);
                }
            }

            yield return new RecordBatch(batch, "fin");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_.\-@]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>Remplace chaque {champ} du modèle par la valeur échappée pour XML ; un champ inconnu est une erreur de la ligne.</summary>
    internal static string Fill(string template, Record record) =>
        Placeholder().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (!record.Contains(name) && record.GetPath(name) is null)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Champ absent de l'enregistrement : {name}.");
            }

            var v = record.Contains(name) ? record[name] : record.GetPath(name);
            return SecurityElement.Escape(Record.ToInvariantString(v) ?? string.Empty);
        });

    private sealed class Writer : IRecordWriter
    {
        private readonly RestConnector.Session _session;
        private readonly ConnectorSettings _settings;
        private readonly WriteRequest _request;
        private readonly string _template;

        public Writer(SoapConnector owner, ConnectorSettings settings, WriteRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            _settings = settings;
            _request = request;
            _template = request.Option("bodyTemplate") ?? throw new ConnectorException(ErrorKind.Permanent, "Écriture SOAP : option bodyTemplate (modèle du corps) obligatoire.");
            CheckBody(Placeholder().Replace(_template, "x"));
            _session = owner.Open(settings);
        }

        public async Task<WriteResult> WriteBatchAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(batch);
            var outcomes = new RecordOutcome[batch.Count];
            for (var i = 0; i < batch.Count; i++)
            {
                try
                {
                    var body = Fill(_template, batch[i]);
                    using var _ = await _session.SendAsync(HttpMethod.Post, Path(_request.Resource), Payload(_settings, body, _request.Option("soapAction")), ct).ConfigureAwait(false);
                    outcomes[i] = new RecordOutcome(i, true);
                }
                catch (ConnectorException ex) when (ex.Kind == ErrorKind.Permanent)
                {
                    outcomes[i] = new RecordOutcome(i, false, ex.Message, ErrorKind.Permanent);
                }
            }

            return new WriteResult(outcomes);
        }

        public Task CompleteAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _session.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
