using System.Web;
using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

/// <summary>Chantier « API web complète » : pagination par page, décalage, curseur, en-tête Link ; OAuth2 ; en-têtes.</summary>
public class RestApiTests
{
    private static ConnectorSettings Settings(string baseUrl, Dictionary<string, string?>? extra = null, Dictionary<string, string>? secrets = null)
    {
        var values = new Dictionary<string, string?> { ["baseUrl"] = baseUrl, ["allowHttp"] = "true", ["maxRetries"] = "3" };
        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        return new ConnectorSettings(values, secrets);
    }

    private static async Task<(List<Record> Records, List<string?> Cursors)> ReadAll(ConnectorSettings s, ReadRequest r, string? cursor = null)
    {
        var records = new List<Record>();
        var cursors = new List<string?>();
        await using var reader = new RestConnector().CreateReader(s, r);
        await foreach (var b in reader.ReadAsync(cursor, CancellationToken.None))
        {
            records.AddRange(b.Records);
            cursors.Add(b.Cursor);
        }

        return (records, cursors);
    }

    /// <summary>Faux catalogue de 23 articles servi par tranches.</summary>
    private static string Items(int from, int count) =>
        "[" + string.Join(",", Enumerable.Range(from, Math.Max(0, Math.Min(count, 23 - from))).Select(i => $"{{\"id\":{i}}}")) + "]";

    private static Dictionary<string, string> Options(params (string, string)[] o) => o.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public async Task Page_number_pagination_stops_on_a_short_page_and_resumes_from_its_cursor()
    {
        using var server = new TestHttpServer((req, _) =>
        {
            var q = HttpUtility.ParseQueryString(req.Url!.Query);
            var page = int.Parse(q["p"]!, System.Globalization.CultureInfo.InvariantCulture);
            var size = int.Parse(q["per_page"]!, System.Globalization.CultureInfo.InvariantCulture);
            return (200, $"{{\"data\":{Items((page - 1) * size, size)}}}", null);
        });
        var request = new ReadRequest("/articles?actif=1", 100, Options(("pagination", "page"), ("pageParam", "p"), ("pageSizeParam", "per_page"), ("pageSize", "10"), ("itemsPath", "data")));
        var (records, cursors) = await ReadAll(Settings(server.BaseUrl), request);
        records.Select(r => (long)r["id"]!).ShouldBe(Enumerable.Range(0, 23).Select(i => (long)i));
        server.RequestCount.ShouldBe(3, "la 3e page n'a que 3 éléments : inutile d'en demander une 4e");
        server.Requests.First().Path.ShouldBe("/articles?actif=1&p=1&per_page=10");
        var (rest, _) = await ReadAll(Settings(server.BaseUrl), request, cursors[0]);
        rest.Select(r => (long)r["id"]!).ShouldBe(Enumerable.Range(10, 13).Select(i => (long)i));
    }

