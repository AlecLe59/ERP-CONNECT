using System.Text;
using ErpConnect.Connectors.Sdk;
using MimeKit;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

/// <summary>
/// Connecteurs e-mail contre un vrai serveur IMAP/SMTP (GreenMail). Chaque test isole ses messages par un objet unique.
/// Ce n'est ni Microsoft 365, ni Gmail, ni Exchange : leur comportement réel n'est pas couvert ici.
/// </summary>
public class MailConnectorTests
{
    private const string Csv = "code;libelle;montant\nF001;Fournitures;120,50\nF002;Transport;80,00\n";

    private static async Task<List<Record>> ReadAllAsync(ConnectorSettings settings, string pattern, Dictionary<string, string> options)
    {
        var all = new List<Record>();
        await using var reader = new ImapConnector().CreateReader(settings, new ReadRequest(pattern, 1000, options));
        await foreach (var b in reader.ReadAsync(null, CancellationToken.None))
        {
            all.AddRange(b.Records);
        }

        return all;
    }

    private static Dictionary<string, string> Options(string subject, params (string, string)[] extra)
    {
        var o = new Dictionary<string, string> { ["format"] = "csv", ["delimiter"] = ";", ["subject"] = subject };
        foreach (var (k, v) in extra)
        {
            o[k] = v;
        }

        return o;
    }

    [MailFact]
    public async Task Imap_reads_matching_attachments_without_marking_messages_read()
    {
        var subject = MailTestServer.Unique("factures");
        await MailTestServer.SendAsync(subject + " janvier", "fournisseur", ("janvier.csv", Csv), ("conditions.pdf", "%PDF-1.4 faux"));
        await MailTestServer.SendAsync(subject + " sans pièce jointe");
        await MailTestServer.SendAsync(subject + " février", "fournisseur", ("fevrier.csv", "code;libelle;montant\nF003;Loyer;900,00\n"));
        await MailTestServer.SendAsync(subject + " autre expéditeur", "compta", ("pirate.csv", "code;libelle;montant\nX;X;1\n"));
        var settings = await MailTestServer.ImapAsync();

        var structure = await new ImapConnector().DiscoverAsync(settings, "*", CancellationToken.None);
        structure.Resources.Count.ShouldBeGreaterThanOrEqualTo(4);

        var records = await ReadAllAsync(settings, "*.csv", Options(subject, ("from", MailTestServer.Address("fournisseur"))));
        records.Select(r => r["code"]).ShouldBe(["F001", "F002", "F003"]); // ordre d'arrivée, pdf ignoré, autre expéditeur exclu
        (await MailTestServer.MessagesAsync(subject)).ShouldAllBe(m => !m.Seen); // lecture seule : rien de marqué lu
    }

    [MailFact]
    public async Task Imap_acknowledge_moves_only_the_messages_that_were_read()
    {
        var subject = MailTestServer.Unique("releves");
        var folder = MailTestServer.Unique("Traite");
        await MailTestServer.EnsureFolderAsync(folder);
        await MailTestServer.SendAsync(subject + " 1", "fournisseur", ("r1.csv", Csv));
        await MailTestServer.SendAsync(subject + " 2 sans csv", "fournisseur", ("note.txt", "rien"));
        var settings = await MailTestServer.ImapAsync();
        var reader = new ImapConnector().CreateReader(settings, new ReadRequest("*.csv", 1000, Options(subject, ("afterRead", "move"), ("moveTo", folder))));
        await using (reader)
        {
            await foreach (var _ in reader.ReadAsync(null, CancellationToken.None))
            {
            }

            (await MailTestServer.MessagesAsync(subject)).Count.ShouldBe(2); // rien ne bouge avant l'accusé de traitement
            reader.ShouldBeAssignableTo<ISourceAcknowledgement>();
            await ((ISourceAcknowledgement)reader).AcknowledgeAsync(CancellationToken.None);
        }

        (await MailTestServer.MessagesAsync(subject)).Select(m => m.Subject).ShouldBe([subject + " 2 sans csv"]);
        (await MailTestServer.MessagesAsync(subject, folder)).Select(m => m.Subject).ShouldBe([subject + " 1"]);
        (await ReadAllAsync(settings, "*.csv", Options(subject))).ShouldBeEmpty();
    }

