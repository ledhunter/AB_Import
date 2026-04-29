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
    public async Task<ImportSession> UploadAsync(
        string importTypeCode,
        Stream fileStream,
        string fileName,
        int? visaryProjectId,
        int? visarySiteId,
        string? userId,
        CancellationToken ct)
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

        // Создаём сессию + ImportFileSnapshot.
        var session = new ImportSession
        {
            ImportTypeCode = importTypeCode,
            FileName = fileName,
            FileSize = ms.Length,
            FileFormat = format,
            FileSha256 = sha256Hex,
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
        };
        _serviceDb.Sessions.Add(session);
        await _serviceDb.SaveChangesAsync(ct);

        _log.LogInformation("Import session {SessionId} created: type={Type} file={File} ({Size} bytes, sha256={Sha}…)",
            session.Id, importTypeCode, fileName, ms.Length, sha256Hex[..12]);

        return session;
    }

    /// <summary>
    /// Парсить файл и валидировать строки. Вызывается в фоновом потоке после Upload.
    /// </summary>
    public async Task ParseAndValidateAsync(Guid sessionId, CancellationToken ct)
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
            parseResult = await parser.ParseAsync(stream, ct);
        }

        // Сохраняем file-level ошибки парсинга.
        foreach (var err in parseResult.Errors)
        {
            _serviceDb.Errors.Add(new ImportError
            {
                ImportSessionId = sessionId,
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
        await TransitionAsync(session, ImportStatus.Validating, ct);
        var validateStage = await StartStageAsync(sessionId, ImportStageKind.Validate, "Валидация строк…", ct);
        await _hub.Clients.Group(groupName).SendAsync("StageStarted", new { sessionId, stage = "Validate" }, ct);

        var ctx = new ImportContext(sessionId, session.VisaryProjectId, session.VisarySiteId, session.UserId);
        var validation = await mapper.ValidateAsync(ctx, parseResult.Rows, _visaryDb, ct);

        // File-level ошибки валидации.
        foreach (var fe in validation.FileLevelErrors)
        {
            _serviceDb.Errors.Add(new ImportError
            {
                ImportSessionId = sessionId,
                SourceRowNumber = 0,
                ColumnName = fe.ColumnName,
                ErrorCode = fe.ErrorCode,
                Message = fe.Message,
            });
        }

        // Сохраняем StagedRow + ImportError для каждой строки.
        int successCount = 0, errorCount = 0;
        for (int i = 0; i < validation.Rows.Count; i++)
        {
            var mr = validation.Rows[i];
            var raw = parseResult.Rows[i];
            _serviceDb.StagedRows.Add(new StagedRow
            {
                ImportSessionId = sessionId,
                SourceRowNumber = mr.SourceRowNumber,
                RawValues = System.Text.Json.JsonSerializer.SerializeToDocument(raw.Cells),
                MappedValues = mr.MappedValues,
                Status = mr.IsValid ? StagedRowStatus.Valid : StagedRowStatus.Invalid,
            });
            foreach (var err in mr.Errors)
            {
                _serviceDb.Errors.Add(new ImportError
                {
                    ImportSessionId = sessionId,
                    SourceRowNumber = mr.SourceRowNumber,
                    ColumnName = err.ColumnName,
                    ErrorCode = err.ErrorCode,
                    Message = err.Message,
                });
            }
            if (mr.IsValid) successCount++; else errorCount++;
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
        var session = await _serviceDb.Sessions.FirstAsync(s => s.Id == sessionId, ct);
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

        var mappedRows = staged.Select(r => new MappedRow(r.SourceRowNumber, true, r.MappedValues!, [])).ToList();
        var ctx = new ImportContext(sessionId, session.VisaryProjectId, session.VisarySiteId, session.UserId);
        var applyResult = await mapper.ApplyAsync(ctx, _visaryDb, mappedRows, ct);

        // Обновляем статусы StagedRow.
        if (applyResult.AppliedCount > 0)
        {
            foreach (var r in staged) r.Status = StagedRowStatus.Applied;
            await _serviceDb.SaveChangesAsync(ct);
        }
        foreach (var err in applyResult.Errors)
        {
            _serviceDb.Errors.Add(new ImportError
            {
                ImportSessionId = sessionId,
                SourceRowNumber = 0,
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

    private static string ContentTypeFor(FileFormat f) => f switch
    {
        FileFormat.Csv  => "text/csv",
        FileFormat.Xls  => "application/vnd.ms-excel",
        FileFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        FileFormat.Xlsb => "application/vnd.ms-excel.sheet.binary.macroEnabled.12",
        _ => "application/octet-stream"
    };
}
