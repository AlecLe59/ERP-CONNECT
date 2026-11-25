using System.Globalization;
using System.Text;
using ErpConnect.Connectors.Tests;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;
using MimeKit;

namespace ErpConnect.Engine.Tests;

/// <summary>Flux complets par le moteur avec un vrai serveur de messagerie (GreenMail) : pièces jointes → CSV, CSV → courriel.</summary>
public sealed class MailFlowTests : IAsyncLifetime
{
    private Kernel _k = null!;

    public async Task InitializeAsync() => _k = await Kernel.StartAsync();

    public async Task DisposeAsync() => await _k.DisposeAsync();

    private static async Task<Dictionary<string, string?>> ServerAsync(string portName, string user, string security) => new()
    {
        ["host"] = MailTestServer.Host,
        ["port"] = MailTestServer.Port(portName).ToString(CultureInfo.InvariantCulture),
        ["security"] = security,
        ["certificateFingerprint"] = await MailTestServer.FingerprintAsync(MailTestServer.Port(portName)),
        ["username"] = MailTestServer.Address(user),
        ["timeoutSeconds"] = "15",
    };

    private Dictionary<string, string?> Password => new() { ["password"] = MailTestServer.Password };

    [MailFact]
    public async Task Attachments_to_csv_then_messages_moved_so_the_next_run_reads_nothing()
    {
        var subject = MailTestServer.Unique("factures");
        var done = MailTestServer.Unique("Traite");
        await MailTestServer.EnsureFolderAsync(done);
        await MailTestServer.SendAsync(subject + " A", "fournisseur", ("a.csv", "numero;montant\nFA-1;100\nFA-2;250\n"));
        await MailTestServer.SendAsync(subject + " B", "fournisseur", ("b.csv", "numero;montant\nFA-3;75\n"));
        await _k.Connections.SaveAsync(Kernel.Admin, "boite", "boite", "mail.imap", await ServerAsync("imapsPort", "compta", "ssl"), Password, [], 0, "t", default);
        var output = _k.Folder("out");
        await _k.LocalAsync("dst", output);
        OptionEntry[] source = [new("format", "csv"), new("delimiter", ";"), new("subject", subject), new("afterRead", "move"), new("moveTo", done)];
        FlowDefinition Def(string file) => new(new SourceSpec("boite", "*.csv", source), new TargetSpec("dst", file, "createNew", [new("format", "csv")]), new MappingSpec("identity", []), 100);

        // L'aperçu lit les pièces jointes mais ne déplace rien.
        var preview = await new PreviewService(_k.Connections).PreviewAsync(Def("apercu.csv"), 10, default);
        preview.Rows.Count.ShouldBe(3);
        (await MailTestServer.MessagesAsync(subject)).Count.ShouldBe(2);

        var run = await _k.FlowRunAsync(Def("factures.csv"));
        run.ShouldHaveStatus(RunStatus.Succeeded);
        run.WrittenCount.ShouldBe(3);
        (await File.ReadAllTextAsync(Path.Combine(output, "factures.csv"))).ShouldContain("FA-3");
        (await MailTestServer.MessagesAsync(subject)).ShouldBeEmpty();
        (await MailTestServer.MessagesAsync(subject, done)).Count.ShouldBe(2);

        var again = await _k.FlowRunAsync(Def("factures-2.csv"));
        again.ReadCount.ShouldBe(0);
    }

    [MailFact]
    public async Task Rejected_rows_leave_the_messages_in_place()
    {
        var subject = MailTestServer.Unique("rejet");
        var done = MailTestServer.Unique("Traite");
        await MailTestServer.EnsureFolderAsync(done);
        await MailTestServer.SendAsync(subject, "fournisseur", ("a.csv", "numero;montant\nFA-1;100\nFA-2;pas-un-nombre\n"));
        await _k.Connections.SaveAsync(Kernel.Admin, "boite", "boite", "mail.imap", await ServerAsync("imapsPort", "compta", "ssl"), Password, [], 0, "t", default);
        await _k.LocalAsync("dst", _k.Folder("out"));
        var run = await _k.FlowRunAsync(new FlowDefinition(
            new SourceSpec("boite", "*.csv", [new("format", "csv"), new("delimiter", ";"), new("subject", subject), new("afterRead", "move"), new("moveTo", done)]),
            new TargetSpec("dst", "f.csv", "createNew", [new("format", "csv")]),
            new MappingSpec("explicit", [new FieldMap("numero", "[numero]"), new FieldMap("montant", "abs([montant])")]), 100));
        run.RejectedCount.ShouldBe(1);
        (await MailTestServer.MessagesAsync(subject)).Count.ShouldBe(1); // échec : le courriel reste dans la boîte, il sera relu
    }

    [MailFact]
    public async Task Failure_to_move_messages_keeps_the_written_data_and_warns()
    {
        var subject = MailTestServer.Unique("sansdossier");
        await MailTestServer.SendAsync(subject, "fournisseur", ("a.csv", "numero;montant\nFA-1;100\n"));
        await _k.Connections.SaveAsync(Kernel.Admin, "boite", "boite", "mail.imap", await ServerAsync("imapsPort", "compta", "ssl"), Password, [], 0, "t", default);
        var output = _k.Folder("out");
        await _k.LocalAsync("dst", output);
        var run = await _k.FlowRunAsync(new FlowDefinition(
            new SourceSpec("boite", "*.csv", [new("format", "csv"), new("delimiter", ";"), new("subject", subject), new("afterRead", "move"), new("moveTo", "DossierAbsent")]),
            new TargetSpec("dst", "f.csv", "createNew", [new("format", "csv")]), new MappingSpec("identity", []), 100));
        run.ShouldHaveStatus(RunStatus.SucceededWithWarnings);
        File.Exists(Path.Combine(output, "f.csv")).ShouldBeTrue();
        (await MailTestServer.MessagesAsync(subject)).Count.ShouldBe(1);
    }

    [MailFact]
    public async Task Csv_to_email_with_attachment()
    {
        var subject = MailTestServer.Unique("export");
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "clients.csv"), 25);
        await _k.LocalAsync("src", input);
        var smtp = await ServerAsync("smtpsPort", "fournisseur", "ssl");
        smtp["from"] = MailTestServer.Address("fournisseur");
        smtp["to"] = MailTestServer.Address("compta");
        await _k.Connections.SaveAsync(Kernel.Admin, "mail", "mail", "mail.smtp", smtp, Password, [], 0, "t", default);
        var run = await _k.FlowRunAsync(new FlowDefinition(
            new SourceSpec("src", "*.csv", [new("format", "csv")]),
            new TargetSpec("mail", "clients-{date}.csv", "createNew", [new("format", "csv"), new("subject", subject + " ({fichier})")]),
            new MappingSpec("identity", []), 10));
        run.ShouldHaveStatus(RunStatus.Succeeded);
        var message = (await MailTestServer.MessagesAsync(subject)).Single().Message;
        var attachment = message.Attachments.OfType<MimePart>().Single();
        using var content = new MemoryStream();
        await attachment.Content!.DecodeToAsync(content);
        Encoding.UTF8.GetString(content.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(26);
    }
}
