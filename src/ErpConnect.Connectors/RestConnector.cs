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
            new("auth", "Authentification", ConfigFieldType.Choice, Default: "none", Choices: ["none", "basic", "bearer", "apiKey"]),
            new("username", "Utilisateur (basic)", ConfigFieldType.Text),
            new("password", "Mot de passe (basic)", ConfigFieldType.Text, Secret: true),
            new("token", "Jeton (bearer)", ConfigFieldType.Text, Secret: true),
            new("apiKeyHeader", "En-tête de clé d'API", ConfigFieldType.Text, Default: "X-API-Key"),
            new("apiKey", "Clé d'API", ConfigFieldType.Text, Secret: true),
            new("timeoutSeconds", "Délai par requête (s)", ConfigFieldType.Number, Default: "60"),
            new("maxRetries", "Tentatives maximum", ConfigFieldType.Number, Default: "5"),
            new("maxRequestsPerSecond", "Requêtes par seconde (0 = illimité)", ConfigFieldType.Number, Default: "0"),
            new("allowHttp", "Autoriser HTTP non chiffré", ConfigFieldType.Boolean, Default: "false", Help: "Réservé aux tests et réseaux isolés."),
            new("testPath", "Chemin de test", ConfigFieldType.Text, Default: "/"),
        ],
        "Lecture paginée et écriture JSON sur une API HTTP.");

    /// <summary>Délai d'attente de test (remplaçable).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    internal Session Open(ConnectorSettings settings) => new(settings, _handlerFactory(), Delay);

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

        public Session(ConnectorSettings settings, HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task> delay)
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
            _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.GetInt("timeoutSeconds", 60), 1, 3600)) };
            _retry = new RetryPolicy(Math.Clamp(settings.GetInt("maxRetries", 5), 1, 20), delay: delay);
            _limiter = new RequestRateLimiter(settings.GetInt("maxRequestsPerSecond", 0), delay: delay);
        }

        public int Requests { get; private set; }

        public Uri Resolve(string pathOrUrl)
        {
            var uri = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http", StringComparison.Ordinal)
                ? abs
                : new Uri(_base, pathOrUrl.TrimStart('/'));
            if (!string.Equals(uri.Host, _base.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != _base.Port || uri.Scheme != _base.Scheme)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Requête vers un autre hôte refusée : {uri.Host}.");
            }

            return uri;
        }

        private void Authenticate(HttpRequestMessage req)
        {
            switch (_settings.Get("auth") ?? "none")
            {
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
            _retry.ExecuteAsync(async (_, token) =>
            {
                await _limiter.WaitAsync(token).ConfigureAwait(false);
                using var req = new HttpRequestMessage(method, Resolve(path));
                Authenticate(req);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (idempotencyKey is not null)
                {
                    req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                }

                if (body is not null)
                {
                    req.Content = new ByteArrayContent(body);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                }

                HttpResponseMessage response;
                Requests++;
                try
                {
                    response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new ConnectorException(ErrorKind.Transient, $"Erreur réseau : {ex.Message}", ex);
                }
                catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
                {
                    throw new ConnectorException(ErrorKind.Transient, "Délai de réponse dépassé.", ex);
                }

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                using (response)
                {
                    var retryAfter = RetryAfter(response);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests && retryAfter is { } ra)
                    {
                        _limiter.PauseFor(ra);
                    }

                    var detail = await ReadSnippetAsync(response, token).ConfigureAwait(false);
                    var kind = IsTransient(response.StatusCode) ? ErrorKind.Transient : ErrorKind.Permanent;
                    throw new ConnectorException(kind, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detail}", retryAfter: retryAfter);
                }
            }, ct);

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
        }
    }

    private sealed class Reader : IRecordReader
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
            var next = string.IsNullOrEmpty(cursor) ? _request.Resource : cursor;
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
                next = string.IsNullOrEmpty(nextPath) ? null : Navigate(doc.RootElement, nextPath) is { ValueKind: JsonValueKind.String } n && n.GetString() is { Length: > 0 } s ? s : null;
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
    public static IReadOnlyList<IConnector> CreateDefault() => [new LocalFolderConnector(), new SftpConnector(), new FtpConnector(), new RestConnector(), new SqliteConnector()];
}
