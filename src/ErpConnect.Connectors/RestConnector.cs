using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErpConnect.Connectors.Formats;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors;

/// <summary>
/// API REST JSON. Lecture paginée (lien « suivant » dans la réponse, compatible OData @odata.nextLink),
/// écriture enregistrement par enregistrement ou par lot, clé d'idempotence, 429 et Retry-After respectés.
/// HTTPS obligatoire sauf option explicite ; aucune requête n'est envoyée vers un autre hôte que celui configuré
/// (un lien « suivant » piégé ne peut pas exfiltrer le jeton).
/// </summary>
public sealed class RestConnector : IConnector
{
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public RestConnector(Func<HttpMessageHandler>? handlerFactory = null) =>
        _handlerFactory = handlerFactory ?? (() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

    public ConnectorManifest Manifest { get; } = new(
        "rest", "API REST (JSON)", "1.0.0",
        ConnectorCapabilities.Read | ConnectorCapabilities.Write | ConnectorCapabilities.Discover,
        [
            new("baseUrl", "URL de base", ConfigFieldType.Url, Required: true),
            new("auth", "Authentification", ConfigFieldType.Choice, Default: "none", Choices: ["none", "basic", "bearer", "apiKey", "oauth2"]),
            new("username", "Utilisateur (basic)", ConfigFieldType.Text),
            new("password", "Mot de passe (basic)", ConfigFieldType.Text, Secret: true),
            new("token", "Jeton (bearer)", ConfigFieldType.Text, Secret: true),
            new("apiKeyHeader", "En-tête de clé d'API", ConfigFieldType.Text, Default: "X-API-Key"),
            new("apiKey", "Clé d'API", ConfigFieldType.Text, Secret: true),
            new("tokenUrl", "URL du jeton (OAuth2)", ConfigFieldType.Url, Help: "Point d'accès qui délivre le jeton, par exemple https://login.exemple.com/oauth2/token."),
            new("clientId", "Identifiant client (OAuth2)", ConfigFieldType.Text),
            new("clientSecret", "Secret client (OAuth2)", ConfigFieldType.Text, Secret: true),
            new("scope", "Portée (OAuth2)", ConfigFieldType.Text, Help: "Facultatif, selon le fournisseur."),
            new("oauthClientAuth", "Envoi des identifiants (OAuth2)", ConfigFieldType.Choice, Default: "post", Choices: ["post", "basic"],
                Help: "post : dans le corps de la requête ; basic : en-tête Authorization. Selon le fournisseur."),
            new("headers", "En-têtes supplémentaires", ConfigFieldType.Text, Help: "Une paire Nom: valeur par ligne (non secrète), par exemple un identifiant de société."),
            new("timeoutSeconds", "Délai par requête (s)", ConfigFieldType.Number, Default: "60"),
            new("maxRetries", "Tentatives maximum", ConfigFieldType.Number, Default: "5"),
            new("maxRequestsPerSecond", "Requêtes par seconde (0 = illimité)", ConfigFieldType.Number, Default: "0"),
            new("allowHttp", "Autoriser HTTP non chiffré", ConfigFieldType.Boolean, Default: "false", Help: "Réservé aux tests et réseaux isolés."),
            new("testPath", "Chemin de test", ConfigFieldType.Text, Default: "/"),
        ],
        "Lecture paginée (lien suivant, page, décalage, curseur, en-tête Link) et écriture JSON sur une API HTTP ; authentification basique, jeton, clé d'API ou OAuth2.");

    /// <summary>Délai d'attente de test (remplaçable).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Horloge du limiteur de requêtes (remplaçable par les tests).</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    internal Session Open(ConnectorSettings settings) => new(settings, _handlerFactory(), Delay, Now);

    public async Task<ConnectionTestResult> TestAsync(ConnectorSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sw = Stopwatch.StartNew();
        try
        {
            using var s = Open(settings);
            using var response = await s.SendAsync(HttpMethod.Get, settings.Get("testPath") ?? "/", null, null, ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"HTTP {(int)response.StatusCode}.", sw.Elapsed);
        }
        catch (ConnectorException ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    public async Task<DataStructure> DiscoverAsync(ConnectorSettings settings, string? resource, CancellationToken ct)
    {
        await using var reader = CreateReader(settings, new ReadRequest(resource ?? "/", 50));
        await foreach (var batch in reader.ReadAsync(null, ct).ConfigureAwait(false))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in batch.Records)
            {
                foreach (var (k, v) in r)
                {
                    fields.TryAdd(k, FileConnector.TypeName(v));
                }
            }

            return new DataStructure([resource ?? "/"], fields.Select(f => new FieldInfo(f.Key, f.Value)).ToArray());
        }

        return new DataStructure([resource ?? "/"], []);
    }

    public IRecordReader CreateReader(ConnectorSettings settings, ReadRequest request) => new Reader(Open(settings), request);

    public IRecordWriter CreateWriter(ConnectorSettings settings, WriteRequest request) => new Writer(Open(settings), request);

    internal sealed class Session : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Uri _base;
        private readonly ConnectorSettings _settings;
        private readonly RetryPolicy _retry;
        private readonly RequestRateLimiter _limiter;
        private readonly Func<DateTimeOffset> _now;
        private readonly Uri _exact;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private string? _accessToken;
        private DateTimeOffset _tokenExpires;

        public Session(ConnectorSettings settings, HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task> delay, Func<DateTimeOffset> now)
        {
            _settings = settings;
            var raw = settings.Require("baseUrl");
            if (!Uri.TryCreate(raw.EndsWith('/') ? raw : raw + "/", UriKind.Absolute, out var b) || (b.Scheme != Uri.UriSchemeHttps && b.Scheme != Uri.UriSchemeHttp))
            {
                throw new ConnectorException(ErrorKind.Permanent, "URL de base invalide.");
            }

            if (b.Scheme == Uri.UriSchemeHttp && !settings.GetBool("allowHttp"))
            {
                throw new ConnectorException(ErrorKind.Permanent, "HTTP non chiffré refusé : utilisez HTTPS ou activez explicitement « Autoriser HTTP ».");
            }

            if (!string.IsNullOrEmpty(b.UserInfo))
            {
                throw new ConnectorException(ErrorKind.Permanent, "Identifiants interdits dans l'URL : utilisez les champs d'authentification.");
            }

            _base = b;
            _exact = new Uri(raw, UriKind.Absolute);
            _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.GetInt("timeoutSeconds", 60), 1, 3600)) };
            _retry = new RetryPolicy(Math.Clamp(settings.GetInt("maxRetries", 5), 1, 20), delay: delay);
            _limiter = new RequestRateLimiter(settings.GetInt("maxRequestsPerSecond", 0), now, delay);
            _now = now;
            ExtraHeaders = ParseHeaders(settings.Get("headers"));
        }

        public IReadOnlyList<(string Name, string Value)> ExtraHeaders { get; }

        public int TokenRequests { get; private set; }

        internal static IReadOnlyList<(string Name, string Value)> ParseHeaders(string? text)
        {
            var list = new List<(string, string)>();
            foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var i = line.IndexOf(':', StringComparison.Ordinal);
                var name = i > 0 ? line[..i].Trim() : string.Empty;
                if (name.Length == 0 || name.Any(c => c <= ' ' || c >= 127 || c == ':') || line.Contains('\r', StringComparison.Ordinal))
                {
                    throw new ConnectorException(ErrorKind.Permanent, $"En-tête invalide : « {line} » (attendu Nom: valeur).");
                }

                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConnectorException(ErrorKind.Permanent, $"L'en-tête {name} est un secret : utilisez les champs d'authentification.");
                }

                list.Add((name, line[(i + 1)..].Trim()));
            }

            return list;
        }

        /// <summary>
        /// Jeton OAuth2 « client credentials », gardé en mémoire jusqu'à une minute avant son expiration. L'URL du jeton peut être
        /// sur un autre hôte que l'API (fournisseur d'identité) : c'est le seul hôte supplémentaire admis, et il est configuré.
        /// </summary>
        private async Task<string> AccessTokenAsync(bool renew, CancellationToken ct)
        {
            await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!renew && _accessToken is not null && _now() < _tokenExpires)
                {
                    return _accessToken;
                }

                var raw = _settings.Require("tokenUrl");
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && _settings.GetBool("allowHttp"))))
                {
                    throw new ConnectorException(ErrorKind.Permanent, "URL du jeton invalide (HTTPS obligatoire).");
                }

                var clientId = _settings.Require("clientId");
                var secret = _settings.GetSecret("clientSecret") ?? throw new ConnectorException(ErrorKind.Permanent, "Secret client manquant.");
                var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
                if (_settings.Get("scope") is { } scope)
                {
                    form.Add(new("scope", scope));
                }

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                if (_settings.Get("oauthClientAuth") == "basic")
                {
                    var pair = Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(secret);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
                }
                else
                {
                    form.Add(new("client_id", clientId));
                    form.Add(new("client_secret", secret));
                }

                req.Content = new FormUrlEncodedContent(form);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                TokenRequests++;
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(req, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new ConnectorException(ErrorKind.Transient, $"Jeton OAuth2 : erreur réseau : {ex.Message}", ex);
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new ConnectorException(ErrorKind.Transient, "Jeton OAuth2 : délai de réponse dépassé.", ex);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // Le corps peut contenir des détails utiles (invalid_client…) mais jamais le secret : il n'est pas renvoyé par le serveur.
                        var detail = await ReadSnippetAsync(response, ct).ConfigureAwait(false);
                        throw new ConnectorException(IsTransient(response.StatusCode) ? ErrorKind.Transient : ErrorKind.Permanent,
                            $"Jeton OAuth2 refusé : HTTP {(int)response.StatusCode}{detail}");
                    }

                    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                    if (!doc.RootElement.TryGetProperty("access_token", out var tok) || tok.GetString() is not { Length: > 0 } token)
                    {
                        throw new ConnectorException(ErrorKind.Permanent, "Jeton OAuth2 : réponse sans access_token.");
                    }

                    var lifetime = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
                    _accessToken = token;
                    _tokenExpires = _now().AddSeconds(Math.Max(0, lifetime - 60));
                    return token;
                }
            }
            catch (JsonException ex)
            {
                throw new ConnectorException(ErrorKind.Permanent, "Jeton OAuth2 : réponse illisible.", ex);
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        public int Requests { get; private set; }

        public Uri Resolve(string pathOrUrl)
        {
            // Ressource vide ou simple requête (« ?wsdl ») : l'adresse configurée telle quelle, sans « / » final ajouté
            // (un point d'accès SOAP comme Service.asmx ne supporte pas « Service.asmx/ »).
            var uri = string.IsNullOrEmpty(pathOrUrl) ? _exact
                : pathOrUrl.StartsWith('?') ? new Uri(_exact.GetLeftPart(UriPartial.Path) + pathOrUrl)
                : Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http", StringComparison.Ordinal)
                ? abs
                : new Uri(_base, pathOrUrl.TrimStart('/'));
            if (!string.Equals(uri.Host, _base.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != _base.Port || uri.Scheme != _base.Scheme)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Requête vers un autre hôte refusée : {uri.Host}.");
            }

            return uri;
        }

        private async Task AuthenticateAsync(HttpRequestMessage req, bool renewToken, CancellationToken ct)
        {
            foreach (var (name, value) in ExtraHeaders)
            {
                req.Headers.TryAddWithoutValidation(name, value);
            }

            switch (_settings.Get("auth") ?? "none")
            {
                case "oauth2":
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(renewToken, ct).ConfigureAwait(false));
                    break;
                case "basic":
                    var raw = Encoding.UTF8.GetBytes($"{_settings.Require("username")}:{_settings.GetSecret("password")}");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
                    break;
                case "bearer":
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.GetSecret("token") ?? throw new ConnectorException(ErrorKind.Permanent, "Jeton manquant."));
                    break;
                case "apiKey":
                    req.Headers.TryAddWithoutValidation(_settings.Get("apiKeyHeader") ?? "X-API-Key", _settings.GetSecret("apiKey") ?? throw new ConnectorException(ErrorKind.Permanent, "Clé d'API manquante."));
                    break;
            }
        }

        /// <summary>Envoie avec nouvelles tentatives. Renvoie la réponse 2xx ; lève une ConnectorException classée sinon.</summary>
        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[]? body, string? idempotencyKey, CancellationToken ct) =>
            SendAsync(method, path, new Payload(body, "application/json", "application/json", idempotencyKey is null ? [] : [("Idempotency-Key", idempotencyKey)]), ct);

        /// <summary>
        /// Corps, type de contenu (avec ses paramètres), type accepté, en-têtes propres à la requête, et, au besoin, une lecture
        /// des réponses en erreur propre au protocole (faute SOAP) qui remplace le classement par code HTTP.
        /// </summary>
        public sealed record Payload(
            byte[]? Body,
            string ContentType,
            string Accept,
            IReadOnlyList<(string Name, string Value)> Headers,
            Func<HttpResponseMessage, CancellationToken, Task<ConnectorException?>>? OnError = null);

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Payload payload, CancellationToken ct) =>
            _retry.ExecuteAsync(async (_, token) =>
            {
                var response = await SendOnceAsync(method, path, payload, renewToken: false, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && _settings.Get("auth") == "oauth2")
                {
                    // Jeton révoqué ou expiré avant l'heure annoncée : un seul renouvellement, puis l'erreur est remontée.
                    response.Dispose();
                    response = await SendOnceAsync(method, path, payload, renewToken: true, token).ConfigureAwait(false);
                }

                return await CheckAsync(response, payload, token).ConfigureAwait(false);
            }, ct);

        private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, Payload payload, bool renewToken, CancellationToken token)
        {
            await _limiter.WaitAsync(token).ConfigureAwait(false);
            using var req = new HttpRequestMessage(method, Resolve(path));
            await AuthenticateAsync(req, renewToken, token).ConfigureAwait(false);
            req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(payload.Accept));
            foreach (var (name, value) in payload.Headers)
            {
                req.Headers.TryAddWithoutValidation(name, value);
            }

            if (payload.Body is not null)
            {
                req.Content = new ByteArrayContent(payload.Body);
                req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(payload.ContentType.Contains("charset", StringComparison.OrdinalIgnoreCase)
                    ? payload.ContentType
                    : payload.ContentType + "; charset=utf-8");
            }

            Requests++;
            try
            {
                return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ConnectorException(ErrorKind.Transient, $"Erreur réseau : {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new ConnectorException(ErrorKind.Transient, "Délai de réponse dépassé.", ex);
            }
        }

        private async Task<HttpResponseMessage> CheckAsync(HttpResponseMessage response, Payload payload, CancellationToken token)
        {
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (payload.OnError is not null && await payload.OnError(response, token).ConfigureAwait(false) is { } specific)
                {
                    throw specific;
                }

                var retryAfter = RetryAfter(response);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && retryAfter is { } ra)
                {
                    _limiter.PauseFor(ra);
                }

                var detail = await ReadSnippetAsync(response, token).ConfigureAwait(false);
                var kind = IsTransient(response.StatusCode) ? ErrorKind.Transient : ErrorKind.Permanent;
                throw new ConnectorException(kind, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detail}", retryAfter: retryAfter);
            }
        }

        public static bool IsTransient(HttpStatusCode code) =>
            code is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError;

        public static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var h = response.Headers.RetryAfter;
            if (h?.Delta is { } delta)
            {
                return delta;
            }

            if (h?.Date is { } date)
            {
                var d = date - DateTimeOffset.UtcNow;
                return d > TimeSpan.Zero ? d : TimeSpan.Zero;
            }

            return null;
        }

        private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
        {
            var buffer = new char[300];
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            var n = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            return n == 0 ? string.Empty : " : " + new string(buffer, 0, n).ReplaceLineEndings(" ");
        }

        public void Dispose()
        {
            _http.Dispose();
            _limiter.Dispose();
            _tokenLock.Dispose();
        }
    }

    internal sealed class Reader : IRecordReader
    {
        private const long MaxPageBytes = 64L * 1024 * 1024;
        private readonly Session _session;
        private readonly ReadRequest _request;

        public Reader(Session session, ReadRequest request)
        {
            _session = session;
            _request = request;
        }

        public async IAsyncEnumerable<RecordBatch> ReadAsync(string? cursor, [EnumeratorCancellation] CancellationToken ct)
        {
            var itemsPath = _request.Option("itemsPath");
            var nextPath = _request.Option("nextLinkPath");
            var mode = _request.Option("pagination") ?? (string.IsNullOrEmpty(nextPath) ? "none" : "nextLink");
            if (mode is not ("none" or "nextLink" or "linkHeader" or "page" or "offset" or "cursor"))
            {
                throw new ConnectorException(ErrorKind.Permanent, "Pagination : none, nextLink, linkHeader, page, offset ou cursor.");
            }

            var next = string.IsNullOrEmpty(cursor) ? FirstPage(mode, _request) : cursor;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (next is not null)
            {
                if (!seen.Add(next))
                {
                    throw new ConnectorException(ErrorKind.Permanent, "Pagination en boucle détectée.");
                }

                using var response = await _session.SendAsync(HttpMethod.Get, next, null, null, ct).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength > MaxPageBytes)
                {
                    throw new ConnectorException(ErrorKind.Permanent, "Page supérieure à 64 Mio.");
                }

                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 64 }, ct).ConfigureAwait(false);
                var items = Navigate(doc.RootElement, itemsPath);
                var records = items.ValueKind switch
                {
                    JsonValueKind.Array => items.EnumerateArray().Select((e, i) => JsonFormat.ToRecord(e, $"élément {i}")).ToList(),
                    JsonValueKind.Object => [JsonFormat.ToRecord(items, "réponse")],
                    _ => throw new ConnectorException(ErrorKind.Permanent, $"Aucun tableau d'éléments à « {itemsPath ?? "(racine)"} »."),
                };
                next = NextPage(mode, _request, next, doc.RootElement, response, records.Count);
                foreach (var chunk in records.Chunk(Math.Max(1, _request.BatchSize)))
                {
                    yield return new RecordBatch(chunk, next);
                }

                if (records.Count == 0)
                {
                    yield return new RecordBatch([], next);
                }
            }
        }

        private static int PageSize(ReadRequest r, int fallback) =>
            int.TryParse(r.Option("pageSize"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;

        /// <summary>Première page : ajoute les paramètres de pagination (page, taille, décalage) à la ressource.</summary>
        internal static string FirstPage(string mode, ReadRequest r)
        {
            var url = r.Resource;
            switch (mode)
            {
                case "page":
                    url = SetQuery(url, r.Option("pageParam") ?? "page", r.Option("pageStart") ?? "1");
                    if (r.Option("pageSizeParam") is { } sp)
                    {
                        url = SetQuery(url, sp, PageSize(r, 100).ToString(CultureInfo.InvariantCulture));
                    }

                    break;
                case "offset":
                    url = SetQuery(SetQuery(url, r.Option("offsetParam") ?? "offset", "0"), r.Option("limitParam") ?? "limit", PageSize(r, 100).ToString(CultureInfo.InvariantCulture));
                    break;
            }

            return url;
        }

        /// <summary>
        /// Page suivante, ou null en fin de lecture. L'adresse obtenue sert aussi de point de reprise. Une page incomplète
        /// (moins d'éléments que la taille demandée) ou vide termine la lecture en mode page et décalage.
        /// </summary>
        internal static string? NextPage(string mode, ReadRequest r, string current, JsonElement root, HttpResponseMessage response, int count)
        {
            switch (mode)
            {
                case "nextLink":
                    return Navigate(root, r.Option("nextLinkPath")) is { ValueKind: JsonValueKind.String } n && n.GetString() is { Length: > 0 } s ? s : null;
                case "linkHeader":
                    return LinkNext(response);
                case "page":
                {
                    var size = r.Option("pageSizeParam") is null && r.Option("pageSize") is null ? 0 : PageSize(r, 100);
                    if (count == 0 || (size > 0 && count < size))
                    {
                        return null;
                    }

                    var param = r.Option("pageParam") ?? "page";
                    var page = long.TryParse(GetQuery(current, param), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 1;
                    return SetQuery(current, param, (page + 1).ToString(CultureInfo.InvariantCulture));
                }

                case "offset":
                {
                    var limit = PageSize(r, 100);
                    if (count < limit)
                    {
                        return null;
                    }

                    var param = r.Option("offsetParam") ?? "offset";
                    var offset = long.TryParse(GetQuery(current, param), NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
                    return SetQuery(current, param, (offset + count).ToString(CultureInfo.InvariantCulture));
                }

                case "cursor":
                {
                    var path = r.Option("cursorPath") ?? throw new ConnectorException(ErrorKind.Permanent, "Pagination par curseur : option cursorPath obligatoire.");
                    var value = Navigate(root, path);
                    var token = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.Number => value.GetRawText(),
                        _ => null,
                    };
                    return string.IsNullOrEmpty(token) || count == 0 ? null : SetQuery(current, r.Option("cursorParam") ?? "cursor", token);
                }

                default:
                    return null;
            }
        }

        /// <summary>En-tête Link (RFC 8288) : adresse marquée rel="next".</summary>
        internal static string? LinkNext(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Link", out var values))
            {
                return null;
            }

            foreach (var part in string.Join(",", values).Split(','))
            {
                var segments = part.Split(';');
                var target = segments[0].Trim();
                if (target.StartsWith('<') && target.EndsWith('>')
                    && segments.Skip(1).Any(s => s.Trim().Replace(" ", string.Empty, StringComparison.Ordinal) is "rel=\"next\"" or "rel=next"))
                {
                    return target[1..^1];
                }
            }

            return null;
        }

        internal static string? GetQuery(string url, string name)
        {
            var q = url.IndexOf('?', StringComparison.Ordinal);
            if (q < 0)
            {
                return null;
            }

            foreach (var pair in url[(q + 1)..].Split('&'))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                if (string.Equals(key, name, StringComparison.Ordinal))
                {
                    return eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
            }

            return null;
        }

        /// <summary>Remplace ou ajoute un paramètre de requête, valeur encodée.</summary>
        internal static string SetQuery(string url, string name, string value)
        {
            var q = url.IndexOf('?', StringComparison.Ordinal);
            var path = q < 0 ? url : url[..q];
            var pairs = q < 0 ? [] : url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
            var encoded = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
            var i = pairs.FindIndex(p => string.Equals(Uri.UnescapeDataString(p.Split('=')[0]), name, StringComparison.Ordinal));
            if (i >= 0)
            {
                pairs[i] = encoded;
            }
            else
            {
                pairs.Add(encoded);
            }

            return path + "?" + string.Join("&", pairs);
        }

        private static JsonElement Navigate(JsonElement root, string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return root;
            }

            var current = root;
            foreach (var segment in path.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return default;
                }
            }

            return current;
        }

        public ValueTask DisposeAsync()
        {
            _session.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Writer : IRecordWriter
    {
        private readonly Session _session;
        private readonly WriteRequest _request;
        private readonly HttpMethod _method;
        private readonly bool _batchMode;

        public Writer(Session session, WriteRequest request)
        {
            _session = session;
            _request = request;
            _method = (request.Option("method") ?? "POST").ToUpperInvariant() switch
            {
                "POST" => HttpMethod.Post,
                "PUT" => HttpMethod.Put,
                "PATCH" => HttpMethod.Patch,
                var m => throw new ConnectorException(ErrorKind.Permanent, $"Méthode non prise en charge : {m}."),
            };
            _batchMode = string.Equals(request.Option("mode"), "batch", StringComparison.Ordinal);
        }

        public async Task<WriteResult> WriteBatchAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(batch);
            if (_batchMode)
            {
                var body = Serialize(w =>
                {
                    w.WriteStartArray();
                    foreach (var r in batch)
                    {
                        JsonFormat.WriteRecord(w, r);
                    }

                    w.WriteEndArray();
                });
                try
                {
                    using var _ = await _session.SendAsync(_method, Path(null), body, IdempotencyKey(body), ct).ConfigureAwait(false);
                    return WriteResult.AllSucceeded(batch.Count);
                }
                catch (ConnectorException ex) when (ex.Kind == ErrorKind.Permanent)
                {
                    return new WriteResult(batch.Select((_, i) => new RecordOutcome(i, false, ex.Message, ErrorKind.Permanent)).ToArray());
                }
            }

            var outcomes = new RecordOutcome[batch.Count];
            for (var i = 0; i < batch.Count; i++)
            {
                var body = Serialize(w => JsonFormat.WriteRecord(w, batch[i]));
                try
                {
                    using var _ = await _session.SendAsync(_method, Path(batch[i]), body, IdempotencyKey(body), ct).ConfigureAwait(false);
                    outcomes[i] = new RecordOutcome(i, true);
                }
                catch (ConnectorException ex)
                {
                    outcomes[i] = new RecordOutcome(i, false, ex.Message, ex.Kind);
                }
            }

            return new WriteResult(outcomes);
        }

        /// <summary>Le chemin peut contenir {champ} : remplacé par la valeur encodée (PUT /clients/{code}).</summary>
        private string Path(Record? record)
        {
            var path = _request.Resource;
            if (record is null || !path.Contains('{', StringComparison.Ordinal))
            {
                return path;
            }

            foreach (var (k, v) in record)
            {
                path = path.Replace("{" + k + "}", Uri.EscapeDataString(Record.ToInvariantString(v) ?? string.Empty), StringComparison.Ordinal);
            }

            return path;
        }

        /// <summary>Même contenu dans la même exécution = même clé : un renvoi après coupure n'est pas traité deux fois par une API qui la respecte.</summary>
        private string IdempotencyKey(byte[] body)
        {
            var hash = SHA256.HashData([.. Encoding.UTF8.GetBytes((_request.RunId ?? string.Empty) + "\n" + _request.Resource + "\n"), .. body]);
            return Convert.ToHexStringLower(hash.AsSpan(0, 16));
        }

        private static byte[] Serialize(Action<Utf8JsonWriter> write)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                write(w);
            }

            return ms.ToArray();
        }

        public Task CompleteAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _session.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public static class ConnectorRegistry
{
    public static IReadOnlyList<IConnector> CreateDefault() => [new LocalFolderConnector(), new SftpConnector(), new FtpConnector(), new RestConnector(), new SoapConnector(), new SqliteConnector(),
        new Sql.SqlConnector(new Sql.PostgreSqlDialect()), new Sql.SqlConnector(new Sql.SqlServerDialect()), new Sql.SqlConnector(new Sql.MySqlDialect()),
        new Sql.SqlConnector(new Sql.OracleDialect()), new Sql.SqlConnector(new Sql.OdbcDialect())];
}
