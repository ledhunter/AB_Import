using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Visary.Api.Auth;

/// <summary>
/// DelegatingHandler в HttpClient-pipeline'е: на каждом исходящем запросе берёт
/// access_token у <see cref="IVisaryTokenProvider"/> и ставит <c>Authorization: Bearer ...</c>.
///
/// На 401 от Visary: единожды invalidate-им кэш токена и переотправляем запрос. Гасит гонку
/// «access_token истёк ровно между fast-path-проверкой в provider'е и SendAsync» без
/// исключений наружу. Повторный 401 — уже честная ошибка авторизации, идёт дальше.
/// </summary>
public sealed class VisaryAuthHandler : DelegatingHandler
{
    private readonly IVisaryTokenProvider _tokenProvider;
    private readonly ILogger<VisaryAuthHandler> _log;

    public VisaryAuthHandler(IVisaryTokenProvider tokenProvider, ILogger<VisaryAuthHandler> log)
    {
        _tokenProvider = tokenProvider;
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        await ApplyTokenAsync(request, ct).ConfigureAwait(false);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        _log.LogWarning("Visary вернул 401 на {Method} {Url} — invalidating cached token и retry.",
            request.Method, request.RequestUri);
        response.Dispose();
        await _tokenProvider.InvalidateAsync(ct).ConfigureAwait(false);

        using var retry = await CloneRequestAsync(request, ct).ConfigureAwait(false);
        await ApplyTokenAsync(retry, ct).ConfigureAwait(false);
        return await base.SendAsync(retry, ct).ConfigureAwait(false);
    }

    private async Task ApplyTokenAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    // HttpRequestMessage нельзя послать дважды — клонируем headers/content/uri/method.
    // Для multipart-uploadа (FileStorage.UploadAsync) копируем raw bytes и переносим
    // Content-Type с boundary — сервер парсит корректно.
    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage original, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var h in original.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var contentClone = new ByteArrayContent(bytes);
            foreach (var ch in original.Content.Headers)
                contentClone.Headers.TryAddWithoutValidation(ch.Key, ch.Value);
            clone.Content = contentClone;
        }
        return clone;
    }
}
