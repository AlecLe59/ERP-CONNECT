namespace ErpConnect.Contracts;

#pragma warning disable CA1819 // Forme sérialisée.

/// <summary>passphrase : facultative ; si fournie (12 caractères minimum), les clés maîtres sont incluses, chiffrées (Argon2id + AES-GCM), pour restaurer les secrets sur une autre machine.</summary>
public sealed record BackupCreateParams([property: Secret] string? Passphrase);

public sealed record BackupDto(string FileName, long Size, DateTimeOffset CreatedAt, bool IncludesKeys);

public sealed record BackupsListResult(BackupDto[] Backups, string Folder);
