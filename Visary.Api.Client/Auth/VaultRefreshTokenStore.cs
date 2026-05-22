namespace Visary.Api.Auth;

/// <summary>
/// Vault-сторa для refresh_token. SDK-интеграция (VaultSharp для HashiCorp Vault или
/// аналогичный клиент) подключается отдельным коммитом после согласования endpoint'а
/// и auth-mode (AppRole / Kubernetes service account) с командой infra.
///
/// Пока — заглушка, бросающая <see cref="NotImplementedException"/>. Дефолтный DI не
/// привязывается к этому классу: <see cref="VisaryClientExtensions.AddVisaryOidcAuth"/>
/// регистрирует <see cref="EnvironmentRefreshTokenStore"/>, а переход на Vault делается
/// явной строкой <c>services.Replace(...)</c> в <c>Program.cs</c> — см. doc 107.
/// </summary>
public sealed class VaultRefreshTokenStore : IVisaryRefreshTokenStore
{
    private const string NotImplementedMessage =
        "VaultRefreshTokenStore: SDK-интеграция ещё не подключена. " +
        "См. doc_project/107-visary-token-provider.md — раздел «Подключение Vault».";

    public Task<string?> GetAsync(CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task SetAsync(string refreshToken, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);
}
