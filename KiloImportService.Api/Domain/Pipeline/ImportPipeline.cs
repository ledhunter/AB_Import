using System.Security.Cryptography;
using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping;
using KiloImportService.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Domain.Pipeline;

/// <summary>
/// Оркестратор импорта: связывает парсер, маппер и обе БД.
/// Прогресс публикуется в SignalR через <see cref="ImportProgressHub"/>.
///
/// Жизненный цикл:
///   1. <see cref="UploadAsync"/> — приём файла, sha256, ImportFileSnapshot, создание сессии.
///   2. <see cref="ParseAndValidateAsync"/> — фоновая задача (парсер → mapper.Validate → StagedRow + ImportError).
///   3. <see cref="ApplyAsync"/> — применение валидных строк в visary_db.
/// </summary>
public sealed class ImportPipeline
{
    private readonly ImportServiceDbContext _serviceDb;
    private readonly VisaryDbContext _visaryDb;
    private readonly IFileParserFactory _parserFactory;
    private readonly IImportMapperRegistry _mapperRegistry;
    private readonly IHubContext<ImportProgressHub> _hub;
    private readonly IFileStorage _storage;
    private readonly ILogger<ImportPipeline> _log;

    public ImportPipeline(
        ImportServiceDbContext serviceDb,
        VisaryDbContext visaryDb,
        IFileParserFactory parserFactory,
        IImportMapperRegistry mapperRegistry,
        IHubContext<ImportProgressHub> hub,
        IFileStorage storage,
        ILogger<ImportPipeline> log)
    {
        _serviceDb = serviceDb;
        _visaryDb = visaryDb;
        _parserFactory = parserFactory;
        _mapperRegistry = mapperRegistry;
        _hub = hub;
        _storage = storage;
        _log = log;
    }

    /// <summary>
    /// Принять файл и зарегистрировать сессию (status=Pending).
    /// Парсинг запускается отдельно через <see cref="ParseAndValidateAsync"/>.
    /// </summary>
    /// <param name="secondaryFileStream">
    /// Опциональный второй файл (для FinModel — «План»). Сохраняется в storage,
    /// путь записывается в <see cref="ImportFileSnapshot.SecondaryRelativePath"/>.
    /// На Apply пайплайн пробросит путь в <see cref="ImportContext.SecondaryFileRelativePath"/>,
    /// мапер сам откроет файл через <see cref="IFileStorage"/>. См. doc 110.
    /// </param>
    public async Task<ImportSession> UploadAsync(
        string importTypeCode,
        Stream fileStream,
        string fileName,
        int? visaryProjectId,
        int? visarySiteId,
        string? userId,
        CancellationToken ct,
        Stream? secondaryFileStream = null,
        string? secondaryFileName = null)
    {
        // Проверяем тип импорта.
        _ = _mapperRegistry.GetByTypeCode(importTypeCode); // throws if unknown

        // Определяем формат по расширению.
        var format = FileFormatExtensions.DetectFromFileName(fileName)
            ?? throw new ArgumentException($"Не удалось определить формат файла '{fileName}'. " +
                $"Поддерживаются: csv, xls, xlsb, xlsx.");

        // Считаем SHA-256 (потоково, без полной загрузки в память).
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, ct);
        ms.Position = 0;
        var hashBytes = await sha.ComputeHashAsync(ms, ct);
        var sha256Hex = Convert.ToHexStringLower(hashBytes);
        ms.Position = 0;

        // Сохраняем файл на диск (в storage).
        var snapshotPath = await _storage.SaveAsync(ms, fileName, ct);

        // Второй файл — опционально. Сохраняем как отдельный объект storage,
        // путь и метаданные кладём в Snapshot. Формат не валидируем здесь —
        // мапер сам решает, как его трактовать (для FinModel ожидается XLSX).
        string? secondaryPath = null;
        long? secondarySize = null;
        if (secondaryFileStream is not null)
        {
            using var ms2 = new MemoryStream();
            await secondaryFileStream.CopyToAsync(ms2, ct);
            ms2.Position = 0;
            if (ms2.Length > 0)
            {
                secondaryPath = await _storage.SaveAsync(
                    ms2, secondaryFileName ?? "secondary.xlsx", ct);
                secondarySize = ms2.Length;
            }
        }

