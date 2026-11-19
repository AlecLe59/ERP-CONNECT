using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

public class RestConnectorTests
{
    private static ConnectorSettings Settings(string baseUrl, Dictionary<string, string?>? extra = null, Dictionary<string, string>? secrets = null)
    {
        var values = new Dictionary<string, string?> { ["baseUrl"] = baseUrl, ["allowHttp"] = "true", ["maxRetries"] = "4" };
        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        return new ConnectorSettings(values, secrets);
    }

    private static async Task<List<Record>> ReadAll(RestConnector c, ConnectorSettings s, ReadRequest r)
    {
        var list = new List<Record>();
        await using var reader = c.CreateReader(s, r);
        await foreach (var b in reader.ReadAsync(null, CancellationToken.None))
        {
            list.AddRange(b.Records);
        }

        return list;
    }

    [Fact]
    public async Task Follows_next_links_until_the_last_page()
    {
        using var server = new TestHttpServer((req, _) => req.Url!.Query switch
        {
            "" => (200, "{\"value\":[{\"id\":1},{\"id\":2}],\"@odata\":{\"nextLink\":\"/clients?page=2\"}}", null),
            "?page=2" => (200, "{\"value\":[{\"id\":3}]}", null),
            _ => (404, "{}", null),
        });
        var records = await ReadAll(new RestConnector(), Settings(server.BaseUrl), new ReadRequest("/clients", 10, new Dictionary<string, string> { ["itemsPath"] = "value", ["nextLinkPath"] = "@odata.nextLink" }));
        records.Select(r => r["id"]).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public async Task Next_link_to_another_host_is_refused()
    {
        using var server = new TestHttpServer((_, _) => (200, "{\"items\":[{\"id\":1}],\"next\":\"https://evil.example/steal\"}", null));
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(new RestConnector(), Settings(server.BaseUrl, secrets: new() { ["token"] = "t" }),
            new ReadRequest("/x", 10, new Dictionary<string, string> { ["itemsPath"] = "items", ["nextLinkPath"] = "next" })));
        ex.Message.ShouldContain("autre hôte");
        server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Pagination_loop_is_detected()
    {
        using var server = new TestHttpServer((_, _) => (200, "{\"items\":[],\"next\":\"/same\"}", null));
        await Should.ThrowAsync<ConnectorException>(() => ReadAll(new RestConnector(), Settings(server.BaseUrl), new ReadRequest("/same", 10, new Dictionary<string, string> { ["itemsPath"] = "items", ["nextLinkPath"] = "next" })));
    }

    [Fact]
    public async Task Http_without_tls_is_refused_unless_explicitly_allowed()
    {
        var s = new ConnectorSettings(new Dictionary<string, string?> { ["baseUrl"] = "http://127.0.0.1:1/" });
        (await new RestConnector().TestAsync(s, CancellationToken.None)).Message.ShouldContain("HTTP non chiffré refusé");
        var withCreds = new ConnectorSettings(new Dictionary<string, string?> { ["baseUrl"] = "https://u:p@h/" });
        (await new RestConnector().TestAsync(withCreds, CancellationToken.None)).Message.ShouldContain("Identifiants interdits");
    }

    [Fact]
    public async Task Too_many_requests_waits_for_retry_after_then_succeeds()
    {
        var calls = 0;
        using var server = new TestHttpServer((_, _) => Interlocked.Increment(ref calls) <= 2
            ? (429, "{}", new Dictionary<string, string> { ["Retry-After"] = "7" })
            : (200, "[{\"id\":1}]", null));
        var delay = new RecordingDelay();
        var records = await ReadAll(new RestConnector { Delay = delay.Delay }, Settings(server.BaseUrl), new ReadRequest("/x"));
        records.Count.ShouldBe(1);
        calls.ShouldBe(3);
        delay.Waits.Count(w => w >= TimeSpan.FromSeconds(7)).ShouldBeGreaterThanOrEqualTo(2, "Retry-After doit être respecté");
    }

    [Fact]
    public async Task Server_errors_are_retried_then_reported_as_transient()
    {
        using var server = new TestHttpServer((_, _) => (503, "indisponible", null));
        var delay = new RecordingDelay();
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(new RestConnector { Delay = delay.Delay }, Settings(server.BaseUrl), new ReadRequest("/x")));
        ex.Kind.ShouldBe(ErrorKind.Transient);
        server.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Client_errors_are_permanent_and_not_retried()
    {
        using var server = new TestHttpServer((_, _) => (400, "{\"error\":\"champ manquant\"}", null));
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(new RestConnector(), Settings(server.BaseUrl), new ReadRequest("/x")));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
        ex.Message.ShouldContain("champ manquant");
        server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Writer_reports_each_record_and_sends_stable_idempotency_keys()
    {
        using var server = new TestHttpServer((_, body) => body.Contains("\"bad\"", StringComparison.Ordinal) ? (422, "{\"error\":\"refusé\"}", null) : (201, "{}", null));
        var c = new RestConnector();
        var batch = new[] { Record.From(("code", "ok1")), Record.From(("code", "bad")), Record.From(("code", "ok2")) };
        var req = new WriteRequest("/clients/{code}", Options: new Dictionary<string, string> { ["method"] = "PUT" }, RunId: "R1");
        WriteResult result;
        await using (var w = c.CreateWriter(Settings(server.BaseUrl), req))
        {
            result = await w.WriteBatchAsync(batch, CancellationToken.None);
        }

        result.Outcomes.Select(o => o.Success).ShouldBe([true, false, true]);
        result.Outcomes[1].Kind.ShouldBe(ErrorKind.Permanent);
        result.Outcomes[1].Error!.ShouldContain("422");
        server.Requests.Select(r => r.Path).ShouldBe(["/clients/ok1", "/clients/bad", "/clients/ok2"], ignoreOrder: true);
        var keys = server.Requests.Select(r => r.IdempotencyKey).ToList();
        keys.ShouldAllBe(k => k != null && k.Length == 32);
        keys.Distinct().Count().ShouldBe(3);

        await using (var w = c.CreateWriter(Settings(server.BaseUrl), req))
        {
            await w.WriteBatchAsync([batch[0]], CancellationToken.None);
        }

        server.Requests.Last().IdempotencyKey.ShouldBe(server.Requests.First(r => r.Path == "/clients/ok1").IdempotencyKey, "même contenu, même exécution : même clé");
    }

    [Fact]
    public async Task Batch_mode_posts_an_array()
    {
        using var server = new TestHttpServer((_, _) => (200, "{}", null));
        await using (var w = new RestConnector().CreateWriter(Settings(server.BaseUrl), new WriteRequest("/import", Options: new Dictionary<string, string> { ["mode"] = "batch" })))
        {
            (await w.WriteBatchAsync(Samples.Tricky(), CancellationToken.None)).Succeeded.ShouldBe(4);
        }

        var body = server.Requests.Single().Body;
        JsonDocument.Parse(body).RootElement.GetArrayLength().ShouldBe(4);
    }

    [Theory]
    [InlineData("basic", "Basic dTpwd2Q=")]
    [InlineData("bearer", "Bearer jeton")]
    public async Task Authentication_headers_come_from_secrets(string auth, string expected)
    {
        using var server = new TestHttpServer((_, _) => (200, "[]", null));
        var s = Settings(server.BaseUrl, new() { ["auth"] = auth, ["username"] = "u" }, new() { ["password"] = "pwd", ["token"] = "jeton" });
        (await new RestConnector().TestAsync(s, CancellationToken.None)).Success.ShouldBeTrue();
        server.Requests.Single().Authorization.ShouldBe(expected);
    }

    [Fact]
    public async Task Rate_limit_spaces_requests()
    {
        using var server = new TestHttpServer((_, _) => (200, "{}", null));
        var delay = new RecordingDelay();
        await using (var w = new RestConnector { Delay = delay.Delay }.CreateWriter(Settings(server.BaseUrl, new() { ["maxRequestsPerSecond"] = "2" }), new WriteRequest("/x")))
        {
            await w.WriteBatchAsync([Record.From(("a", 1L)), Record.From(("a", 2L)), Record.From(("a", 3L))], CancellationToken.None);
        }

        delay.Waits.Count.ShouldBe(2);
        // Le délai simulé ne fait pas avancer l'horloge : la 2e attente cumule les deux intervalles.
        delay.Waits[0].ShouldBeInRange(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
        delay.Waits[1].ShouldBeInRange(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public void Retry_policy_uses_bounded_exponential_jitter()
    {
        var p = new RetryPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), random: () => 1.0);
        p.DelayFor(1, null).ShouldBe(TimeSpan.FromSeconds(1));
        p.DelayFor(3, null).ShouldBe(TimeSpan.FromSeconds(4));
        p.DelayFor(10, null).ShouldBe(TimeSpan.FromSeconds(10));
        p.DelayFor(1, TimeSpan.FromSeconds(30)).ShouldBe(TimeSpan.FromSeconds(30));
        new RetryPolicy(random: () => 0.0).DelayFor(4, null).ShouldBe(TimeSpan.Zero);
    }
}
