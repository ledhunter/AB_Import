namespace Visary.Api.Dto;

public sealed class SitePatchRequest
{
    public int ID { get; set; }
    public long RowVersion { get; set; }
    public VisaryRef? Type { get; set; }
    public VisaryRef? EstateClass { get; set; }
    public VisaryRef? BuildingMaterial { get; set; }
    public VisaryRef? FinishingMaterial { get; set; }
    public VisaryRef? Town { get; set; }
    public string? Description { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public int? StageNumber { get; set; }
}

/// <summary>
/// PATCH-запрос для Room (через <c>/crud/room/{id}?forceUpdate=true</c>).
/// ID берётся из URL, RowVersion не требуется → поля nullable, чтобы при
/// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/>
/// не попадать в тело. Иначе Visary падает 500: «Can not add property RowVersion to JObject».
/// </summary>
public sealed class RoomPatchRequest
{
    public int? ID { get; set; }
    public long? RowVersion { get; set; }
    public string? Title { get; set; }
    public string? UniqueNumber { get; set; }
    public VisaryRef? Kind { get; set; }
    public VisaryRef? Section { get; set; }
    public string? ExplicationNumber { get; set; }
    public string? Floor { get; set; }
    public string? BuildingSection { get; set; }
    public int? RoomsNumber { get; set; }
    /// <summary>
    /// Студия: для импорта Помещений выставляется в <c>true</c>, когда в файле
    /// в колонке «Колич. комнат» стоит «с»/«ст»/«студ»/«студия» или <c>0</c>
    /// (см. doc 108). В этом же случае <see cref="RoomsNumber"/> = 0.
    /// </summary>
    public bool? IsStudio { get; set; }
    public double? ProjectArea { get; set; }
    /// <summary>
    /// Заполняется только для нежилых помещений (Kind.RoomCategory ≠ 0):
    /// «Машиноместо», «Кладовая», «Коммерческое» и т. п. Для жилых
    /// (Квартира/Апартамент) Visary берёт TotalArea как сумму
    /// TotalAreaWithoutSummerRoom + SummerRoomArea; пишем туда сами.
    /// </summary>
    public double? TotalArea { get; set; }
    public double? CostForOne { get; set; }
    public double? MarketCostPerM { get; set; }
    public double? ZalogCostPerM { get; set; }
}

/// <summary>
/// PATCH-запрос для ShareAgreement (через <c>/crud/shareagreement/{id}?forceUpdate=true</c>).
/// Аналогично <see cref="RoomPatchRequest"/>: ID/RowVersion не нужны в теле.
/// </summary>
public sealed class ShareAgreementPatchRequest
{
    public int? ID { get; set; }
    public long? RowVersion { get; set; }
    public string? Number { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Site { get; set; }
    public VisaryRef? Project { get; set; }

    // Поля для «реанимации» орфанного ДДУ — когда нашли в Visary запись по
    // (Number, Kind, ConditionalNumber, Stage, ProjectNumber), но она не привязана
    // к новой комнате текущей сессии импорта. См. doc 76-share-agreement-dedup.md.
    public int? RoomID { get; set; }
    public VisaryRef? Room { get; set; }
    public VisaryRef? RoomKindRef { get; set; }
    public string? ConditionalNumber { get; set; }
    public string? StageNumber { get; set; }
    public string? ProjectNumber { get; set; }
}

public sealed class SiteCreateRequest
{
    public int? StatusReadiness { get; set; }
    public int? ProjectID { get; set; }
    public VisaryRef? Project { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public int? StageNumber { get; set; }
    public VisaryRef? Type { get; set; }
    public VisaryRef? EstateClass { get; set; }
    public VisaryRef? BuildingMaterial { get; set; }
    public VisaryRef? FinishingMaterial { get; set; }
    public string? Description { get; set; }
}

public sealed class ProjectPatchRequest
{
    public int ID { get; set; }
    public long RowVersion { get; set; }
    public VisaryRef? Town { get; set; }
    public VisaryRef? Type { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? IdentifierKK { get; set; }
    public string? IdentifierZPLM { get; set; }
}

public sealed class ProjectCreateRequest
{
    public VisaryRef? Author { get; set; }
    public string? Date { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Type { get; set; }
    public string? IdentifierZPLM { get; set; }
    public string? IdentifierKK { get; set; }
    public VisaryRef? Town { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Тело POST <c>/api/visary/crud/organization</c>. Минимальный набор для создания
/// новой записи Organization из импорта Финмодели по ИНН: наименование +
/// внешний идентификатор (<see cref="ClientID"/>=ИНН, Visary использует это
/// поле как PIN застройщика — см. <see cref="ListView.IListViewClient.GetOrganizationsByClientIdAsync"/>).
/// </summary>
public sealed class OrganizationCreateRequest
{
    public string? Title { get; set; }
    /// <summary>ИНН — клиентский идентификатор организации, по нему же ищется в listview.</summary>
    public string? ClientID { get; set; }
    public string? INN { get; set; }
    public string? KPP { get; set; }
    public string? OGRN { get; set; }
}

/// <summary>
/// Тело POST <c>/api/visary/crud/deal</c>. Минимальный набор для создания сделки
/// в проекте из импорта Финмодели, когда pre-check
/// (<see cref="ListView.IListViewClient.GetDealsByProjectAsync"/>) не нашёл сделки
/// по паре <see cref="LmID"/> + <see cref="DocNumber"/>.
/// <para>
/// Visary ожидает указания проекта в ДВУХ местах одновременно: scalar
/// <c>ConstructionProjectID</c> и nested ref <c>ConstructionProject:{ID:…}</c>.
/// Это особенность серверного API — нельзя выбрать одно из двух (см. живой запрос
/// в doc 104 v1.1).
/// </para>
/// <para>
/// ⚠️ <b>Title — временный костыль.</b> Заказчик подтвердил, что Visary сейчас
/// требует непустой Title (иначе 400), но в будущем требование уйдёт. См. memory
/// entry <c>project_finmodel_deal_create_title_hack</c>. Когда сервер начнёт
/// принимать <c>null</c>/отсутствие Title — удалить и поле из DTO, и подстановку
/// «-» в <c>FinModelImportMapper.EnsureDealExistsInProjectAsync</c>.
/// </para>
/// </summary>
public sealed class DealCreateRequest
{
    public int ConstructionProjectID { get; set; }
    public VisaryRef? ConstructionProject { get; set; }
    public string? DocNumber { get; set; }
    public string? LmID { get; set; }
    /// <summary>Временно обязателен в Visary; см. XML-doc класса.</summary>
    public string? Title { get; set; }
}

public sealed class IndicatorValuePatchRequest
{
    // Включено в body для optimistic locking — `forceUpdate=false` требует, чтобы клиент
    // прислал актуальный RowVersion. Берётся из `GetIndicatorValueByIdAsync(...)`.
    public int ID { get; set; }
    public long RowVersion { get; set; }

    public double? Value { get; set; }
    public double? PlanValue { get; set; }
    public double? ForecastValue { get; set; }
}

public sealed class CadastralAreaCreateRequest
{
    public double? Area { get; set; }
    public string? CadastralNum { get; set; }
    public VisaryRef? LandCategory { get; set; }
    public List<CadastralUseTypeRef>? UseTypes { get; set; }
}

public sealed class CadastralUseTypeRef
{
    public VisaryRef? Object { get; set; }
}

public sealed class CadastralAreaPatchRequest
{
    public int ID { get; set; }
    public long RowVersion { get; set; }
    public string? UnstructuredArea { get; set; }
    public double? Area { get; set; }
    public string? CadastralNum { get; set; }
    public string? EGRNNumber { get; set; }
    public int? ParentID { get; set; }
}

public sealed class PercentBetCreateRequest
{
    public string? LmID { get; set; }
    public int? BaseRateType { get; set; }
    public int? PercentKind { get; set; }
    public VisaryRef? Deal { get; set; }
    public double? Rate { get; set; }
    public double? CommissionSum { get; set; }
    public VisaryRef? Currency { get; set; }
    public double? StandardRate { get; set; }
    public double? SpecialRate { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public VisaryRef? PaymentCurrency { get; set; }
    public double? BasePart { get; set; }
    public double? FloatRateMin { get; set; }
    public double? FloatRateMax { get; set; }
}

public sealed class SectionCreateRequest
{
    public int ConstructionSiteID { get; set; }
    public VisaryRef? ConstructionSite { get; set; }
    public VisaryRef? Type { get; set; }
    public VisaryRef? BuildingMaterial { get; set; }
    public int? Stage { get; set; }
    public string? Title { get; set; }
}

public sealed class RoomCreateRequest
{
    public int SiteID { get; set; }
    public VisaryRef? Site { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Kind { get; set; }
    public string? ExplicationNumber { get; set; }
    public VisaryRef? Section { get; set; }
    public string? BuildingSection { get; set; }
    public string? Floor { get; set; }
    public double? CalculatedCostPerM { get; set; }
    public double? MarketCostPerM { get; set; }
    public double? ZalogCostPerM { get; set; }
    public int? RoomsNumber { get; set; }
    /// <summary>
    /// Студия: см. <see cref="RoomPatchRequest.IsStudio"/>.
    /// </summary>
    public bool? IsStudio { get; set; }
    public double? ProjectArea { get; set; }
    /// <summary>
    /// Заполняется для нежилых помещений (Kind.RoomCategory ≠ 0). См.
    /// <see cref="RoomPatchRequest.TotalArea"/> — там же объяснение.
    /// </summary>
    public double? TotalArea { get; set; }
    public double? TotalAreaWithoutSummerRoom { get; set; }
    public double? SummerRoomArea { get; set; }
    public string? UniqueNumber { get; set; }
    public string? CadastralNumber { get; set; }
    public string? Description { get; set; }
    public double? Cost { get; set; }
    public double? CostForOne { get; set; }
}

public sealed class ShareAgreementCreateRequest
{
    public int RoomID { get; set; }
    public VisaryRef? Room { get; set; }
    public VisaryRef? Project { get; set; }
    public VisaryRef? Site { get; set; }
    public string? Title { get; set; }
    public string? Number { get; set; }
    public VisaryRef? RoomKindRef { get; set; }
    public string? ProjectNumber { get; set; }
    public string? StageNumber { get; set; }
    public string? ConditionalNumber { get; set; }
}

/// <summary>
/// POST <c>/api/visary/crud/wbs</c> — создание главы или подстатьи ИСР.
/// • Глава: <c>ParentID=null, Parent=null</c>, ProjectID указывает на проект.
/// • Подстатья: <c>ParentID</c> = ID родителя (главы или другой статьи).
/// Code (КБК, например "1.1.") присваивается сервером автоматически.
/// </summary>
public sealed class WbsCreateRequest
{
    public int ProjectID { get; set; }
    public VisaryRef? Project { get; set; }
    public int? ParentID { get; set; }
    public VisaryRef? Parent { get; set; }
    public int? ConstructionSiteID { get; set; }
    public VisaryRef? ConstructionSite { get; set; }
    public string? Title { get; set; }
    public double? DeclaredSum { get; set; }
    public double? ConfirmedSum { get; set; }
}

/// <summary>
/// PATCH <c>/api/visary/crud/wbs/{id}?forceUpdate=true</c> — обновление сумм
/// существующей подстатьи ИСР при повторном импорте бюджета. Используется
/// <c>forceUpdate=true</c>, чтобы не делать дополнительный GET ради RowVersion
/// (тот же подход, что в <see cref="RoomPatchRequest"/> / <see cref="ShareAgreementPatchRequest"/>).
/// Поля <c>ID</c>/<c>RowVersion</c> — nullable + <c>WhenWritingNull</c>: они не должны
/// попадать в тело при <c>forceUpdate=true</c>, иначе Visary падает 500
/// «Can not add property RowVersion to JObject».
/// </summary>
public sealed class WbsPatchRequest
{
    public int? ID { get; set; }
    public long? RowVersion { get; set; }
    public string? Title { get; set; }
    public double? DeclaredSum { get; set; }
    public double? ConfirmedSum { get; set; }
}

/// <summary>
/// POST <c>/api/visary/crud/typedimportwbs</c> — создание задания TypedJournal-импорта
/// бюджета (WBS) из XLSX, предварительно загруженного в файловое хранилище.
/// Тело сформировано по HAR-файлу <c>Context/har импорт бюджета.txt</c>.
///
/// <para>
/// Ключевое поле — <see cref="File"/>: это <b>link-токен</b>, полученный через
/// <c>POST /api/files/link/file_link/by_id</c> (см. <c>IFileStorageClient.GetFileLinkAsync</c>).
/// </para>
/// <para>
/// <see cref="ImportType"/> = 10 в test-окружении — внутренний код Visary для
/// «Бюджет/WBS». При перевозке на другие стенды сверять со справочником.
/// </para>
/// </summary>
public sealed class TypedImportWbsCreateRequest
{
    public int ProjectID { get; set; }
    public VisaryRef? Project { get; set; }
    public int ConstructionSiteID { get; set; }
    public VisaryRef? ConstructionSite { get; set; }
    /// <summary>Код типа импорта в Visary; 10 = «Бюджет/WBS».</summary>
    public int ImportType { get; set; } = 10;
    /// <summary>С какой строки XLSX начинать парсинг (0 = с первой строки данных).</summary>
    public int StartLine { get; set; } = 0;
    /// <summary>Имя листа. Пустая строка = первый лист книги (Visary сам подхватывает).</summary>
    public string SheetName { get; set; } = string.Empty;
    /// <summary>Link-токен файла из ФХ (НЕ URL, НЕ ID — opaque-строка).</summary>
    public string File { get; set; } = string.Empty;
}

/// <summary>
/// Снэпшот записи <c>typedimportwbs</c> для опроса статуса фоновой обработки импорта
/// бюджета в Visary. Заполняется ответом <c>GET /api/visary/crud/typedimportwbs/{id}</c>
/// (а также крошечной формой при создании через <c>POST /api/visary/crud/typedimportwbs</c>,
/// где приходит только <see cref="ID"/>).
/// </summary>
/// <remarks>
/// Поле <see cref="Status"/> объявлено как <see cref="System.Text.Json.JsonElement"/>?,
/// потому что Visary шлёт его разными типами (наблюдали: число, строка, объект-обёртка
/// <c>{ID, Title}</c>). Если объявить <c>string?</c>, десериализатор бросит JsonException
/// на каждом polling-запросе → polling «зависает» до таймаута даже когда импорт давно
/// завершён успешно (см. doc 94 v1.2 и инцидент с typedimportwbs ID=9442). Классификация
/// статуса (success/failure/in-progress) делается <c>BudgetVisaryUploader</c> поверх
/// <see cref="JsonElement"/>: строка → корни слов «успеш»/«предупреж»/«ошибк»; объект →
/// читаем поле Title/Name и применяем те же корни; число → пока неизвестна полная
/// таблица кодов Visary, поэтому логируем raw и считаем «in-progress» (до следующей
/// итерации/таймаута). См. тот же паттерн в <c>RoomFull.RoomCategory</c>,
/// <c>ConstructionSiteFull.Status</c>.
/// <para/>
/// <see cref="CountErrors"/> и <see cref="CountWarnings"/> nullable: при создании
/// записи Visary возвращает только ID — остальные поля приходят на последующих GET.
/// </remarks>
public sealed class TypedImportWbsRaw
{
    public int ID { get; set; }
    public int? ImportType { get; set; }
    public string? File { get; set; }
    public System.Text.Json.JsonElement? Status { get; set; }
    public int? CountErrors { get; set; }
    public int? CountWarnings { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? FinishDate { get; set; }
}

/// <summary>
/// Числовые коды для <see cref="CostItemRaw.Status"/> (поле <c>Status</c> сущности
/// CostItem). Значение <see cref="Plan"/> = 70 — единственное, наблюдавшееся в HAR
/// (Context/har ГФ.txt) при ручном добавлении ГФ через UI Visary. Импорт ГФ из
/// Финмодели всегда создаёт записи со статусом «План».
/// </summary>
public static class CostItemStatus
{
    public const int Plan = 70;
}

/// <summary>
/// POST <c>/api/visary/crud/costitem</c> — создание строки графика финансирования
/// (одна запись = одна квартальная сумма по подстатье <see cref="Visary.Api.Dto.WbsRaw"/>).
/// Тело сформировано по HAR <c>Context/har ГФ.txt</c>:
/// <code>{ "WBSID": 168482, "WBS": { "ID": 168482 }, "PlanSum": 2222000,
///         "PlanPeriod": { "Start": "2026-07-01T00:00:00Z", "End": "2026-09-30T00:00:00Z" },
///         "Status": 70 }</code>
/// • Дубликат "WBS"+"WBSID" — тот же паттерн, что в <see cref="WbsCreateRequest"/>.
/// • <c>PlanQuarter</c>/<c>PlanYear</c>/<c>PlanMonth</c> Visary derives из <c>PlanPeriod</c>;
///   в POST НЕ передаём, чтобы не плодить рассинхрон.
/// • Идемпотентности на сервере нет — повторный POST даст дубликат. Caller обязан
///   pre-check'ить через <see cref="ListView.IListViewClient.GetCostItemsByWbsAsync"/>.
/// </summary>
public sealed class CostItemCreateRequest
{
    public int WBSID { get; set; }
    public VisaryRef? WBS { get; set; }
    public double PlanSum { get; set; }
    public CostItemPeriod? PlanPeriod { get; set; }
    public int Status { get; set; } = CostItemStatus.Plan;
}

/// <summary>
/// PATCH <c>/api/visary/crud/costitem/{id}?forceUpdate=true</c> — обновление суммы
/// у существующей строки ГФ (по найденной в <see cref="ListView.IListViewClient.GetCostItemsByWbsAsync"/>).
/// Тот же приём, что в <see cref="WbsPatchRequest"/>/<see cref="RoomPatchRequest"/>:
/// <c>ID</c>/<c>RowVersion</c> nullable + <c>WhenWritingNull</c> — иначе Visary падает
/// 500 «Can not add property RowVersion to JObject».
/// Период менять обычно не нужно (он же часть бизнес-ключа дедупликации); если
/// потребуется — поле опционально.
/// </summary>
public sealed class CostItemPatchRequest
{
    public int? ID { get; set; }
    public long? RowVersion { get; set; }
    public double? PlanSum { get; set; }
    public CostItemPeriod? PlanPeriod { get; set; }
}
