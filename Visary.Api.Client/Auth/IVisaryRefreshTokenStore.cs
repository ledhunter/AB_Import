namespace Visary.Api.Auth;

/// <summary>
/// Чтение/запись refresh_token для <see cref="OidcVisaryTokenProvider"/>.
///
/// Зачем write-back: IdP может возвращать НОВЫЙ refresh_token на каждый refresh-grant
/// (rotation). Старый затем отзывается. Без сохранения нового — следующий рефреш пойдёт
/// по уже-отозванному refresh_token, IdP вернёт 400 invalid_grant, backend встанет.
///
/// Реализации:
///   • <see cref="EnvironmentRefreshTokenStore"/> — env-var (dev/CI; ротацию хранит только
///     в памяти процесса, после рестарта откатывается).
///   • <see cref="VaultRefreshTokenStore"/> — Vault-стора (prod), SDK подключается позже —
///     см. doc 107.
/// </summary>
public interface IVisaryRefreshTokenStore
{
    /// <summary>Возвращает текущий refresh_token либо <c>null</c>, если не задан.</summary>
    Task<string?> GetAsync(CancellationToken ct = default);

    /// <summary>Сохраняет новый refresh_token после успешного refresh-grant'а с rotation'ом.</summary>
    Task SetAsync(string refreshToken, CancellationToken ct = default);
}
