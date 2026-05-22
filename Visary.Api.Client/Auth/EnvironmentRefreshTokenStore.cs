namespace Visary.Api.Auth;

/// <summary>
/// Refresh-token из env-переменной — fallback для dev/CI, когда полноценный Vault недоступен.
///
/// ⚠️ Ротация хранится только в памяти процесса: после рестарта <see cref="GetAsync"/>
/// снова отдаст значение из env. Если IdP уже отозвал прежний refresh_token, бэкенд встанет
/// до обновления env. Для prod-окружения — переключаться на <see cref="VaultRefreshTokenStore"/>.
/// </summary>
public sealed class EnvironmentRefreshTokenStore : IVisaryRefreshTokenStore
{
    public const string DefaultEnvVarName = "VISARY_AUTH_REFRESH_TOKEN";

    private readonly string _envVarName;
    private string? _inMemoryOverride;
    private readonly object _gate = new();

    public EnvironmentRefreshTokenStore(string envVarName = DefaultEnvVarName)
    {
        _envVarName = envVarName;
    }

    public Task<string?> GetAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_inMemoryOverride ?? Environment.GetEnvironmentVariable(_envVarName));
        }
    }

    public Task SetAsync(string refreshToken, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _inMemoryOverride = refreshToken;
        }
        return Task.CompletedTask;
    }
}
