using System.Xml.Linq;
using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

/// <summary>Connecteur SOAP contre un serveur HTTP local qui vérifie l'enveloppe reçue (pas une pile SOAP du marché).</summary>
public class SoapConnectorTests
{
    private const string S11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string S12 = "http://www.w3.org/2003/05/soap-envelope";

    private static ConnectorSettings Settings(TestHttpServer s, Dictionary<string, string?>? extra = null, Dictionary<string, string>? secrets = null)
    {
        var v = new Dictionary<string, string?> { ["baseUrl"] = s.BaseUrl + "ws/clients", ["allowHttp"] = "true", ["maxRetries"] = "3" };
        foreach (var (k, x) in extra ?? [])
        {
            v[k] = x;
        }

        return new ConnectorSettings(v, secrets);
    }

    private static string Envelope(string ns, string body) => $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"{ns}\"><s:Body>{body}</s:Body></s:Envelope>";

    private const string Clients =
        "<ListeClientsResponse xmlns=\"urn:erp\"><Client code=\"C1\"><Nom>Dupont &amp; fils</Nom><Adresse><Ville>Lille</Ville></Adresse></Client>" +
        "<Client code=\"C2\"><Nom>Martin</Nom></Client><Client code=\"C3\"><Nom>Éric</Nom></Client></ListeClientsResponse>";

    private static async Task<List<Record>> ReadAll(ConnectorSettings s, Dictionary<string, string> options)
    {
        var list = new List<Record>();
        await using var r = new SoapConnector { Delay = (_, _) => Task.CompletedTask }.CreateReader(s, new ReadRequest("/", 2, options));
        await foreach (var b in r.ReadAsync(null, CancellationToken.None))
        {
            list.AddRange(b.Records);
        }

        return list;
    }

    private static readonly Dictionary<string, string> ReadOptions = new()
    {
        ["soapAction"] = "urn:erp/ListeClients",
        ["body"] = "<ListeClients xmlns=\"urn:erp\"><Actifs>true</Actifs></ListeClients>",
        ["recordElement"] = "Client",
    };

    [Fact]
    public async Task Soap11_call_sends_a_valid_envelope_and_reads_repeated_elements()
    {
        string? action = null, contentType = null;
        XDocument? received = null;
        using var server = new TestHttpServer((req, body) =>
        {
            action = req.Headers["SOAPAction"];
            contentType = req.ContentType;
            received = XDocument.Parse(body);
            return (200, Envelope(S11, Clients), null);
        });
        var records = await ReadAll(Settings(server), ReadOptions);
        records.Select(r => r["@code"]).ShouldBe(["C1", "C2", "C3"]);
        records[0]["Nom"].ShouldBe("Dupont & fils");
        records[0].GetPath("Adresse.Ville").ShouldBe("Lille");
        action.ShouldBe("\"urn:erp/ListeClients\"");
        contentType.ShouldStartWith("text/xml");
        received!.Root!.Name.ShouldBe(XName.Get("Envelope", S11));
        received.Descendants(XName.Get("Actifs", "urn:erp")).Single().Value.ShouldBe("true");
        server.Requests.Single().Path.ShouldBe("/ws/clients");
    }

    [Fact]
    public async Task Soap12_puts_the_action_in_the_content_type()
    {
        string? contentType = null;
        string? ns = null;
        using var server = new TestHttpServer((req, body) =>
        {
            contentType = req.ContentType;
            ns = XDocument.Parse(body).Root!.Name.NamespaceName;
            return (200, Envelope(S12, Clients), null);
        });
        (await ReadAll(Settings(server, new() { ["soapVersion"] = "1.2" }), ReadOptions)).Count.ShouldBe(3);
        contentType!.ShouldContain("application/soap+xml");
        contentType!.ShouldContain("action=\"urn:erp/ListeClients\"");
        ns.ShouldBe(S12);
    }

