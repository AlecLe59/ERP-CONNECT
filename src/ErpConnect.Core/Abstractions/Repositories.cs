using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Settings;

namespace ErpConnect.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface ISettingsRepository
{
    Task<VersionedSettings> GetAsync(CancellationToken ct);

    /// <summary>Enregistre si <paramref name="expectedVersion"/> est la version actuelle ; sinon renvoie null (conflit).</summary>
    Task<VersionedSettings?> TryUpdateAsync(EngineSettings settings, long expectedVersion, string updatedBy, CancellationToken ct);
}

public interface IRoleAssignmentRepository
{
    Task<IReadOnlyList<RoleAssignment>> ListAsync(CancellationToken ct);

    Task<RoleAssignment> AddAsync(RoleAssignment assignment, CancellationToken ct);

    Task<bool> RemoveAsync(long id, CancellationToken ct);
}

public interface IAuditLog
{
    Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct);

    Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct);

    Task<AuditVerification> VerifyChainAsync(CancellationToken ct);
}

/// <summary>Ligne chiffrée du coffre, telle que stockée.</summary>
public sealed record SecretRecord(
    string Id,
    string Usage,
    int KeyVersion,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface ISecretStore
{
    Task<SecretRecord?> GetAsync(string id, CancellationToken ct);

    Task UpsertAsync(SecretRecord record, CancellationToken ct);

    Task<bool> DeleteAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<SecretRecord>> ListAllAsync(CancellationToken ct);

    /// <summary>Remplace toutes les lignes en une transaction (rotation de clé).</summary>
    Task ReplaceAllAsync(IReadOnlyList<SecretRecord> records, int newActiveKeyVersion, CancellationToken ct);

    Task<int?> GetActiveKeyVersionAsync(CancellationToken ct);

    Task RegisterKeyVersionAsync(int version, CancellationToken ct);
}

public interface ISecretVault
{
    bool IsInitialized { get; }

    int? ActiveKeyVersion { get; }

    Task StoreAsync(string id, string usage, ReadOnlyMemory<byte> plaintext, CancellationToken ct);

    /// <summary>Déchiffre un secret. Usage interne au service uniquement : jamais exposé par RPC.</summary>
    Task<byte[]?> ResolveAsync(string id, string usage, CancellationToken ct);

    Task<bool> DeleteAsync(string id, CancellationToken ct);

    Task RotateAsync(CancellationToken ct);
}

/// <summary>Recherche d'un compte ou groupe Windows local ou AD par son nom.</summary>
public interface IPrincipalResolver
{
    ResolvedPrincipal? Resolve(string name);
}

public sealed record ResolvedPrincipal(string Sid, string Name, PrincipalKind Kind);
