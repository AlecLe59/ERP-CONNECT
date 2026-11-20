using System.Text;
using System.Text.Json.Nodes;
using ErpConnect.Contracts;
using ErpConnect.Core.Authorization;
using ErpConnect.Rpc.Dispatch;
using ErpConnect.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErpConnect.Rpc.Tests;

internal sealed class FakeAuthorizer : IRpcAuthorizer
{
    public Dictionary<string, HashSet<string>> BySid { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlySet<string>> GetPermissionsAsync(CallerIdentity caller, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(BySid.TryGetValue(caller.UserSid, out var p) ? p : []);

    public Task<IReadOnlySet<Role>> GetRolesAsync(CallerIdentity caller, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Role>>(new HashSet<Role>());
}

internal sealed class FakeAudit : IRpcAuditSink
{
    public List<(string Sid, string Method)> Denied { get; } = [];

    public Task DeniedAsync(CallerIdentity caller, string method, string correlationId, CancellationToken ct)
    {
        lock (Denied)
        {
            Denied.Add((caller.UserSid, method));
        }

        return Task.CompletedTask;
    }
}

public class DispatcherTests
{
    private readonly FakeAuthorizer _auth = new();
    private readonly FakeAudit _audit = new();
    private readonly RpcDispatcher _dispatcher;

    public DispatcherTests()
    {
        _auth.BySid[Identities.Admin.UserSid] = ["dashboard.read"];
        _auth.BySid[Identities.ForUser("2000").UserSid] = ["settings.read"];
        _dispatcher = new RpcDispatcher(_auth, _audit, NullLogger<RpcDispatcher>.Instance, "0.2.0");
        _dispatcher.Register<EmptyParams, VaultStatusResult>(RpcMethods.SystemGetStatus.Name == "x" ? null! : RpcMethods.VaultGetStatus,
            (_, _, _) => Task.FromResult(new VaultStatusResult(true, 1, 0, "test")));
        _dispatcher.Register<EmptyParams, SystemStatusResult>(RpcMethods.SystemGetStatus, (_, _, _) => throw new InvalidOperationException("détail interne C:\\secret\\path"));
    }

    private async Task<JsonNode> Send(RpcSession s, string json) =>
        JsonNode.Parse((await _dispatcher.HandleAsync(s, Encoding.UTF8.GetBytes(json), default))!)!;

    private async Task<RpcSession> HelloAsync(CallerIdentity who)
    {
        var s = new RpcSession(who);
        var r = await Send(s, """{"jsonrpc":"2.0","id":1,"method":"session.hello","params":{"clientName":"t","clientVersion":"1","protocolVersion":"1.0"}}""");
        r["result"].ShouldNotBeNull();
        return s;
    }

    [Fact]
    public async Task Hello_returns_identity_and_effective_permissions()
    {
        var s = new RpcSession(Identities.Admin);
        var r = await Send(s, """{"jsonrpc":"2.0","id":"a","method":"session.hello","params":{"clientName":"t","clientVersion":"1","protocolVersion":"1.6"}}""");
        r["id"]!.GetValue<string>().ShouldBe("a");
        r.Result()["userSid"]!.GetValue<string>().ShouldBe(Identities.Admin.UserSid);
        r.Result()["permissions"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldBe(["dashboard.read"]);
        s.HelloCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Calls_before_hello_are_unauthenticated()
    {
        var r = await Send(new RpcSession(Identities.Admin), """{"jsonrpc":"2.0","id":1,"method":"vault.getStatus"}""");
        r.ErrorCode().ShouldBe(RpcErrorCodes.Unauthenticated);
        r["error"]!["data"]!["code"]!.GetValue<string>().ShouldBe("UNAUTHENTICATED");
    }

    [Fact]
    public async Task Major_version_mismatch_is_refused()
    {
        var r = await Send(new RpcSession(Identities.Admin), """{"jsonrpc":"2.0","id":1,"method":"session.hello","params":{"clientName":"t","clientVersion":"1","protocolVersion":"2.0"}}""");
        r.ErrorCode().ShouldBe(RpcErrorCodes.ProtocolVersionMismatch);
    }

    [Fact]
    public async Task Account_without_any_role_cannot_open_a_session_and_is_audited()
    {
        var r = await Send(new RpcSession(Identities.NoRole), """{"jsonrpc":"2.0","id":1,"method":"session.hello","params":{"clientName":"t","clientVersion":"1","protocolVersion":"1.0"}}""");
        r.ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
        _audit.Denied.ShouldContain((Identities.NoRole.UserSid, "session.hello"));
    }

    [Fact]
    public async Task Missing_permission_is_forbidden_and_audited()
    {
        var s = await HelloAsync(Identities.Admin); // dashboard.read seulement
        var r = await Send(s, """{"jsonrpc":"2.0","id":2,"method":"vault.getStatus"}""");
        r.ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
        _audit.Denied.ShouldContain((Identities.Admin.UserSid, "vault.getStatus"));
    }

    [Fact]
    public async Task Permission_is_rechecked_on_every_call()
    {
        var who = Identities.ForUser("2000");
        var s = await HelloAsync(who);
        (await Send(s, """{"jsonrpc":"2.0","id":2,"method":"vault.getStatus"}"""))["result"].ShouldNotBeNull();
        _auth.BySid[who.UserSid] = ["dashboard.read"];
        (await Send(s, """{"jsonrpc":"2.0","id":3,"method":"vault.getStatus"}""")).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("not json", RpcErrorCodes.ParseError)]
    [InlineData("[]", RpcErrorCodes.InvalidRequest)]
    [InlineData("42", RpcErrorCodes.InvalidRequest)]
    [InlineData("""{"id":1,"method":"session.hello"}""", RpcErrorCodes.InvalidRequest)]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"session.hello"}""", RpcErrorCodes.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"session.hello"}""", RpcErrorCodes.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"session.hello","params":[1]}""", RpcErrorCodes.InvalidParams)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"session.hello","params":{"clientName":1}}""", RpcErrorCodes.InvalidParams)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"session.hello","params":{}}""", RpcErrorCodes.InvalidParams)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"../etc"}""", RpcErrorCodes.MethodNotFound)]
    public async Task Malformed_requests_get_clean_errors(string json, int code) =>
        (await Send(new RpcSession(Identities.Admin), json)).ErrorCode().ShouldBe(code);

    // S12 : 1 000 niveaux d'imbrication.
    [Fact]
    public async Task Deep_nesting_is_a_parse_error()
    {
        var deep = """{"jsonrpc":"2.0","id":1,"method":"session.hello","params":{"x":""" + new string('[', 1000) + new string(']', 1000) + "}}";
        (await Send(new RpcSession(Identities.Admin), deep)).ErrorCode().ShouldBe(RpcErrorCodes.ParseError);
    }

    [Fact]
    public async Task Unknown_method_after_hello()
    {
        var s = await HelloAsync(Identities.Admin);
        (await Send(s, """{"jsonrpc":"2.0","id":2,"method":"flows.list"}""")).ErrorCode().ShouldBe(RpcErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task Internal_errors_reveal_no_detail_but_carry_a_correlation_id()
    {
        var s = await HelloAsync(Identities.Admin);
        var r = await Send(s, """{"jsonrpc":"2.0","id":2,"method":"system.getStatus"}""");
        r.ErrorCode().ShouldBe(RpcErrorCodes.InternalError);
        r.ToJsonString().ShouldNotContain("secret");
        r.ToJsonString().ShouldNotContain("InvalidOperationException");
        r["error"]!["data"]!["correlationId"]!.GetValue<string>().ShouldStartWith(s.Id);
    }

    [Fact]
    public async Task Notifications_get_no_response()
    {
        var bytes = await _dispatcher.HandleAsync(new RpcSession(Identities.Admin), """{"jsonrpc":"2.0","method":"vault.getStatus"}"""u8.ToArray(), default);
        bytes.ShouldBeNull();
    }

    [Fact]
    public async Task Unavailable_engine_returns_engine_unavailable()
    {
        var s = await HelloAsync(Identities.ForUser("2000"));
        _dispatcher.Available = false;
        (await Send(s, """{"jsonrpc":"2.0","id":2,"method":"vault.getStatus"}""")).ErrorCode().ShouldBe(RpcErrorCodes.EngineUnavailable);
    }

    [Fact]
    public void Registering_with_wrong_types_is_refused() =>
        Should.Throw<InvalidOperationException>(() =>
            _dispatcher.Register<EmptyParams, SettingsResult>(RpcMethods.VaultGetStatus, (_, _, _) => Task.FromResult<SettingsResult>(null!)));
}
