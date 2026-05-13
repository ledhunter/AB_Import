using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Domain.Importing;
using Microsoft.EntityFrameworkCore;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace KiloImportService.Api.Pdf;

/// <summary>
/// Генерирует PDF-отчёт по одной или нескольким сессиям импорта.
///
/// Структура документа:
///   • Шапка: «Отчёт по импортам», дата генерации, число сессий
///   • Для каждой выбранной сессии — отдельный раздел (с page-break между сессиями):
///       1. Заголовок: имя файла + статус
///       2. Метаданные (тип, файл, проект/объект, даты, длительность)
///       3. Сводка по строкам: всего / валидных / с ошибками
///       4. Таблица ошибок (если есть): № строки, лист, колонка, код, сообщение
///
/// Для ограничения размера PDF: первые 200 ошибок по сессии; если больше — сноска
/// «и ещё N ошибок (полный отчёт см. в UI)».
/// </summary>
public sealed class ImportPdfReportService
{
    private const int MaxErrorsPerSession = 200;

    private readonly ImportServiceDbContext _db;
    private readonly ILogger<ImportPdfReportService> _log;

    // Регистрируем FontResolver ОДИН раз на процесс (PDFsharp использует глобальный singleton).
    private static readonly object FontInitLock = new();
    private static bool _fontInitialized;

    public ImportPdfReportService(ImportServiceDbContext db, ILogger<ImportPdfReportService> log)
    {
        _db = db;
        _log = log;
        EnsureFontResolver();
    }

    private static void EnsureFontResolver()
    {
        if (_fontInitialized) return;
        lock (FontInitLock)
        {
            if (_fontInitialized) return;
            GlobalFontSettings.FontResolver = new PdfFontResolver();
            _fontInitialized = true;
        }
    }

    /// <summary>
    /// Сгенерировать PDF для заданного набора sessionId. Идентификаторы, которых нет
    /// в БД, тихо игнорируются. Сортировка сессий в PDF — по <c>StartedAt DESC</c>.
    /// </summary>
    public async Task<byte[]> GenerateAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
    {
        if (sessionIds.Count == 0)
            throw new ArgumentException("Не передано ни одного sessionId.", nameof(sessionIds));

        var sessions = await _db.Sessions
            .AsNoTracking()
            .Where(s => sessionIds.Contains(s.Id))
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync(ct);

        var errorsBySession = await _db.Errors
            .AsNoTracking()
            .Where(e => sessionIds.Contains(e.ImportSessionId))
            .OrderBy(e => e.Sheet)
            .ThenBy(e => e.SourceRowNumber)
            .GroupBy(e => e.ImportSessionId)
            .ToDictionaryAsync(g => g.Key, g => g.ToList(), ct);

        var doc = BuildDocument(sessions, errorsBySession);
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();

        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        _log.LogInformation("PDF report generated: sessions={Count}, bytes={Bytes}", sessions.Count, ms.Length);
        return ms.ToArray();
    }

    private static Document BuildDocument(
        List<ImportSession> sessions,
        Dictionary<Guid, List<ImportError>> errorsBySession)
    {
        var doc = new Document();
        doc.Info.Title = "Отчёт по импортам";
        doc.Info.Author = "KiloImportService";

        var style = doc.Styles["Normal"]!;
        style.Font.Name = PdfFontResolver.DefaultFamily;
        style.Font.Size = 10;

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);

        AddHeader(section, sessions.Count);

        for (var i = 0; i < sessions.Count; i++)
        {
            if (i > 0) section.AddPageBreak();
            var session = sessions[i];
            errorsBySession.TryGetValue(session.Id, out var errs);
            AddSessionBlock(section, session, errs ?? []);
        }

