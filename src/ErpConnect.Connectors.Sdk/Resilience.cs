namespace ErpConnect.Connectors.Sdk;

/// <summary>
/// Nouvelle tentative exponentielle avec gigue complète (« full jitter »), plafonnée.
/// Un délai Retry-After fourni par le système distant est toujours respecté s'il est plus long.
/// Seules les erreurs transitoires sont réessayées.
/// </summary>
public sealed class RetryPolicy
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _random;

    public RetryPolicy(int maxAttempts = 5, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        MaxDelay = maxDelay ?? TimeSpan.FromMinutes(2);
        _delay = delay ?? Task.Delay;
#pragma warning disable CA5394 // La gigue n'a pas de rôle de sécurité.
        _random = random ?? Random.Shared.NextDouble;
#pragma warning restore CA5394
    }

    public int MaxAttempts { get; }

    /// <summary>Attente avant la tentative donnée (gigue complète), via la fonction d'attente configurée.</summary>
    public Task WaitBeforeAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct) => _delay(DelayFor(attempt, retryAfter), ct);

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    /// <summary>Délai avant la tentative <paramref name="attempt"/> (1 = première nouvelle tentative).</summary>
    public TimeSpan DelayFor(int attempt, TimeSpan? retryAfter)
    {
        var exp = Math.Min(MaxDelay.TotalMilliseconds, BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var jittered = TimeSpan.FromMilliseconds(exp * _random());
        return retryAfter is { } ra && ra > jittered ? ra : jittered;
    }

    public async Task<T> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> action, CancellationToken ct, Action<int, Exception, TimeSpan>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && ConnectorException.Classify(ex) == ErrorKind.Transient && !ct.IsCancellationRequested)
            {
                var wait = DelayFor(attempt, (ex as ConnectorException)?.RetryAfter);
                onRetry?.Invoke(attempt, ex, wait);
                await _delay(wait, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Limiteur de débit : au plus N requêtes par seconde, et pause imposée après un 429 (Retry-After).</summary>
public sealed class RequestRateLimiter : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public RequestRateLimiter(double maxPerSecond, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _interval = maxPerSecond > 0 ? TimeSpan.FromSeconds(1 / maxPerSecond) : TimeSpan.Zero;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _now();
            if (_next > now)
            {
                await _delay(_next - now, ct).ConfigureAwait(false);
                now = _next;
            }

            _next = now + _interval;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Le système distant demande d'attendre : aucune requête avant cette échéance.</summary>
    public void PauseFor(TimeSpan duration)
    {
        var until = _now() + duration;
        if (until > _next)
        {
            _next = until;
        }
    }

    public void Dispose() => _gate.Dispose();
}
