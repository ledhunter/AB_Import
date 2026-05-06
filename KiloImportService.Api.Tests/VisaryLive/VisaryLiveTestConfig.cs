using System.IO;
using System.Text.Json;

namespace KiloImportService.Api.Tests.VisaryLive;

/// <summary>
/// Конфигурация live-тестов: BaseUrl и BearerToken.
/// Источники в порядке приоритета:
///   1. env var <c>VISARY_TEST_TOKEN</c> / <c>VISARY_TEST_BASEURL</c>
///   2. <c>.audit/.token</c> в корне репозитория (используется audit-скриптом)
///   3. <c>KiloImportService.Api/appsettings.Local.json</c> (тот же файл, что у API)
///
/// Если токен не найден или истёк — все live-тесты пропускаются через
/// <c>Skip.IfNot(...)</c>. Локально перед запуском обновите токен в любом из источников.
/// </summary>
internal static class VisaryLiveTestConfig
{
    public const string DefaultBaseUrl = "https://isup-alfa-test.k8s.npc.ba";

    public static (string? BaseUrl, string? Token) Resolve()
    {
        var envToken   = Environment.GetEnvironmentVariable("VISARY_TEST_TOKEN");
        var envBaseUrl = Environment.GetEnvironmentVariable("VISARY_TEST_BASEURL");

        var fileToken  = TryReadFile(FindRepoFile(".audit/.token"))?.Trim();

        var (jsonBase, jsonToken) = ReadAppSettingsLocal();

        return (
            BaseUrl: envBaseUrl ?? jsonBase ?? DefaultBaseUrl,
            Token:   envToken   ?? fileToken ?? jsonToken
        );
    }

    public static bool IsTokenAvailable() => !string.IsNullOrWhiteSpace(Resolve().Token);

    // Висящие 401 от мёртвого токена выглядят как баг кода. Проверяем exp у JWT перед прогоном.
    public static bool IsTokenLikelyAlive(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return false;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return false;
        try
        {
            var payload = parts[1];
            var pad     = payload.Length % 4;
            if (pad > 0) payload = payload.PadRight(payload.Length + (4 - pad), '=');
            var bytes   = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return false;
            var expSeconds = exp.GetInt64();
            // Запас 30 секунд от текущего времени, чтобы тест не упал в момент истечения.
            return DateTimeOffset.FromUnixTimeSeconds(expSeconds) > DateTimeOffset.UtcNow.AddSeconds(30);
        }
        catch { return false; }
    }

    public static string SkipReason()
    {
        var (baseUrl, token) = Resolve();
        if (string.IsNullOrWhiteSpace(token))
            return "Live-тесты пропущены: токен не задан. Установите VISARY_TEST_TOKEN, или положите токен в .audit/.token, или в appsettings.Local.json (Visary:BearerToken).";
        if (!IsTokenLikelyAlive(token))
            return $"Live-тесты пропущены: токен истёк (BaseUrl={baseUrl}). Получите свежий из DevTools Visary.";
        return "OK";
    }

    private static string? TryReadFile(string? path)
        => (path != null && File.Exists(path)) ? File.ReadAllText(path) : null;

    // Поиск файла относительно корня репо: тестовый процесс запускается из bin/Debug/net10.0/.
    // Поднимаемся вверх по дереву, пока не найдём .git (граница репозитория).
    private static string? FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return Path.Combine(dir, relativePath);
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static (string? BaseUrl, string? Token) ReadAppSettingsLocal()
    {
        var path = FindRepoFile("KiloImportService.Api/appsettings.Local.json");
        if (path == null || !File.Exists(path)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Visary", out var v)) return (null, null);
            string? baseUrl = v.TryGetProperty("BaseUrl", out var b) ? b.GetString() : null;
            string? token   = v.TryGetProperty("BearerToken", out var t) ? t.GetString() : null;
            return (baseUrl, token);
        }
        catch { return (null, null); }
    }
}

/// <summary>
/// Известные ID живых сущностей в test-стенде Visary, использовавшихся при ручной верификации.
/// Если ID будут удалены/переименованы — соответствующие тесты упадут, и нужно будет
/// заменить ID на актуальный (взять любой из listview).
/// </summary>
internal static class VisaryLiveTestIds
{
    public const int ConstructionProject            = 4584;
    public const int ConstructionSite               = 7850;
    public const int ConstructionSection            = 615;
    public const int ConstructionSiteIndicator      = 114305;
    public const int ConstructionSiteIndicatorValue = 823476;
    public const int Room                           = 20585;
    public const int CadastralArea                  = 573;
    public const int PercentBet                     = 6;
    public const int ShareAgreement                 = 242;
    public const int Deal                           = 73;
    public const int Organization                   = 4499;
    public const int Town                           = 5565;
    public const int Region                         = 956;
    public const int ProjectType                    = 2;
    public const int InflationCalcMethod            = 4;
    public const int EstateClass                    = 12;
    public const int BuildingMaterial               = 7;
    public const int FinishingMaterial              = 3;
    public const int RoomKind                       = 11;

    public const string OrganizationClientId = "2";
}