    [MailFact]
    public async Task Imap_acknowledge_can_mark_messages_read_and_unseenOnly_skips_them()
    {
        var subject = MailTestServer.Unique("commandes");
        await MailTestServer.SendAsync(subject, "fournisseur", ("c.csv", Csv));
        var settings = await MailTestServer.ImapAsync();
        var options = Options(subject, ("afterRead", "seen"), ("unseenOnly", "true"));
        var reader = new ImapConnector().CreateReader(settings, new ReadRequest("*.csv", 1000, options));
        await using (reader)
        {
            await foreach (var _ in reader.ReadAsync(null, CancellationToken.None))
            {
            }

            await ((ISourceAcknowledgement)reader).AcknowledgeAsync(CancellationToken.None);
        }

        (await MailTestServer.MessagesAsync(subject)).Single().Seen.ShouldBeTrue();
        (await ReadAllAsync(settings, "*.csv", options)).ShouldBeEmpty();
    }

    [MailFact]
    public async Task Imap_errors_are_classified()
    {
        var connector = new ImapConnector();
        var good = await MailTestServer.ImapAsync();
        (await connector.TestAsync(good, CancellationToken.None)).Success.ShouldBeTrue();

        var wrongPassword = await connector.TestAsync(await MailTestServer.ImapAsync(password: "mauvais"), CancellationToken.None);
        wrongPassword.Success.ShouldBeFalse();
        wrongPassword.Message.ShouldContain("Identifiants refusés");

        var unpinned = await connector.TestAsync(await MailTestServer.ImapAsync(extra: new() { ["certificateFingerprint"] = null }), CancellationToken.None);
        unpinned.Success.ShouldBeFalse(); // certificat auto-signé du serveur de test : refusé sans épinglage
        unpinned.Message.ShouldContain("Certificat du serveur refusé");

        var wrongPin = await connector.TestAsync(await MailTestServer.ImapAsync(extra: new() { ["certificateFingerprint"] = new string('A', 64) }), CancellationToken.None);
        wrongPin.Success.ShouldBeFalse();

        var plain = await connector.TestAsync(await MailTestServer.ImapAsync(extra: new() { ["security"] = "none", ["port"] = MailTestServer.Port("imapPort").ToString(System.Globalization.CultureInfo.InvariantCulture) }), CancellationToken.None);
        plain.Success.ShouldBeFalse();
        plain.Message.ShouldContain("non chiffrée refusée");

        var unreachable = await MailTestServer.ImapAsync(extra: new() { ["port"] = "1" });
        var ex = await Should.ThrowAsync<ConnectorException>(async () => await ReadAllAsync(unreachable, "*.csv", Options("x")));
        ex.Kind.ShouldBe(ErrorKind.Transient);

        var missingFolder = await MailTestServer.ImapAsync(extra: new() { ["folder"] = "DossierInexistant" });
        (await Should.ThrowAsync<ConnectorException>(async () => await ReadAllAsync(missingFolder, "*.csv", Options("x")))).Kind.ShouldBe(ErrorKind.Permanent);

        var write = Should.Throw<ConnectorException>(() => new ImapConnector().CreateTransport(good).WriteAtomicAsync("a.csv", WriteMode.CreateNew, (_, _) => Task.CompletedTask, CancellationToken.None));
        write.Message.ShouldContain("source");
    }

    [MailFact]
    public async Task Smtp_sends_one_message_with_the_generated_file_attached()
    {
        var subject = MailTestServer.Unique("export");
        var settings = await MailTestServer.SmtpAsync(new() { ["cc"] = MailTestServer.Address("fournisseur") });
        var writer = new SmtpConnector().CreateWriter(settings, new WriteRequest("clients-{date}.csv", WriteMode.CreateNew,
            new Dictionary<string, string> { ["format"] = "csv", ["subject"] = subject + " {fichier}\r\nBcc: espion@ailleurs.test" }, "run-42"));
        await using (writer)
        {
            await writer.WriteBatchAsync([new Record { ["code"] = "C1", ["nom"] = "Dupont & fils" }, new Record { ["code"] = "C2", ["nom"] = "Éric" }], CancellationToken.None);
            (await MailTestServer.MessagesAsync(subject)).ShouldBeEmpty(); // rien n'est envoyé avant la fin
            await writer.CompleteAsync(CancellationToken.None);
        }

        var message = (await MailTestServer.MessagesAsync(subject)).Single().Message;
        message.Subject.ShouldStartWith(subject + " clients-");
        message.Bcc.ShouldBeEmpty();
        message.Headers.Contains("Bcc").ShouldBeFalse(); // un objet ne peut pas injecter d'en-tête
        message.Cc.Mailboxes.Single().Address.ShouldBe(MailTestServer.Address("fournisseur"));
        message.Headers["X-ErpConnect-Run"].ShouldBe("run-42");
        var attachment = message.Attachments.OfType<MimePart>().Single();
        attachment.FileName!.ShouldMatch(@"^clients-\d{8}\.csv$");
        using var content = new MemoryStream();
        await attachment.Content!.DecodeToAsync(content);
        var text = Encoding.UTF8.GetString(content.ToArray());
        text.ShouldContain("Dupont & fils");
        text.ShouldContain("Éric");
    }