        return doc;
    }

    private static void AddHeader(Section section, int sessionsCount)
    {
        var title = section.AddParagraph("Отчёт по импортам");
        title.Format.Font.Size = 18;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromMillimeter(2);

        var sub = section.AddParagraph();
        sub.Format.Font.Size = 9;
        sub.Format.Font.Color = Colors.Gray;
        sub.AddText($"Сгенерировано: {DateTime.Now:dd.MM.yyyy HH:mm:ss} · ");
        sub.AddText($"Сессий в отчёте: {sessionsCount}");
        sub.Format.SpaceAfter = Unit.FromMillimeter(8);
    }

    private static void AddSessionBlock(Section section, ImportSession session, List<ImportError> errors)
    {
        // 1) Заголовок: имя файла + статус
        var h = section.AddParagraph();
        h.Format.SpaceBefore = Unit.FromMillimeter(2);
        h.Format.SpaceAfter = Unit.FromMillimeter(3);
        var fileRun = h.AddFormattedText(session.FileName, TextFormat.Bold);
        fileRun.Size = 14;
        h.AddText("   ");
        var statusRun = h.AddFormattedText(StatusLabel(session.Status));
        statusRun.Color = StatusColor(session.Status);
        statusRun.Size = 11;

        // 2) Метаданные (двухколоночная таблица "ключ: значение")
        AddMetadataTable(section, session);

        // 3) Сводка по строкам
        var summary = section.AddParagraph();
        summary.Format.SpaceBefore = Unit.FromMillimeter(3);
        summary.Format.SpaceAfter = Unit.FromMillimeter(3);
        summary.AddFormattedText("Сводка по строкам: ", TextFormat.Bold);
        summary.AddText($"всего {session.TotalRows}, ");
        summary.AddFormattedText($"валидных {session.SuccessRows}").Color = Colors.DarkGreen;
        summary.AddText(", ");
        var errStat = summary.AddFormattedText($"с ошибками {session.ErrorRows}");
        errStat.Color = session.ErrorRows > 0 ? Colors.DarkRed : Colors.Gray;

        if (!string.IsNullOrWhiteSpace(session.ErrorMessage))
        {
            var fileErr = section.AddParagraph();
            fileErr.Format.SpaceBefore = Unit.FromMillimeter(2);
            fileErr.AddFormattedText("Ошибка уровня файла: ", TextFormat.Bold);
            fileErr.AddText(session.ErrorMessage);
            fileErr.Format.Font.Color = Colors.DarkRed;
        }

        // 4) Таблица ошибок (если есть)
        if (errors.Count > 0)
        {
            var errTitle = section.AddParagraph("Ошибки");
            errTitle.Format.Font.Bold = true;
            errTitle.Format.SpaceBefore = Unit.FromMillimeter(4);
            errTitle.Format.SpaceAfter = Unit.FromMillimeter(2);

            AddErrorsTable(section, errors);
        }
    }

    private static void AddMetadataTable(Section section, ImportSession session)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;

        table.AddColumn(Unit.FromCentimeter(4.5));   // label
        table.AddColumn(Unit.FromCentimeter(12.5));  // value

        AddKv(table, "Тип импорта", session.ImportTypeCode);
        AddKv(table, "Формат файла", session.FileFormat.ToString().ToUpperInvariant());
        AddKv(table, "Размер файла", FormatFileSize(session.FileSize));
        AddKv(table, "Проект Visary", session.VisaryProjectId?.ToString() ?? "—");
        AddKv(table, "Объект Visary", session.VisarySiteId?.ToString() ?? "—");
        AddKv(table, "Начало", session.StartedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss"));
        AddKv(table, "Окончание", session.CompletedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss") ?? "—");
        AddKv(table, "Длительность", FormatDuration(session.StartedAt, session.CompletedAt));
        AddKv(table, "Session ID", session.Id.ToString());
    }

    private static void AddKv(Table table, string label, string value)
    {
        var row = table.AddRow();
        var labelP = row.Cells[0].AddParagraph(label);
        labelP.Format.Font.Color = Colors.Gray;
        labelP.Format.Font.Size = 9;
        var valP = row.Cells[1].AddParagraph(value);
        valP.Format.Font.Size = 10;
    }

    private static void AddErrorsTable(Section section, List<ImportError> errors)
    {
        var table = section.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = Colors.LightGray;

        table.AddColumn(Unit.FromCentimeter(1.2));   // №
        table.AddColumn(Unit.FromCentimeter(2.8));   // Лист
        table.AddColumn(Unit.FromCentimeter(3.0));   // Колонка
        table.AddColumn(Unit.FromCentimeter(2.8));   // Код
        table.AddColumn(Unit.FromCentimeter(7.2));   // Сообщение

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Colors.Gainsboro;
        head.Format.Font.Bold = true;
        head.Format.Font.Size = 9;
        head.Cells[0].AddParagraph("№");
        head.Cells[1].AddParagraph("Лист");
        head.Cells[2].AddParagraph("Колонка");
        head.Cells[3].AddParagraph("Код");
        head.Cells[4].AddParagraph("Сообщение");

        var limited = errors.Take(MaxErrorsPerSession).ToList();
        foreach (var e in limited)
        {
            var row = table.AddRow();
            row.Format.Font.Size = 9;
            row.Cells[0].AddParagraph(e.SourceRowNumber == 0 ? "file" : e.SourceRowNumber.ToString());
            row.Cells[1].AddParagraph(string.IsNullOrEmpty(e.Sheet) ? "—" : e.Sheet);
            row.Cells[2].AddParagraph(string.IsNullOrEmpty(e.ColumnName) ? "—" : e.ColumnName);
            row.Cells[3].AddParagraph(e.ErrorCode);
            row.Cells[4].AddParagraph(e.Message);
        }

        if (errors.Count > limited.Count)
        {
            var remainder = section.AddParagraph(
                $"… и ещё {errors.Count - limited.Count} ошибок (полный отчёт см. в UI).");
            remainder.Format.Font.Size = 9;
            remainder.Format.Font.Color = Colors.Gray;
            remainder.Format.Font.Italic = true;
            remainder.Format.SpaceBefore = Unit.FromMillimeter(2);
        }
    }

    // ── вспомогательные форматтеры ────────────────────────────────────────

    private static string StatusLabel(ImportStatus s) => s switch
    {
        ImportStatus.Pending => "Ожидает",
        ImportStatus.Parsing => "Парсинг",
        ImportStatus.Validating => "Валидация",
        ImportStatus.Validated => "Готов к применению",
        ImportStatus.Applying => "Применение",
        ImportStatus.Applied => "Применено",
        ImportStatus.Failed => "Ошибка",
        ImportStatus.Cancelled => "Отменено",
        _ => s.ToString()
    };

    private static Color StatusColor(ImportStatus s) => s switch
    {
        ImportStatus.Applied => Colors.DarkGreen,
        ImportStatus.Failed => Colors.DarkRed,
        ImportStatus.Cancelled => Colors.Gray,
        ImportStatus.Validated => Colors.DarkOrange,
        _ => Colors.SteelBlue
    };

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = ["Б", "КБ", "МБ", "ГБ"];
        double size = bytes;
        var idx = 0;
        while (size >= 1024 && idx < units.Length - 1)
        {
            size /= 1024;
            idx++;
        }
        return $"{size:0.##} {units[idx]}";
    }

    private static string FormatDuration(DateTimeOffset start, DateTimeOffset? end)
    {
        if (end is null) return "—";
        var span = end.Value - start;
        if (span.TotalSeconds < 0) return "—";
        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }
}