    [Fact]
    public async Task Page_pagination_without_size_stops_on_an_empty_page()
    {
        using var server = new TestHttpServer((req, _) =>
        {
            var page = int.Parse(HttpUtility.ParseQueryString(req.Url!.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
            return (200, Items((page - 1) * 10, 10), null);
        });
        var (records, _) = await ReadAll(Settings(server.BaseUrl), new ReadRequest("/a", 100, Options(("pagination", "page"))));
        records.Count.ShouldBe(23);
        server.RequestCount.ShouldBe(4);
    }

    [Fact]
    public async Task Offset_pagination_advances_by_the_number_of_items_received()
    {
        using var server = new TestHttpServer((req, _) =>
        {
            var q = HttpUtility.ParseQueryString(req.Url!.Query);
            return (200, $"{{\"results\":{Items(int.Parse(q["start"]!, System.Globalization.CultureInfo.InvariantCulture), int.Parse(q["count"]!, System.Globalization.CultureInfo.InvariantCulture))}}}", null);
        });
        var (records, _) = await ReadAll(Settings(server.BaseUrl),
            new ReadRequest("/a", 100, Options(("pagination", "offset"), ("offsetParam", "start"), ("limitParam", "count"), ("pageSize", "8"), ("itemsPath", "results"))));
        records.Select(r => (long)r["id"]!).ShouldBe(Enumerable.Range(0, 23).Select(i => (long)i));
        server.Requests.Select(r => r.Path).ShouldBe(["/a?start=0&count=8", "/a?start=8&count=8", "/a?start=16&count=8"]);
    }

    [Fact]
    public async Task Cursor_pagination_follows_the_token_until_it_disappears()
    {
        using var server = new TestHttpServer((req, _) => HttpUtility.ParseQueryString(req.Url!.Query)["after"] switch
        {
            null => (200, "{\"items\":[{\"id\":1}],\"meta\":{\"next\":\"c+2/=\"}}", null),
            "c+2/=" => (200, "{\"items\":[{\"id\":2}],\"meta\":{\"next\":\"c3\"}}", null),
            "c3" => (200, "{\"items\":[{\"id\":3}],\"meta\":{\"next\":null}}", null),
            _ => (400, "{}", null),
        });
        var (records, _) = await ReadAll(Settings(server.BaseUrl),
            new ReadRequest("/a", 100, Options(("pagination", "cursor"), ("cursorPath", "meta.next"), ("cursorParam", "after"), ("itemsPath", "items"))));
        records.Select(r => (long)r["id"]!).ShouldBe([1L, 2, 3]);
        server.Requests.ElementAt(1).Path.ShouldBe("/a?after=c%2B2%2F%3D", "le jeton est encodé dans l'adresse");
    }

    [Fact]
    public async Task Link_header_pagination_follows_rel_next_and_stays_on_the_configured_host()
    {
        TestHttpServer? server = null;
        server = new TestHttpServer((req, _) => req.Url!.Query switch
        {
            "" => (200, "[{\"id\":1}]", new() { ["Link"] = $"<{server!.BaseUrl}a?page=2>; rel=\"next\", <{server.BaseUrl}a?page=9>; rel=\"last\"" }),
            "?page=2" => (200, "[{\"id\":2}]", new() { ["Link"] = "<https://autre.example/vol>; rel=\"next\"" }),
            _ => (404, "{}", null),
        });
        using (server)
        {
            var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(Settings(server.BaseUrl), new ReadRequest("/a", 100, Options(("pagination", "linkHeader")))));
            ex.Message.ShouldContain("autre hôte");
            server.RequestCount.ShouldBe(2);
        }
    }

    [Fact]
    public async Task Unknown_pagination_mode_is_refused() =>
        await Should.ThrowAsync<ConnectorException>(() => ReadAll(Settings("http://127.0.0.1:1/"), new ReadRequest("/a", 10, Options(("pagination", "magie")))));

    private static TestHttpServer OAuthServer(Func<int> tokenIssued, Action onToken, string mode = "post", int expiresIn = 3600, Func<string?, bool>? accept = null) =>
        new((req, body) =>
        {
            if (req.Url!.AbsolutePath == "/oauth/token")
            {
                var form = HttpUtility.ParseQueryString(body);
                var ok = mode == "post"
                    ? form["client_id"] == "app" && form["client_secret"] == "s3cr3t!"
                    : req.Headers["Authorization"] == "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("app:s3cr3t%21"));
                if (form["grant_type"] != "client_credentials" || !ok)
                {
                    return (401, "{\"error\":\"invalid_client\"}", null);
                }

                onToken();
                return (200, $"{{\"access_token\":\"jeton-{tokenIssued()}\",\"token_type\":\"Bearer\",\"expires_in\":{expiresIn}}}", null);
            }

            var auth = req.Headers["Authorization"];
            return (accept ?? (a => a?.StartsWith("Bearer jeton-", StringComparison.Ordinal) == true))(auth)
                ? (200, "[{\"id\":1}]", null)
                : (401, "{}", null);
        });

    private static ConnectorSettings OAuth(TestHttpServer s, string mode = "post", string secret = "s3cr3t!") => Settings(s.BaseUrl,
        new() { ["auth"] = "oauth2", ["tokenUrl"] = s.BaseUrl + "oauth/token", ["clientId"] = "app", ["scope"] = "api.read", ["oauthClientAuth"] = mode },
        new() { ["clientSecret"] = secret });

    [Theory]
    [InlineData("post")]
    [InlineData("basic")]
    public async Task OAuth2_client_credentials_obtains_a_token_once_and_reuses_it(string mode)
    {
        var issued = 0;
        using var server = OAuthServer(() => issued, () => issued++, mode);
        await using var reader = new RestConnector().CreateReader(OAuth(server, mode), new ReadRequest("/a", 10));
        await foreach (var b in reader.ReadAsync(null, CancellationToken.None))
        {
            b.Records.Count.ShouldBe(1);
        }

        var w = new RestConnector().CreateWriter(OAuth(server, mode), new WriteRequest("/a", Options: new Dictionary<string, string> { ["mode"] = "batch" }));
        await w.WriteBatchAsync([Record.From(("id", 1L))], CancellationToken.None);
        await w.WriteBatchAsync([Record.From(("id", 2L))], CancellationToken.None);
        await w.DisposeAsync();
        issued.ShouldBe(2, "un jeton pour le lecteur, un pour l'écrivain, réutilisé sur ses deux lots");
        server.Requests.Where(r => r.Path == "/a").ShouldAllBe(r => r.Authorization!.StartsWith("Bearer jeton-", StringComparison.Ordinal));
        server.Requests.Where(r => r.Path == "/oauth/token").ShouldAllBe(r => r.Body.Contains("scope=api.read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OAuth2_renews_the_token_once_when_the_api_answers_401()
    {
        var issued = 0;
        using var server = OAuthServer(() => issued, () => issued++, accept: a => a == "Bearer jeton-2");
        var (records, _) = await ReadAll(OAuth(server), new ReadRequest("/a", 10));
        records.Count.ShouldBe(1);
        issued.ShouldBe(2, "le premier jeton est refusé : un renouvellement, puis succès");
    }

    [Fact]
    public async Task OAuth2_wrong_secret_is_permanent_and_the_secret_never_appears_in_the_error()
    {
        var issued = 0;
        using var server = OAuthServer(() => issued, () => issued++);
        var wrong = "faux-" + Guid.NewGuid().ToString("N"); // généré : aucun secret, même factice, dans le dépôt
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(OAuth(server, secret: wrong), new ReadRequest("/a", 10)));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
        ex.Message.ShouldContain("Jeton OAuth2 refusé");
        ex.Message.ShouldNotContain(wrong);
        server.Requests.Count(r => r.Path == "/oauth/token").ShouldBe(1, "erreur permanente : pas de nouvelle tentative");
    }

    [Fact]
    public async Task OAuth2_token_url_must_be_https()
    {
        var s = new ConnectorSettings(
            new Dictionary<string, string?> { ["baseUrl"] = "https://api.example/", ["auth"] = "oauth2", ["tokenUrl"] = "http://login.example/token", ["clientId"] = "a" },
            new Dictionary<string, string> { ["clientSecret"] = "x" });
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(s, new ReadRequest("/a", 10)));
        ex.Message.ShouldContain("HTTPS");
    }

    [Fact]
    public async Task Extra_headers_are_sent_and_secret_headers_are_refused()
    {
        string? tenant = null;
        using var server = new TestHttpServer((req, _) =>
        {
            tenant = req.Headers["X-Societe"];
            return (200, "[]", null);
        });
        await ReadAll(Settings(server.BaseUrl, new() { ["headers"] = "X-Societe: 042\nAccept-Language: fr-FR" }), new ReadRequest("/a", 10));
        tenant.ShouldBe("042");
        await Should.ThrowAsync<ConnectorException>(() => ReadAll(Settings(server.BaseUrl, new() { ["headers"] = "Authorization: Bearer x" }), new ReadRequest("/a", 10)));
        await Should.ThrowAsync<ConnectorException>(() => ReadAll(Settings(server.BaseUrl, new() { ["headers"] = "sans-deux-points" }), new ReadRequest("/a", 10)));
    }

    [Theory]
    [InlineData("/a", "page", "2", "/a?page=2")]
    [InlineData("/a?x=1&page=1", "page", "2", "/a?x=1&page=2")]
    [InlineData("/a?x=1", "q", "a b&c", "/a?x=1&q=a%20b%26c")]
    public void Query_parameters_are_replaced_or_added_and_encoded(string url, string name, string value, string expected) =>
        RestConnector.Reader.SetQuery(url, name, value).ShouldBe(expected);
}