    [MailFact]
    public async Task Smtp_sends_nothing_without_records_or_when_aborted()
    {
        var subject = MailTestServer.Unique("vide");
        var settings = await MailTestServer.SmtpAsync(new() { ["subject"] = subject });
        await using (var empty = new SmtpConnector().CreateWriter(settings, new WriteRequest("vide.csv", WriteMode.CreateNew, new Dictionary<string, string> { ["format"] = "csv" }, "r1")))
        {
            await empty.CompleteAsync(CancellationToken.None);
        }

        await using (var aborted = new SmtpConnector().CreateWriter(settings, new WriteRequest("abandon.csv", WriteMode.CreateNew, new Dictionary<string, string> { ["format"] = "csv" }, "r2")))
        {
            await aborted.WriteBatchAsync([new Record { ["code"] = "C1" }], CancellationToken.None);
        }

        (await MailTestServer.MessagesAsync(subject)).ShouldBeEmpty();
    }

    [MailFact]
    public async Task Smtp_test_and_errors()
    {
        var connector = new SmtpConnector();
        (await connector.TestAsync(await MailTestServer.SmtpAsync(), CancellationToken.None)).Success.ShouldBeTrue();
        (await connector.TestAsync(await MailTestServer.SmtpAsync(new() { ["to"] = "pas-une-adresse" }), CancellationToken.None)).Message.ShouldContain("Adresse invalide");
        (await connector.TestAsync(await MailTestServer.SmtpAsync(new() { ["certificateFingerprint"] = null }), CancellationToken.None)).Message.ShouldContain("Certificat du serveur refusé");
        var unreachable = await MailTestServer.SmtpAsync(new() { ["port"] = "1" });
        var writer = connector.CreateWriter(unreachable, new WriteRequest("a.csv", WriteMode.CreateNew, new Dictionary<string, string> { ["format"] = "csv" }, "r"));
        await using (writer)
        {
            await writer.WriteBatchAsync([new Record { ["code"] = "C1" }], CancellationToken.None);
            (await Should.ThrowAsync<ConnectorException>(() => writer.CompleteAsync(CancellationToken.None))).Kind.ShouldBe(ErrorKind.Transient);
        }
    }

    [Fact]
    public void Addresses_and_file_names_are_checked()
    {
        SmtpTransport.Addresses("a@b.fr, Compta <c@d.fr>; e@f.fr", "to").Select(a => a.Address).ShouldBe(["a@b.fr", "c@d.fr", "e@f.fr"]);
        Should.Throw<ConnectorException>(() => SmtpTransport.Addresses("a@b.fr\r\nBcc: x@y.fr", "to"));
        Should.Throw<ConnectorException>(() => SmtpTransport.Addresses("sans-arobase", "to"));
        MailCommon.SafeFileName("..\\..\\windows\\system32\\evil.csv", "x").ShouldBe("evil.csv");
        MailCommon.SafeFileName("fact:ure?.csv", "x").ShouldBe("fact_ure_.csv");
        MailCommon.SafeFileName(null, "piece-2").ShouldBe("piece-2");
        ImapTransport.Parse(ImapTransport.FileName(new MailKit.UniqueId(42), "2.1", "a-b.csv")).ShouldBe((new MailKit.UniqueId(42), "2.1"));
        Should.Throw<ConnectorException>(() => ImapTransport.Parse("../../etc"));
        Should.Throw<ConnectorException>(() => ImapFilter.Create(new ConnectorSettings(new Dictionary<string, string?>()), new Dictionary<string, string> { ["afterRead"] = "move" }));
        Should.Throw<ConnectorException>(() => ImapFilter.Create(new ConnectorSettings(new Dictionary<string, string?>()), new Dictionary<string, string> { ["afterRead"] = "delete" }));
    }
}
