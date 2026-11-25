namespace ErpConnect.Contracts;

/// <summary>Essai d'un script de transformation sur un enregistrement d'exemple (JSON objet), sans connexion ni écriture.</summary>
public sealed record ScriptTestParams(string Code, string RecordJson);

/// <summary>fate : output | filtered | rejected | stopped (limite de temps, mémoire, instructions ou récursion atteinte).</summary>
public sealed record ScriptTestResult(string Fate, string? OutputJson, string? Reason, long ElapsedMs);

/// <summary>Limites appliquées aux scripts (fixées par le moteur).</summary>
public sealed record ScriptLimitsResult(int TimeoutMs, long MemoryBytes, int MaxStatements, int MaxRecursion, long MaxArraySize, int MaxSourceLength);