        // Создаём сессию + ImportFileSnapshot.
        var session = new ImportSession
        {
            ImportTypeCode = importTypeCode,
            FileName = fileName,
            FileSize = ms.Length,
            FileFormat = format,
            VisaryProjectId = visaryProjectId,
            VisarySiteId = visarySiteId,
            UserId = userId,
            Status = ImportStatus.Pending,
        };
        session.FileSnapshot = new ImportFileSnapshot
        {
            ImportSessionId = session.Id,
            RelativePath = snapshotPath,
            ContentType = ContentTypeFor(format),
            SizeBytes = ms.Length,
            SecondaryRelativePath = secondaryPath,
            SecondaryFileName = secondaryPath is null ? null : secondaryFileName,
            SecondarySizeBytes = secondarySize,
        };
        _serviceDb.Sessions.Add(session);
        await _serviceDb.SaveChangesAsync(ct);

        _log.LogInformation(
            "Import session {SessionId} created: type={Type} file={File} ({Size} bytes){SecondaryInfo}",
            session.Id, importTypeCode, fileName, ms.Length,
            secondaryPath is null ? "" : $" + secondary='{secondaryFileName}' ({secondarySize} bytes)");

        return session;
    }

    /// <summary>
    /// Парсить файл и валидировать строки. Вызывается в фоновом потоке после Upload.
    /// При <see cref="OperationCanceledException"/> (Cancel-endpoint) переводим
    /// сессию в <c>Cancelled</c> и пробрасываем исключение наружу.
    /// </summary>
    public async Task ParseAndValidateAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await ParseAndValidateCoreAsync(sessionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledAsync(sessionId, "Импорт отменён пользователем.", default);
            throw;
        }
    }

    private async Task ParseAndValidateCoreAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await _serviceDb.Sessions
            .Include(s => s.FileSnapshot)
            .FirstAsync(s => s.Id == sessionId, ct);
        var groupName = ImportProgressHub.GroupName(sessionId);
        var mapper = _mapperRegistry.GetByTypeCode(session.ImportTypeCode);

        // ── PARSE ──
        await TransitionAsync(session, ImportStatus.Parsing, ct);
        var parseStage = await StartStageAsync(sessionId, ImportStageKind.Parse, "Чтение файла…", ct);
        await _hub.Clients.Group(groupName).SendAsync("StageStarted", new { sessionId, stage = "Parse" }, ct);

        var parser = _parserFactory.GetParser(session.FileFormat);
        ParseResult parseResult;
        await using (var stream = await _storage.OpenReadAsync(session.FileSnapshot!.RelativePath, ct))
        {
            parseResult = await parser.ParseAsync(stream, mapper.LayoutHint, ct);
        }

        // Сохраняем file-level ошибки парсинга.
        foreach (var err in parseResult.Errors)
        {
            _serviceDb.Errors.Add(new ImportError
            {
                ImportSessionId = sessionId,
                Sheet = string.Empty,
                SourceRowNumber = err.RowNumber ?? 0,
                ErrorCode = "parse_failure",
                Message = err.Message,
            });
        }
        await CompleteStageAsync(parseStage, success: parseResult.Errors.Count == 0,
            message: $"Прочитано строк: {parseResult.Rows.Count}", ct: ct);
        await _hub.Clients.Group(groupName).SendAsync("StageCompleted",
            new { sessionId, stage = "Parse", rows = parseResult.Rows.Count }, ct);

        if (parseResult.Errors.Any(e => e.RowNumber is null))
        {
            // Фатальная ошибка парсинга — выходим.
            await TransitionAsync(session, ImportStatus.Failed, ct,
                error: parseResult.Errors.First(e => e.RowNumber is null).Message);
            return;
        }

        // ── VALIDATE ──
        _log.LogInformation("Session {SessionId}: starting VALIDATE stage", sessionId);
        await TransitionAsync(session, ImportStatus.Validating, ct);
        var validateStage = await StartStageAsync(sessionId, ImportStageKind.Validate, "Валидация строк…", ct);
        await _hub.Clients.Group(groupName).SendAsync("StageStarted", new { sessionId, stage = "Validate" }, ct);

        _log.LogInformation("Session {SessionId}: calling mapper.ValidateAsync", sessionId);
        var ctx = new ImportContext(
            sessionId, session.VisaryProjectId, session.VisarySiteId, session.UserId,
            SecondaryFileRelativePath: session.FileSnapshot?.SecondaryRelativePath);
        var validation = await mapper.ValidateAsync(ctx, parseResult.Rows, _visaryDb, ct);
        _log.LogInformation("Session {SessionId}: mapper.ValidateAsync returned rows={RowsCount} errors={ErrorsCount}", 
            sessionId, validation.Rows.Count, validation.FileLevelErrors.Count);

        // File-level ошибки валидации.
        foreach (var fe in validation.FileLevelErrors)
        {
            _serviceDb.Errors.Add(new ImportError
            {
                ImportSessionId = sessionId,
                Sheet = string.Empty, // file-level: не привязано к листу
                SourceRowNumber = 0,
                ColumnName = fe.ColumnName,
                ErrorCode = fe.ErrorCode,
                Message = fe.Message,
            });
        }

        // Сохраняем StagedRow + ImportError для каждой строки.
        // Параллельно публикуем StageProgress в SignalR — раз в N строк, чтобы не
        // спамить хаб тысячами событий на больших файлах.
        // ⚙️ Прогресс считаем В РАЗРЕЗЕ ЛИСТА: для пользователя «строка X из Y»
        // ассоциируется с конкретным листом Excel, а не с глобальной нумерацией.
        // Иначе «лист Квартиры: строка 2 из 9» (где 9 — суммарный счётчик
        // по всем листам) выглядит бессмысленно.
        //
        // ⚠️ Тоталы по листу берём из validation.Rows (а НЕ из parseResult.Rows): маппер
        // может тихо пропускать сводные «ИТОГО»-строки, и тогда количество ParsedRow
        // на лист > количество MappedRow на лист. Считаем относительно того, что реально
        // увидим в этом цикле — иначе sheetProcessed никогда не достигнет sheetTotal.
        var totalsBySheet = validation.Rows
            .GroupBy(r => r.Sheet, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var processedBySheet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // RawValues для StagedRow нужны для отчёта (UI показывает исходные ячейки).
        // Маппер может пропускать строки, поэтому индекс i в validation.Rows НЕ совпадает
        // с индексом в parseResult.Rows. Строим lookup (Sheet, SourceRowNumber) → ParsedRow.
        // На дубликатах (если ParsedRow один и тот же приходит дважды — маловероятно)
        // оставляем первый, чтобы избежать падения ToDictionary.
        var parsedByKey = new Dictionary<(string Sheet, int Row), ParsedRow>(parseResult.Rows.Count);
        foreach (var pr in parseResult.Rows)
        {
            var key = (pr.Sheet ?? string.Empty, pr.SourceRowNumber);
            parsedByKey.TryAdd(key, pr);
        }

        int successCount = 0, errorCount = 0;
        var totalRowsValidate = validation.Rows.Count;
        var notifyEvery = Math.Max(1, totalRowsValidate / 50); // ≈ 50 апдейтов на файл
        for (int i = 0; i < validation.Rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var mr = validation.Rows[i];
            // Sheet берём из MappedRow (mapper гарантирует — см. MappedRow.Sheet).
            // Раньше использовался parseResult.Rows[i].Sheet — это был bug-by-design:
            // mapper может пропускать строки, индексы расходятся, в StagedRow попадал
            // «чужой» лист, и составной ключ (Sheet, SourceRowNumber) уже не уникален.
            var sheet = mr.Sheet ?? string.Empty;
            var parsedRow = parsedByKey.GetValueOrDefault((sheet, mr.SourceRowNumber));
            // Сохраняем не только Cells, но и Sheet — чтобы UI мог показать,
            // на каком листе находилась строка.
            var rawPayload = new Dictionary<string, object?>
            {
                ["sheet"] = sheet,
                ["cells"] = parsedRow?.Cells ?? new Dictionary<string, string>(),
            };
            _serviceDb.StagedRows.Add(new StagedRow
            {
                ImportSessionId = sessionId,
                Sheet = sheet,
                SourceRowNumber = mr.SourceRowNumber,
                RawValues = System.Text.Json.JsonSerializer.SerializeToDocument(rawPayload),
                MappedValues = mr.MappedValues,
                Status = mr.IsValid ? StagedRowStatus.Valid : StagedRowStatus.Invalid,
            });
            foreach (var err in mr.Errors)
            {
                _serviceDb.Errors.Add(new ImportError
                {
                    ImportSessionId = sessionId,
                    Sheet = sheet,
                    SourceRowNumber = mr.SourceRowNumber,
                    ColumnName = err.ColumnName,
                    ErrorCode = err.ErrorCode,
                    Message = err.Message,
                });
            }
            if (mr.IsValid) successCount++; else errorCount++;

            // Per-sheet счётчик: «лист X — строка N из Total(X)».
            var sheetProcessed = processedBySheet.GetValueOrDefault(sheet) + 1;
            processedBySheet[sheet] = sheetProcessed;
            var sheetTotal = totalsBySheet.GetValueOrDefault(sheet, 1);

            var processed = i + 1;
            // Шлём событие либо на throttle (каждые notifyEvery строк), либо в
            // ключевые моменты: ПЕРВАЯ и ПОСЛЕДНЯЯ строки В ЛИСТЕ и общий финал.
            // Первая строка нужна, чтобы лист сразу появился в UI sheetProgress
            // (иначе при listе из 1-2 строк и notifyEvery>=2 он мог совсем не
            // получить событие до того, как пайплайн перейдёт к следующему).
            var isSheetFirstRow = sheetProcessed == 1;
            var isSheetLastRow  = sheetProcessed == sheetTotal;
            if (processed == totalRowsValidate || isSheetFirstRow || isSheetLastRow
                || processed % notifyEvery == 0)
            {
                var sheetPercent = sheetTotal == 0 ? 100 : (int)Math.Round((sheetProcessed * 100.0) / sheetTotal);
                var overallPercent = totalRowsValidate == 0
                    ? 100
                    : (int)Math.Round((processed * 100.0) / totalRowsValidate);
                validateStage.ProgressPercent = overallPercent;
                await _hub.Clients.Group(groupName).SendAsync("StageProgress", new
                {
                    sessionId,
                    stage = "Validate",
                    currentRow = sheetProcessed,
                    totalRows = sheetTotal,
                    percentComplete = sheetPercent,
                    sheet = sheet,
                }, ct);
            }
        }
        session.TotalRows = parseResult.Rows.Count;
        session.SuccessRows = successCount;
        session.ErrorRows = errorCount;
        await _serviceDb.SaveChangesAsync(ct);

        await CompleteStageAsync(validateStage, success: validation.FileLevelErrors.Count == 0,
            message: $"Валидно: {successCount} / Ошибок: {errorCount}", ct: ct);
        await _hub.Clients.Group(groupName).SendAsync("StageCompleted",
            new { sessionId, stage = "Validate", validRows = successCount, invalidRows = errorCount }, ct);

        var finalStatus = validation.FileLevelErrors.Count > 0
            ? ImportStatus.Failed
            : ImportStatus.Validated;
        await TransitionAsync(session, finalStatus, ct,
            error: validation.FileLevelErrors.FirstOrDefault()?.Message);
    }

    /// <summary>
    /// Применить валидированные строки в visary_db. Только из статуса Validated.
    /// </summary>
    public async Task ApplyAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await ApplyCoreAsync(sessionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledAsync(sessionId, "Применение отменено пользователем.", default);
            throw;
        }
    }

    private async Task ApplyCoreAsync(Guid sessionId, CancellationToken ct)
    {
        // Include(FileSnapshot) нужен для ImportContext.SecondaryFileRelativePath:
        // мапер на Apply-стадии должен иметь возможность открыть второй файл
        // (для FinModel — лист «План»). См. doc 110.
        var session = await _serviceDb.Sessions
            .Include(s => s.FileSnapshot)
            .FirstAsync(s => s.Id == sessionId, ct);
        if (session.Status != ImportStatus.Validated)
            throw new InvalidOperationException($"Apply возможен только из статуса Validated, текущий: {session.Status}.");

        var groupName = ImportProgressHub.GroupName(sessionId);
        var mapper = _mapperRegistry.GetByTypeCode(session.ImportTypeCode);

        await TransitionAsync(session, ImportStatus.Applying, ct);
        var applyStage = await StartStageAsync(sessionId, ImportStageKind.Apply, "Запись в visary_db…", ct);
        await _hub.Clients.Group(groupName).SendAsync("StageStarted", new { sessionId, stage = "Apply" }, ct);

        // Загружаем валидные строки.
        var staged = await _serviceDb.StagedRows
            .Where(r => r.ImportSessionId == sessionId && r.Status == StagedRowStatus.Valid)
            .ToListAsync(ct);

        var mappedRows = staged.Select(r => new MappedRow(r.SourceRowNumber, r.Sheet ?? string.Empty, true, r.MappedValues!, [])).ToList();
        var ctx = new ImportContext(
            sessionId, session.VisaryProjectId, session.VisarySiteId, session.UserId,
            SecondaryFileRelativePath: session.FileSnapshot?.SecondaryRelativePath);
        var applyResult = await mapper.ApplyAsync(ctx, _visaryDb, mappedRows, ct);

        // Обновляем статусы StagedRow + переносим per-row actions из маппера.
        // Actions сохраняем ВСЕГДА, даже если массово apply не удался: журнал
        // частичных действий (создан корпус → упал room) сам по себе диагностически
        // ценный артефакт. Ключ — (Sheet, SourceRowNumber), он совпадает с
        // уникальным индексом таблицы.
        var actionsByKey = (applyResult.RowActions ?? [])
            .ToDictionary(a => (a.Sheet, a.SourceRowNumber));
        foreach (var r in staged)
        {
            if (applyResult.AppliedCount > 0)
                r.Status = StagedRowStatus.Applied;
            if (actionsByKey.TryGetValue((r.Sheet, r.SourceRowNumber), out var log))
            {
                r.Actions = System.Text.Json.JsonSerializer.SerializeToDocument(log.Actions);
            }
        }
        await _serviceDb.SaveChangesAsync(ct);
        foreach (var err in applyResult.Errors)
        {
            // RowError может нести Sheet+SourceRowNumber для row-level Apply-ошибок
            // (например, company_group_not_found в FinModel привязывается к params-
            // строке). Без них — file-level (SourceRowNumber=0, Sheet=""), фронт
            // отрендерит в блоке «Ошибки уровня файла».
            _serviceDb.Errors.Add(new ImportError
            {
                ImportSessionId = sessionId,
                Sheet = err.Sheet ?? string.Empty,
                SourceRowNumber = err.SourceRowNumber ?? 0,
                ErrorCode = err.ErrorCode,
                Message = err.Message,
                ColumnName = err.ColumnName,
            });
        }
        await _serviceDb.SaveChangesAsync(ct);

        await CompleteStageAsync(applyStage, success: applyResult.Errors.Count == 0,
            message: $"Записано: {applyResult.AppliedCount}", ct: ct);
        await _hub.Clients.Group(groupName).SendAsync("StageCompleted",
            new { sessionId, stage = "Apply", applied = applyResult.AppliedCount }, ct);

        var finalStatus = applyResult.Errors.Count == 0 ? ImportStatus.Applied : ImportStatus.Failed;
        await TransitionAsync(session, finalStatus, ct,
            error: applyResult.Errors.FirstOrDefault()?.Message);
        session.CompletedAt = DateTimeOffset.UtcNow;
        await _serviceDb.SaveChangesAsync(ct);
    }

    // ───── Stage helpers ─────
    private async Task<ImportSessionStage> StartStageAsync(Guid sessionId, ImportStageKind kind, string message, CancellationToken ct)
    {
        var stage = new ImportSessionStage
        {
            ImportSessionId = sessionId,
            Kind = kind,
            Message = message,
            ProgressPercent = 0,
        };
        _serviceDb.Stages.Add(stage);
        await _serviceDb.SaveChangesAsync(ct);
        return stage;
    }

    private async Task CompleteStageAsync(ImportSessionStage stage, bool success, string message, CancellationToken ct)
    {
        stage.CompletedAt = DateTimeOffset.UtcNow;
        stage.IsSuccess = success;
        stage.ProgressPercent = 100;
        stage.Message = message;
        await _serviceDb.SaveChangesAsync(ct);
    }

    private async Task TransitionAsync(ImportSession session, ImportStatus newStatus, CancellationToken ct, string? error = null)
    {
        _log.LogInformation("Session {SessionId}: {Old} → {New}", session.Id, session.Status, newStatus);
        session.Status = newStatus;
        if (!string.IsNullOrEmpty(error)) session.ErrorMessage = error;
        await _serviceDb.SaveChangesAsync(ct);
        await _hub.Clients.Group(ImportProgressHub.GroupName(session.Id))
            .SendAsync("SessionStatus", new { sessionId = session.Id, status = newStatus.ToString() }, ct);
    }

    /// <summary>
    /// Принудительно завершает «зависшую» сессию: ставит статус <paramref name="finalStatus"/>
    /// и шлёт SignalR-событие, чтобы UI вышел из «в процессе». Используется:
    ///   • Upload-фоновой задачей при unhandled exception в ParseAndValidate
    ///     (раньше exception проглатывался и status оставался <c>Validating</c>);
    ///   • Cancel-endpoint'ом, когда CTS уже снят, но статус в БД промежуточный
    ///     (Parsing/Validating/Applying) — пользователь жмёт «Отменить» и видит,
    ///     что ничего не происходит.
    /// Защищает от повторной финализации (если уже Applied/Failed/Cancelled — не трогаем).
    /// </summary>
    public async Task ForceFinalizeAsync(
        Guid sessionId, ImportStatus finalStatus, string reason, CancellationToken ct = default)
    {
        if (finalStatus is not (ImportStatus.Failed or ImportStatus.Cancelled or ImportStatus.Applied))
            throw new ArgumentException(
                $"ForceFinalizeAsync принимает только Failed/Cancelled/Applied, получено {finalStatus}",
                nameof(finalStatus));
        try
        {
            var session = await _serviceDb.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            if (session is null) return;
            if (session.Status is ImportStatus.Applied or ImportStatus.Failed or ImportStatus.Cancelled)
                return;
            session.Status = finalStatus;
            session.CompletedAt = DateTimeOffset.UtcNow;
            session.ErrorMessage = reason;
            await _serviceDb.SaveChangesAsync(ct);
            await _hub.Clients.Group(ImportProgressHub.GroupName(sessionId))
                .SendAsync("SessionStatus", new { sessionId, status = finalStatus.ToString() }, ct);
            _log.LogInformation(
                "Session {SessionId} force-finalized to {Status}: {Reason}",
                sessionId, finalStatus, reason);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ForceFinalizeAsync failed for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Помечает сессию как отменённую — вызывается после <see cref="OperationCanceledException"/>.
    /// Использует НЕсвязанный <see cref="CancellationToken"/> (default), чтобы запись в БД
    /// прошла даже после отмены исходного токена.
    /// </summary>
    private async Task MarkCancelledAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        try
        {
            var session = await _serviceDb.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            if (session is null) return;
            // Если кто-то уже выставил финальный статус — не перетираем.
            if (session.Status is ImportStatus.Applied or ImportStatus.Failed or ImportStatus.Cancelled)
                return;
            session.Status = ImportStatus.Cancelled;
            session.CompletedAt = DateTimeOffset.UtcNow;
            session.ErrorMessage = reason;
            await _serviceDb.SaveChangesAsync(ct);
            await _hub.Clients.Group(ImportProgressHub.GroupName(sessionId))
                .SendAsync("SessionStatus", new { sessionId, status = ImportStatus.Cancelled.ToString() }, ct);
            _log.LogInformation("Session {SessionId} marked as Cancelled: {Reason}", sessionId, reason);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MarkCancelledAsync failed for session {SessionId}", sessionId);
        }
    }

    private static string ContentTypeFor(FileFormat f) => f switch
    {
        FileFormat.Csv  => "text/csv",
        FileFormat.Xls  => "application/vnd.ms-excel",
        FileFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        FileFormat.Xlsb => "application/vnd.ms-excel.sheet.binary.macroEnabled.12",
        _ => "application/octet-stream"
    };
}
