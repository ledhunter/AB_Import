using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Visary.Api.Common;
using Visary.Api.Dto;

namespace Visary.Api.FileStorage;

/// <summary>
/// Клиент файлового хранилища Visary (`/api/files/*`). Для нашего сценария — загрузка
/// сгенерированного XLSX-бюджета в папку ФХ и получение link-токена, который потом
/// идёт в `TypedImportWbs.File` (см. <see cref="CRUD.ICrudClient.CreateTypedImportWbsAsync"/>).
///
/// HAR-источник: <c>Context/har файл по загрузке бюджета в папку ФХ.txt</c> и
/// <c>Context/har импорт бюджета.txt</c>. Подробное описание API см.
/// <c>doc_project/82-visary-file-storage-upload.md</c>.
/// </summary>
public interface IFileStorageClient
{
    /// <summary>
    /// Загружает файл в папку файлового хранилища Visary.
    /// </summary>
    /// <param name="bytes">Содержимое файла.</param>
    /// <param name="fileName">Имя файла (например, <c>«Бюджет_{sessionId}.xlsx»</c>).</param>
    /// <param name="contentType">MIME (например, <c>application/vnd.openxmlformats-officedocument.spreadsheetml.sheet</c>).</param>
    /// <param name="driveId">ID диска ФХ. В test-окружении — 65.</param>
    /// <param name="directoryId">ID папки внутри диска. В test-окружении для бюджета — 40870.</param>
    /// <returns><c>item_id</c> созданной записи в ФХ (число).</returns>
    Task<int> UploadAsync(
        byte[] bytes, string fileName, string contentType,
        int driveId, int directoryId, CancellationToken ct = default);

    /// <summary>
    /// Получить link-токен для уже залитого файла. Этот opaque-токен передаётся в
    /// поле <c>File</c> запроса на создание <c>typedimportwbs</c>.
    /// </summary>
    Task<string> GetFileLinkAsync(
        int driveId, int itemId, bool checkPermission = true, CancellationToken ct = default);
}

public sealed class FileStorageClient : VisaryHttpBase<FileStorageClient>, IFileStorageClient
{
    public FileStorageClient(
        HttpClient http,
        IOptionsMonitor<VisaryOptions> options,
        ILogger<FileStorageClient> log)
        : base(http, options, log)
    {
    }

    public async Task<int> UploadAsync(
        byte[] bytes, string fileName, string contentType,
        int driveId, int directoryId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/files/files/upload?drive_id={driveId}&directory_id={directoryId}";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        // Имя поля формы = "upload" (см. HAR: Content-Disposition: form-data; name="upload").
        form.Add(fileContent, "upload", fileName);

        using var req = NewRequest(HttpMethod.Post, url);
        req.Content = form;

        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);

        // Ответ от Visary — наблюдаем три варианта (см. HAR + реальные запросы):
        //   • "40872"   — голое число (в кавычках или без)
        //   • [40884]   — JSON-массив с одним int
        //   • {"id":N}  — теоретически возможный JSON-объект
        // ExtractItemId терпимо разбирает все три формы.
        var raw = (await response.Content.ReadAsStringAsync(ct)).Trim();
        var itemId = ExtractItemId(raw)
            ?? throw new InvalidOperationException(
                $"FileStorage upload: не удалось распарсить ответ '{raw}' как item_id.");
        _log.LogInformation(
            "Visary FileStorage ← 200 POST /files/upload (drive={DriveId}, dir={DirId}, name='{Name}') → itemId={ItemId}",
            driveId, directoryId, fileName, itemId);
        return itemId;
    }

    /// <summary>
    /// Парсит item_id из ответа upload. Поддерживает: голый int, int в кавычках,
    /// JSON-массив <c>[id]</c>, JSON-объект <c>{"id":id}</c> (или похожие поля).
    /// </summary>
    private static int? ExtractItemId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // 1) Голый int.
        if (int.TryParse(raw, out var n)) return n;
        // 2) Quoted int: "40872".
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var inner = raw[1..^1];
            if (int.TryParse(inner, out var qn)) return qn;
        }
        // 3) JSON-массив или объект.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return root.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when root.TryGetInt32(out var v) => v,
                System.Text.Json.JsonValueKind.Array when root.GetArrayLength() > 0
                    && root[0].ValueKind == System.Text.Json.JsonValueKind.Number
                    && root[0].TryGetInt32(out var av) => av,
                System.Text.Json.JsonValueKind.Object => TryGetIntField(root, "id", "ID", "item_id", "itemId"),
                _ => null,
            };
        }
        catch { return null; }
    }

    private static int? TryGetIntField(System.Text.Json.JsonElement obj, params string[] names)
    {
        foreach (var n in names)
        {
            if (obj.TryGetProperty(n, out var el)
                && el.ValueKind == System.Text.Json.JsonValueKind.Number
                && el.TryGetInt32(out var v))
                return v;
        }
        return null;
    }

    public async Task<string> GetFileLinkAsync(
        int driveId, int itemId, bool checkPermission = true, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/files/link/file_link/by_id"
                  + $"?drive_id={driveId}&item_id={itemId}"
                  + $"&check_permission={(checkPermission ? "true" : "false")}";

        using var req = NewRequest(HttpMethod.Post, url);
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);

        var raw = (await response.Content.ReadAsStringAsync(ct)).Trim();
        // Visary возвращает либо голую строку с кавычками, либо JSON {"link":"…"}/{"result":"…"}.
        // Защищаемся от обоих вариантов: если raw — quoted-строка, снимаем кавычки;
        // если JSON-объект — пробуем выдрать поле "link" / "result".
        var token = ExtractLinkToken(raw);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"FileStorage file_link: пустой ответ или неожиданный формат: '{raw}'.");
        }
        _log.LogInformation(
            "Visary FileStorage ← 200 POST /link/file_link/by_id (drive={DriveId}, item={ItemId}) → link length={Len}",
            driveId, itemId, token.Length);
        return token;
    }

    private static string ExtractLinkToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Чисто строковое значение в кавычках: "abc..." → abc...
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        // JSON-объект: пытаемся выдрать link/result.
        if (raw.StartsWith('{'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                foreach (var name in new[] { "link", "result", "value", "token" })
                {
                    if (root.TryGetProperty(name, out var el)
                        && el.ValueKind == System.Text.Json.JsonValueKind.String)
                        return el.GetString() ?? string.Empty;
                }
            }
            catch { /* fallthrough */ }
        }
        // Plain-token без кавычек — отдаём как есть.
        return raw;
    }
}
