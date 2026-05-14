using System.Reflection;
using System.Text.Json;
using ClosedXML.Excel;
using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping.Budget;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Budget;

/// <summary>
/// Генерирует XLSX-файл бюджета по эталонному шаблону «Бюджет_А4.1.xlsx» для
/// последующего ручного импорта в Visary (см. doc_project/78-budget-xlsx-export.md).
///
/// Visary при импорте чувствительна к структуре файла: порядок строк, состав статей,
/// заголовок, примечания должны строго совпадать с эталоном. Поэтому встроенный
/// шаблон копируется как есть, и подменяются только значения в колонках C (DeclaredSum)
/// и D (ConfirmedSum). Стили, ширины, шрифты, примечания (G) сохраняются автоматически.
///
/// Суммы глав/разделов считаются здесь же (агрегация снизу вверх по
/// <see cref="BudgetReferenceEntry.ParentCode"/>), а не перекладываются на Visary —
/// в эталоне они проставлены явно.
/// </summary>
public sealed class BudgetXlsxExporter
{
    private const string TemplateResourceName =
        "KiloImportService.Api.Resources.budget-template-a41.xlsx";
    private const string SheetName = "Бюджет";

    // Колонки в эталоне (см. doc_project/78-budget-xlsx-export.md).
    private const int ColCode         = 1; // A — «№ п/п»
    private const int ColDeclaredSum  = 3; // C — заявленные капвложения
    private const int ColConfirmedSum = 4; // D — одобренные капвложения

    // Финмодель хранит суммы в ТЫСЯЧАХ рублей, Visary ждёт рубли. См. ТЗ.
    private const double FinmodelToVisaryFactor = 1000d;

    private readonly ImportServiceDbContext _db;
    private readonly IBudgetReferenceProvider _budgetRef;
    private readonly ILogger<BudgetXlsxExporter> _log;

    public BudgetXlsxExporter(
        ImportServiceDbContext db,
        IBudgetReferenceProvider budgetRef,
        ILogger<BudgetXlsxExporter> log)
    {
        _db = db;
        _budgetRef = budgetRef;
        _log = log;
    }