    [Fact]
    public async Task Ws_security_header_carries_the_escaped_credentials()
    {
        XDocument? received = null;
        using var server = new TestHttpServer((_, body) =>
        {
            received = XDocument.Parse(body);
            return (200, Envelope(S11, Clients), null);
        });
        await ReadAll(Settings(server, new() { ["auth"] = "wsse", ["username"] = "compta" }, new() { ["password"] = "p<a&ss\"" }), ReadOptions);
        var wsse = XNamespace.Get("http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd");
        received!.Descendants(wsse + "Username").Single().Value.ShouldBe("compta");
        received.Descendants(wsse + "Password").Single().Value.ShouldBe("p<a&ss\"");
    }

    [Fact]
    public async Task Client_fault_is_permanent_and_not_retried()
    {
        using var server = new TestHttpServer((_, _) => (500, Envelope(S11, "<s:Fault><faultcode>s:Client</faultcode><faultstring>Société inconnue</faultstring></s:Fault>"), null));
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(Settings(server), ReadOptions));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
        ex.Message.ShouldContain("Société inconnue");
        server.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Server_fault_is_transient_and_retried()
    {
        using var server = new TestHttpServer((_, _) => (500, Envelope(S12, "<s:Fault><s:Code><s:Value>s:Receiver</s:Value></s:Code><s:Reason><s:Text>Base indisponible</s:Text></s:Reason></s:Fault>"), null));
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(Settings(server, new() { ["soapVersion"] = "1.2" }), ReadOptions));
        ex.Kind.ShouldBe(ErrorKind.Transient);
        server.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task A_response_with_a_dtd_is_refused()
    {
        using var server = new TestHttpServer((_, _) => (200, "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>", null));
        await Should.ThrowAsync<ConnectorException>(() => ReadAll(Settings(server), ReadOptions));
    }

    [Fact]
    public async Task Writing_fills_the_template_with_escaped_values_and_rejects_only_incomplete_records()
    {
        var bodies = new List<XDocument>();
        using var server = new TestHttpServer((_, body) =>
        {
            lock (bodies)
            {
                bodies.Add(XDocument.Parse(body));
            }

            return (200, Envelope(S11, "<CreerClientResponse xmlns=\"urn:erp\"><Ok>true</Ok></CreerClientResponse>"), null);
        });
        var options = new Dictionary<string, string>
        {
            ["soapAction"] = "urn:erp/CreerClient",
            ["bodyTemplate"] = "<CreerClient xmlns=\"urn:erp\"><Code>{code}</Code><Nom>{nom}</Nom></CreerClient>",
        };
        await using var w = new SoapConnector().CreateWriter(Settings(server), new WriteRequest("", Options: options));
        var result = await w.WriteBatchAsync(
        [
            Record.From(("code", "C1"), ("nom", "Dupont <&> \"fils\"")),
            Record.From(("code", "C2")),
            Record.From(("code", "C3"), ("nom", "</Nom><Admin>1</Admin>")),
        ], CancellationToken.None);
        result.Outcomes.Select(o => o.Success).ShouldBe([true, false, true]);
        result.Outcomes[1].Error!.ShouldContain("nom");
        bodies.Count.ShouldBe(2);
        var urn = XNamespace.Get("urn:erp");
        bodies[0].Descendants(urn + "Nom").Single().Value.ShouldBe("Dupont <&> \"fils\"");
        bodies[1].Descendants(urn + "Nom").Single().Value.ShouldBe("</Nom><Admin>1</Admin>", "une valeur ne peut pas injecter d'élément");
        bodies[1].Descendants(urn + "Admin").ShouldBeEmpty();
    }

    [Fact]
    public void A_malformed_body_template_is_refused() =>
        Should.Throw<ConnectorException>(() => new SoapConnector().CreateWriter(
            new ConnectorSettings(new Dictionary<string, string?> { ["baseUrl"] = "https://erp.example/ws" }),
            new WriteRequest("", Options: new Dictionary<string, string> { ["bodyTemplate"] = "<A><B>{x}</A>" })));
}
