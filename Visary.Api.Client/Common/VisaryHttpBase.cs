using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Visary.Api.Dto;
using Visary.Api.Exceptions;

namespace Visary.Api.Common;

public abstract class VisaryHttpBase<T>
{
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected readonly HttpClient _http;
    private readonly IOptionsMonitor<VisaryOptions> _optionsMonitor;
    protected readonly ILogger<T> _log;

    protected VisaryHttpBase(HttpClient http, IOptionsMonitor<VisaryOptions> optionsMonitor, ILogger<T> log)
    {
        _http = http;
        _optionsMonitor = optionsMonitor;
        _log = log;
    }

    protected VisaryOptions Options => _optionsMonitor.CurrentValue;

    // Базовый URL Visary API — в эталонной иерархии (см. doc 145) лежит как
    // `EndpointsConfiguration:VisaryApi:Endpoint`. Поле в VisaryOptions называется
    // `Endpoint`; внутри клиентов оставляем алиас `BaseUrl` — это исторически
    // удобное имя для строковой конкатенации с `/api/visary/...`.
    protected string BaseUrl => Options.Endpoint.TrimEnd('/');

    protected void EnsureConfig()
    {
        var opt = Options;
        if (string.IsNullOrWhiteSpace(opt.Endpoint))
            throw new InvalidOperationException(
                "EndpointsConfiguration:VisaryApi:Endpoint не задан.");
        // Authorization теперь ставит VisaryAuthHandler через IVisaryTokenProvider — см. doc 107.
        // Здесь проверка BearerToken снята: при использовании StaticVisaryTokenProvider
        // он сам бросает InvalidOperationException при пустом токене.
    }

    protected HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        EnsureConfig();
        // Заголовок Authorization добавляет VisaryAuthHandler (DelegatingHandler в pipeline'е
        // HttpClient'а) — см. Visary.Api.Auth.VisaryAuthHandler.
        return new HttpRequestMessage(method, url);
    }

    protected async Task HandleAuthErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary auth error {Status}: {Body}", (int)response.StatusCode, body);
            throw new VisaryAuthException($"Visary вернул {(int)response.StatusCode} — токен истёк.");
        }
    }

    protected async Task HandleConflictAsync(HttpResponseMessage response, CancellationToken ct, string context)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary 409 Conflict [{Context}]: {Body}", context, body);
            throw new HttpRequestException($"Visary вернул 409 Conflict — RowVersion устарел. {context}");
        }
    }

    protected async Task HandleErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            _log.LogError("Visary error {Status}: {Body}", (int)response.StatusCode, body);
            // Раньше body уходил только в лог, в exception.Message было голое
            // «Visary вернул 500 Internal Server Error» — пользователь видел
            // это в UI и не понимал, что именно пошло не так. Подмешиваем
            // усечённый body в текст исключения для row-error в отчёте.
            var bodySnippet = TruncateForExceptionMessage(body);
            var suffix = string.IsNullOrWhiteSpace(bodySnippet) ? string.Empty : $". Body: {bodySnippet}";
            throw new HttpRequestException(
                $"Visary вернул {(int)response.StatusCode} {response.ReasonPhrase}{suffix}");
        }
    }

    /// <summary>
    /// Усекает тело ответа до ~400 символов и сворачивает переносы строк —
    /// чтобы row-error в построчном отчёте оставался читабельным и не
    /// засорял UI многострочным stack-trace-ом Visary.
    /// </summary>
    private static string TruncateForExceptionMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        const int max = 400;
        var collapsed = body.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (collapsed.Contains("  ")) collapsed = collapsed.Replace("  ", " ");
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    // Унифицированный GET для эндпоинтов /api/visary/crud/{mnemonic}/{id}.
    // Используется и CrudClient, и ListViewClient (для get-by-id операций).
    protected async Task<TEntity> GetCrudByIdAsync<TEntity>(string mnemonic, int id, CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/visary/crud/{mnemonic}/{id}";
        using var req = NewRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<TEntity>(JsonOptions, ct);
        _log.LogInformation("Visary ← 200 GET {Mnemonic}/{Id}", mnemonic, id);
        return result!;
    }
}