    /// <summary>
    /// Сгенерировать байты XLSX по mapped budget rows сессии.
    /// Бросает <see cref="InvalidOperationException"/>, если у сессии нет бюджетных строк
    /// или ресурс шаблона не найден.
    /// </summary>
    public async Task<byte[]> GenerateAsync(Guid sessionId, CancellationToken ct)
    {
        // 1) Тянем mapped budget rows из staged-таблицы. Берём Valid и Applied:
        //    после Apply строки переводятся в Applied, и для уже применённой сессии
        //    бюджет всё ещё нужен (пользователь может вернуться и скачать позже).
        //    Error/Pending исключаем — они либо невалидны, либо ещё не прошли валидацию.
        var rows = await _db.StagedRows
            .AsNoTracking()
            .Where(r => r.ImportSessionId == sessionId
                     && (r.Status == StagedRowStatus.Valid
                         || r.Status == StagedRowStatus.Applied)
                     && r.MappedValues != null)
            .Select(r => r.MappedValues!)
            .ToListAsync(ct);

        var (terminalSums, chapterDirectSums) = ExtractBudgetSums(rows);
        if (terminalSums.Count == 0 && chapterDirectSums.Count == 0)
        {
            throw new InvalidOperationException(
                $"В сессии {sessionId} нет бюджетных строк (Kind='budget' среди валидных).");
        }

        // 2) Считаем агрегаты глав/разделов по дереву справочника. ChapterDirectSums
        //    переписывают агрегат для соответствующих глав — это значения «Итого» главы
        //    из файла, которые надёжнее, чем сумма children (см. ТЗ 2026-05-14, v1.3):
        //    в Главах 2/3 строки в файле не совпадают со справочником, поэтому
        //    aggregated по children = 0, и без override Глава в выгрузке получит 0.
        var aggregated = AggregateUpwards(terminalSums, chapterDirectSums);

        _log.LogInformation(
            "BudgetXlsxExporter: session={SessionId} terminal-articles={Term} chapter-direct={Direct} totalRows={Total}",
            sessionId, terminalSums.Count, chapterDirectSums.Count, aggregated.Count);

        // 3) Открываем embedded-шаблон в MemoryStream (ClosedXML модифицирует in-place).
        await using var template = OpenTemplateStream();
        using var memory = new MemoryStream();
        await template.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var workbook = new XLWorkbook(memory);
        var sheet = workbook.Worksheet(SheetName);

        // Чистим defined names — нужны, потому что ниже удаляем строки подстатей Глав 2/3.
        // ClosedXML при Row.Delete() пересчитывает refs у named ranges и падает с
        // ParsingException("Unexpected token EofSymbolId") на пустых ссылках. Visary
        // defined names не использует — можно безопасно убрать.
        foreach (var nr in workbook.NamedRanges.ToList()) nr.Delete();
        foreach (var nr in sheet.NamedRanges.ToList()) nr.Delete();

        // 4) Прогон строк шаблона:
        //    • Глава 1 — выгружаем ПОЛНОЕ дерево статей (1., 1.1., …, 1.8.), даже
        //      отсутствующим в финмодели проставляется 0 (см. ТЗ от 2026-05-14, v1.1).
        //    • Главы 2 и 3 — выгружаем ТОЛЬКО саму главу (2., 3.) с агрегированным ИТОГО,
        //      подстатьи (2.1., 2.1.1., …, 3.8.) удаляем из выгрузки (бизнес-правило ТЗ
        //      от 2026-05-14, v1.2: для Visary нужны только сводные суммы по этим главам).
        //    Агрегация снизу вверх в AggregateUpwards считает суммы 2. и 3. из терминальных
        //    подстатей, даже если они не попадают в выгрузку.
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        var rowsToDelete = new List<int>();
        int written = 0;
        for (int rownum = 2; rownum <= lastRow; rownum++)
        {
            var codeCell = sheet.Cell(rownum, ColCode).GetString().Trim();
            if (string.IsNullOrEmpty(codeCell)) continue;

            var code = NormalizeCode(codeCell);
            if (IsCollapsedChapterDescendant(code))
            {
                rowsToDelete.Add(rownum);
                continue;
            }

            var (decl, conf) = aggregated.TryGetValue(code, out var v) ? v : (0d, 0d);
            sheet.Cell(rownum, ColDeclaredSum).Value = decl * FinmodelToVisaryFactor;
            sheet.Cell(rownum, ColConfirmedSum).Value = conf * FinmodelToVisaryFactor;
            written++;
        }

        // 5) Удаляем подстатьи Глав 2 и 3 (с конца, чтобы номера выше не сдвигались).
        for (int i = rowsToDelete.Count - 1; i >= 0; i--)
            sheet.Row(rowsToDelete[i]).Delete();

        _log.LogInformation(
            "BudgetXlsxExporter: session={SessionId} written={Written} deleted={Deleted} (× {Factor} factor; главы 2/3 свёрнуты до ИТОГО)",
            sessionId, written, rowsToDelete.Count, FinmodelToVisaryFactor);

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    /// <summary>
    /// Достаёт пары (Code → DeclaredSum/ConfirmedSum) из mapped JSON-строк (только Kind='budget').
    /// Разделяет на два потока:
    /// • <c>terminal</c> — обычные подстатьи (<c>ArticleCode != ChapterCode</c>): идут в агрегат.
    /// • <c>chapterDirect</c> — chapter-total override (<c>ArticleCode == ChapterCode</c>):
    ///   значение строки «Итого» главы из файла. Используется как override итоговой
    ///   суммы главы в <see cref="AggregateUpwards"/>.
    /// </summary>
    private static (
        Dictionary<string, (double Decl, double Conf)> Terminal,
        Dictionary<string, (double Decl, double Conf)> ChapterDirect)
        ExtractBudgetSums(IReadOnlyList<JsonDocument> rows)
    {
        var terminal = new Dictionary<string, (double Decl, double Conf)>(StringComparer.Ordinal);
        var chapterDirect = new Dictionary<string, (double Decl, double Conf)>(StringComparer.Ordinal);
        foreach (var doc in rows)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("Kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != "budget")
                continue;

            var articleCode = root.GetProperty("ArticleCode").GetString();
            if (string.IsNullOrEmpty(articleCode)) continue;
            var chapterCode = root.TryGetProperty("ChapterCode", out var cc)
                              && cc.ValueKind == JsonValueKind.String
                                  ? cc.GetString()
                                  : null;

            var declared  = root.GetProperty("DeclaredSum").GetDouble();
            var confirmed = root.GetProperty("ConfirmedSum").GetDouble();

            // Sentinel «это ИТОГО главы»: ArticleCode совпадает с ChapterCode.
            if (string.Equals(articleCode, chapterCode, StringComparison.Ordinal))
            {
                chapterDirect[articleCode] = (declared, confirmed);
                continue;
            }

            // Если в файле одну и ту же подстатью встретили несколько раз —
            // парсер их уже агрегировал. На случай гонок берём максимум,
            // чтобы не «обнулить» сумму отдельной строкой.
            if (terminal.TryGetValue(articleCode, out var prev))
            {
                terminal[articleCode] = (
                    Math.Max(prev.Decl, declared),
                    Math.Max(prev.Conf, confirmed));
            }
            else
            {
                terminal[articleCode] = (declared, confirmed);
            }
        }
        return (terminal, chapterDirect);
    }

    /// <summary>
    /// Считает суммы для глав/разделов: проходит по справочнику в порядке убывания глубины,
    /// для каждой записи прибавляет её сумму к ParentCode. На выходе — словарь Code → суммы,
    /// в котором есть как терминальные подстатьи, так и агрегированные родители.
    ///
    /// <paramref name="chapterDirect"/> применяется ПОСЛЕ агрегации и переписывает суммы
    /// глав значениями из файла (строки «Итого» главы). Это нужно для Глав 2/3, где статьи
    /// в файле не совпадают со справочником и aggregated by-children == 0.
    /// </summary>
    private Dictionary<string, (double Decl, double Conf)> AggregateUpwards(
        IReadOnlyDictionary<string, (double Decl, double Conf)> terminal,
        IReadOnlyDictionary<string, (double Decl, double Conf)> chapterDirect)
    {
        var acc = new Dictionary<string, (double Decl, double Conf)>(StringComparer.Ordinal);
        // 1) Засеваем терминальными суммами.
        foreach (var (code, v) in terminal) acc[code] = v;

        // 2) Идём от глубоких записей к коротким: каждый ребёнок прибавляется к родителю.
        var ordered = _budgetRef.Entries
            .OrderByDescending(e => e.Depth)
            .ThenBy(e => e.Code, StringComparer.Ordinal)
            .ToList();

        foreach (var entry in ordered)
        {
            if (entry.ParentCode is null) continue;
            if (!acc.TryGetValue(entry.Code, out var self)) continue;

            acc.TryGetValue(entry.ParentCode, out var parent);
            acc[entry.ParentCode] = (parent.Decl + self.Decl, parent.Conf + self.Conf);
        }

        // 3) Override итоговых сумм глав из файла (строки «Итого»). Если для главы есть
        //    chapter-direct сумма — берём её, иначе оставляем агрегат по children.
        foreach (var (code, v) in chapterDirect)
        {
            acc[code] = v;
        }

        return acc;
    }

    private static string NormalizeCode(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return s;
        return s.EndsWith('.') ? s : s + ".";
    }

    /// <summary>
    /// Главы, у которых в выгрузке оставляем ТОЛЬКО саму строку главы (с ИТОГО) и
    /// удаляем все подстатьи. Введено по ТЗ 2026-05-14, v1.2: для Глав 2 и 3 в файле для
    /// Visary нужны только сводные суммы.
    /// </summary>
    private static readonly string[] CollapsedChapterCodes = ["2.", "3."];

    /// <summary>
    /// <c>true</c>, если код — потомок «свёрнутой» главы (т.е. это <c>2.1.</c>,
    /// <c>2.1.1.</c>, <c>3.5.</c>, …), но не сама глава. Эти строки удаляются из выгрузки.
    /// </summary>
    private static bool IsCollapsedChapterDescendant(string code)
    {
        foreach (var chapter in CollapsedChapterCodes)
        {
            if (string.Equals(code, chapter, StringComparison.Ordinal)) return false; // сама глава остаётся
            if (code.StartsWith(chapter, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static Stream OpenTemplateStream()
    {
        var asm = typeof(BudgetXlsxExporter).Assembly;
        var stream = asm.GetManifestResourceStream(TemplateResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{TemplateResourceName}' не найден. " +
                $"Проверь <EmbeddedResource> в KiloImportService.Api.csproj. " +
                $"Доступные ресурсы: {string.Join(", ", asm.GetManifestResourceNames())}");
        return stream;
    }
}
