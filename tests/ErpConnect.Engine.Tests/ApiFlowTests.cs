using System.Web;
using System.Xml.Linq;
using ErpConnect.Connectors.Tests;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;

namespace ErpConnect.Engine.Tests;

/// <summary>Flux complets par le moteur : API OAuth2 paginée vers CSV, puis CSV vers un service SOAP.</summary>
public sealed class ApiFlowTests : IAsyncLifetime
{
    private Kernel _k = null!;

    public async Task InitializeAsync() => _k = await Kernel.StartAsync();

    public async Task DisposeAsync() => await _k.DisposeAsync();

    [Fact]
    public async Task OAuth2_paginated_api_to_csv()
    {
        var tokens = 0;
        using var api = new TestHttpServer((req, body) =>
        {
            if (req.Url!.AbsolutePath == "/token")
            {
                Interlocked.Increment(ref tokens);
                return HttpUtility.ParseQueryString(body)["client_secret"] == "secret-de-test"
                    ? (200, "{\"access_token\":\"abc\",\"expires_in\":3600}", null)
                    : (401, "{}", null);
            }

            if (req.Headers["Authorization"] != "Bearer abc")
            {
                return (401, "{}", null);
            }

            var offset = int.Parse(HttpUtility.ParseQueryString(req.Url.Query)["offset"]!, System.Globalization.CultureInfo.InvariantCulture);
            var items = Enumerable.Range(offset, Math.Max(0, Math.Min(50, 120 - offset))).Select(i => $"{{\"id\":{i},\"nom\":\"Article {i}\"}}");
            return (200, $"{{\"items\":[{string.Join(",", items)}]}}", null);
        });
        var output = _k.Folder("out");
        await _k.LocalAsync("dst", output);
        await _k.Connections.SaveAsync(Kernel.Admin, "api", "api", "rest",
            new Dictionary<string, string?> { ["baseUrl"] = api.BaseUrl, ["allowHttp"] = "true", ["auth"] = "oauth2", ["tokenUrl"] = api.BaseUrl + "token", ["clientId"] = "erp" },
            new Dictionary<string, string?> { ["clientSecret"] = "secret-de-test" }, [], 0, "t", default);
        var run = await _k.FlowRunAsync(new FlowDefinition(
            new SourceSpec("api", "/articles", [new("pagination", "offset"), new("pageSize", "50"), new("itemsPath", "items")]),
            new TargetSpec("dst", "articles.csv", "createNew", [new("format", "csv")]),
            new MappingSpec("identity", []), 40));
        run.ShouldHaveStatus(RunStatus.Succeeded);
        run.WrittenCount.ShouldBe(120);
        (await File.ReadAllLinesAsync(Path.Combine(output, "articles.csv"))).Length.ShouldBe(121);
        tokens.ShouldBe(1);
    }

    [Fact]
    public async Task Csv_to_soap_service_one_call_per_record()
    {
        var received = new List<string>();
        using var ws = new TestHttpServer((_, body) =>
        {
            var code = XDocument.Parse(body).Descendants(XName.Get("Code", "urn:erp")).Single().Value;
            lock (received)
            {
                received.Add(code);
            }

            return code == "7"
                ? (500, "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Code refusé</faultstring></s:Fault></s:Body></s:Envelope>", null)
                : (200, "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><OkResponse xmlns=\"urn:erp\"/></s:Body></s:Envelope>", null);
        });
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 20);
        await _k.LocalAsync("src", input);
        await _k.Connections.SaveAsync(Kernel.Admin, "ws", "ws", "soap",
            new Dictionary<string, string?> { ["baseUrl"] = ws.BaseUrl + "Service.asmx", ["allowHttp"] = "true" }, new Dictionary<string, string?>(), [], 0, "t", default);
        var run = await _k.FlowRunAsync(new FlowDefinition(
            new SourceSpec("src", "*.csv", [new("format", "csv")]),
            new TargetSpec("ws", "/", "createNew", [new("soapAction", "urn:erp/Creer"), new("bodyTemplate", "<Creer xmlns=\"urn:erp\"><Code>{id}</Code><Nom>{nom}</Nom></Creer>")]),
            new MappingSpec("identity", []), 5));
        run.WrittenCount.ShouldBe(19);
        run.RejectedCount.ShouldBe(1);
        (await _k.Runs.RejectsAsync(run.Id, 1, default)).Single().Reason.ShouldContain("Code refusé");
        received.Count.ShouldBe(20);
        ws.Requests.ShouldAllBe(r => r.Path == "/Service.asmx");
    }
}
