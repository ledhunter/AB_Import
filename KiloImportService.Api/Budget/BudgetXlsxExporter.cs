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

    // Эпсилон для «нулевого» сравнения: после ×1000 числа становятся большими,
    // 1e-3 (рубля) — это копейка. Меньше — считаем нулём.
    private const double ZeroEpsilon = 1e-3;

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

        var sums = ExtractBudgetSums(rows);
        if (sums.Count == 0)
        {
            throw new InvalidOperationException(
                $"В сессии {sessionId} нет бюджетных строк (Kind='budget' среди валидных).");
        }

        // 2) Считаем агрегаты глав/разделов по дереву справочника.
        var aggregated = AggregateUpwards(sums);

        _log.LogInformation(
            "BudgetXlsxExporter: session={SessionId} terminal-articles={Term} totalRows={Total}",
            sessionId, sums.Count, aggregated.Count);

        // 3) Открываем embedded-шаблон в MemoryStream (ClosedXML модифицирует in-place).
        await using var template = OpenTemplateStream();
        using var memory = new MemoryStream();
        await template.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var workbook = new XLWorkbook(memory);
        var sheet = workbook.Worksheet(SheetName);

        // Чистим defined names (печатные области, именованные диапазоны) — при удалении
        // строк ClosedXML пытается пересчитать refs у них и падает на пустых ссылках
        // (ParsingException: "Unexpected token EofSymbolId" — пустая формула в RefersTo).
        // Visary при импорте бюджета defined names не использует, можно безопасно убрать.
        if (workbook.NamedRanges.Count() > 0)
        {
            foreach (var nr in workbook.NamedRanges.ToList()) nr.Delete();
        }
        if (sheet.NamedRanges.Count() > 0)
        {
            foreach (var nr in sheet.NamedRanges.ToList()) nr.Delete();
        }

        // 4) Определяем какие Code оставить в файле. Trim-zeros по краям с сохранением
        //    промежуточных нулевых статей (см. <see cref="BuildKeepSet"/>).
        var keep = BuildKeepSet(aggregated);

        // 5) Сначала проставляем суммы (× 1000) для строк, которые остаются;
        //    несохраняемые строки запоминаем, чтобы удалить их одним пакетом в конце.
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        var rowsToDelete = new List<int>();
        int written = 0;
        for (int rownum = 2; rownum <= lastRow; rownum++)
        {
            var codeCell = sheet.Cell(rownum, ColCode).GetString().Trim();
            if (string.IsNullOrEmpty(codeCell)) continue;

            var code = NormalizeCode(codeCell);
            if (!keep.Contains(code))
            {
                rowsToDelete.Add(rownum);
                continue;
            }

            var (decl, conf) = aggregated.TryGetValue(code, out var v) ? v : (0d, 0d);
            sheet.Cell(rownum, ColDeclaredSum).Value = decl * FinmodelToVisaryFactor;
            sheet.Cell(rownum, ColConfirmedSum).Value = conf * FinmodelToVisaryFactor;
            written++;
        }

        // 6) Удаляем «лишние» строки с конца, чтобы нумерация выше не сдвигалась.
        //    Стили/нумерация эталона на оставшихся строках сохраняются.
        for (int i = rowsToDelete.Count - 1; i >= 0; i--)
            sheet.Row(rowsToDelete[i]).Delete();

        _log.LogInformation(
            "BudgetXlsxExporter: session={SessionId} kept={Written} deleted={Deleted} (× {Factor} factor applied)",
            sessionId, written, rowsToDelete.Count, FinmodelToVisaryFactor);

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    /// <summary>
    /// Строит набор Code, которые нужно оставить в результирующем XLSX.
    ///
    /// Правила (см. ТЗ от 2026-05-13):
    /// • Запись считается «активной», если её собственная сумма ≠ 0 ИЛИ у неё есть
    ///   активный потомок (рекурсивно).
    /// • Внутри каждого родителя (главы/раздела) находим первый и последний активный
    ///   ребёнок по исходному порядку справочника. Дети до первого и после последнего
    ///   удаляем целиком (с их потомками). Дети МЕЖДУ ними оставляем, даже если они
    ///   нулевые — Visary при импорте требует, чтобы между непустыми подстатьями не
    ///   было «пропущенных» промежуточных номеров (1.1 + 1.4 без 1.2, 1.3 ломает импорт).
    /// • Если у главы НИ ОДНОЙ активной подстатьи — главу удаляем целиком вместе с
    ///   её subtree.
    /// </summary>
    private HashSet<string> BuildKeepSet(IReadOnlyDictionary<string, (double Decl, double Conf)> aggregated)
    {
        var entries = _budgetRef.Entries;

        // children по ParentCode (порядок исходный из RawData — он же эталонный).
        var children = new Dictionary<string, List<BudgetReferenceEntry>>(StringComparer.Ordinal);
        var roots = new List<BudgetReferenceEntry>();
        foreach (var e in entries)
        {
            if (e.ParentCode is null) roots.Add(e);
            else
            {
                if (!children.TryGetValue(e.ParentCode, out var list))
                    children[e.ParentCode] = list = new List<BudgetReferenceEntry>();
                list.Add(e);
            }
        }

        // 1) IsActive (рекурсивно): своя сумма ≠ 0 ИЛИ хотя бы один активный потомок.
        var active = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool ComputeActive(BudgetReferenceEntry e)
        {
            if (active.TryGetValue(e.Code, out var cached)) return cached;
            bool selfActive = false;
            if (aggregated.TryGetValue(e.Code, out var v))
                selfActive = Math.Abs(v.Decl) > ZeroEpsilon || Math.Abs(v.Conf) > ZeroEpsilon;
            bool descActive = false;
            if (children.TryGetValue(e.Code, out var kids))
                foreach (var k in kids)
                    if (ComputeActive(k)) descActive = true;
            return active[e.Code] = (selfActive || descActive);
        }
        foreach (var e in entries) ComputeActive(e);

        // 2) Обходим дерево: для каждого родителя определяем [firstActiveIdx..lastActiveIdx]
        //    среди детей, в этот диапазон попадают и нулевые «промежуточные».
        var keep = new HashSet<string>(StringComparer.Ordinal);
        void Walk(BudgetReferenceEntry e)
        {
            if (!active[e.Code]) return;            // ветка целиком неактивна — пропускаем
            keep.Add(e.Code);
            if (!children.TryGetValue(e.Code, out var kids) || kids.Count == 0) return;

            int firstActive = -1, lastActive = -1;
            for (int i = 0; i < kids.Count; i++)
                if (active[kids[i].Code]) { if (firstActive < 0) firstActive = i; lastActive = i; }
            if (firstActive < 0) return;            // никого активного — детей не пишем

            for (int i = firstActive; i <= lastActive; i++)
            {
                // В диапазон попали — оставляем даже неактивных (промежуточные нулевые).
                // Для активных — рекурсивно идём вниз; неактивные «промежуточные» добавляем
                // только сами, без их детей (если бы были, что для нулевой главы маловероятно).
                var kid = kids[i];
                if (active[kid.Code]) Walk(kid);
                else keep.Add(kid.Code);
            }
        }
        foreach (var root in roots) Walk(root);

        return keep;
    }

    /// <summary>
    /// Достаёт пары (Code → DeclaredSum/ConfirmedSum) из mapped JSON-строк
    /// (только Kind='budget'; остальные параметрические строки игнорируем).
    /// </summary>
    private static Dictionary<string, (double Decl, double Conf)> ExtractBudgetSums(
        IReadOnlyList<JsonDocument> rows)
    {
        var sums = new Dictionary<string, (double Decl, double Conf)>(StringComparer.Ordinal);
        foreach (var doc in rows)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("Kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != "budget")
                continue;

            var articleCode = root.GetProperty("ArticleCode").GetString();
            if (string.IsNullOrEmpty(articleCode)) continue;

            var declared  = root.GetProperty("DeclaredSum").GetDouble();
            var confirmed = root.GetProperty("ConfirmedSum").GetDouble();

            // Если в файле одну и ту же подстатью встретили несколько раз —
            // парсер их уже агрегировал. На случай гонок берём максимум,
            // чтобы не «обнулить» сумму отдельной строкой.
            if (sums.TryGetValue(articleCode, out var prev))
            {
                sums[articleCode] = (
                    Math.Max(prev.Decl, declared),
                    Math.Max(prev.Conf, confirmed));
            }
            else
            {
                sums[articleCode] = (declared, confirmed);
            }
        }
        return sums;
    }

    /// <summary>
    /// Считает суммы для глав/разделов: проходит по справочнику в порядке убывания глубины,
    /// для каждой записи прибавляет её сумму к ParentCode. На выходе — словарь Code → суммы,
    /// в котором есть как терминальные подстатьи, так и агрегированные родители.
    /// </summary>
    private Dictionary<string, (double Decl, double Conf)> AggregateUpwards(
        IReadOnlyDictionary<string, (double Decl, double Conf)> terminal)
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
        return acc;
    }

    private static string NormalizeCode(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return s;
        return s.EndsWith('.') ? s : s + ".";
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
