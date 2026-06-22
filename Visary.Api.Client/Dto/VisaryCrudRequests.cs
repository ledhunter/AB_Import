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
    /// <summary>
    /// Признак «Вывод» помещения (заполняется из колонки «Вывод (да/нет)»
    /// импорта «Помещения»). Visary хранит как <c>bool?</c>. Поиск перед
    /// PATCH по этому полю НЕ выполняется (см. doc 113) — пишем как есть.
    /// </summary>
    public bool? IsWithdrawn { get; set; }
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

    // ── Опциональные поля импорта «Помещения» (doc 113) ─────────────────
    /// <summary>См. <see cref="ShareAgreementCreateRequest.Cost"/>.</summary>
    public double? Cost { get; set; }
    /// <summary>См. <see cref="ShareAgreementCreateRequest.DepositedAmount"/>.</summary>
    public double? DepositedAmount { get; set; }
    /// <summary>См. <see cref="ShareAgreementCreateRequest.Date"/>.</summary>
    public string? Date { get; set; }
    /// <summary>См. <see cref="ShareAgreementCreateRequest.DepositorFullName"/>.</summary>
    public string? DepositorFullName { get; set; }
    /// <summary>См. <see cref="ShareAgreementCreateRequest.DeveloperPIN"/>.</summary>
    public string? DeveloperPIN { get; set; }
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
    /// <summary>Признак «Вывод»; см. <see cref="RoomPatchRequest.IsWithdrawn"/>.</summary>
    public bool? IsWithdrawn { get; set; }
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

    // ── Опциональные поля импорта «Помещения» (doc 113) ─────────────────
    // Заполняются из соответствующих колонок XLSX. Поиск перед CREATE/PATCH
    // по этим полям НЕ выполняется — пишем как есть (см. doc 113).
    /// <summary>Колонка «Стоимость ДКП, руб.» / «Сумма депонирования, руб.».</summary>
    public double? Cost { get; set; }
    /// <summary>Колонка «Сумма на эскроу».</summary>
    public double? DepositedAmount { get; set; }
    /// <summary>Колонка «Дата ДДУ». ISO-строка <c>yyyy-MM-dd</c> — именно так
    /// принимает Visary UI (`POST /crud/shareagreement` шлёт `"Date":"2026-05-26"`),
    /// см. doc 113 v1.4. Excel-serial из ячейки конвертируется в строку через
    /// <see cref="DateTime.FromOADate"/> в маппере.</summary>
    public string? Date { get; set; }
    /// <summary>Колонка «ФИО покупателя».</summary>
    public string? DepositorFullName { get; set; }
    /// <summary>Колонка «ПИН застройщика» — кладём как есть, без поиска организации.</summary>
    public string? DeveloperPIN { get; set; }
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

/// <summary>
/// POST <c>/api/visary/crud/fmmodel</c> — создание Финмодели по проекту/объекту.
/// Импортируется из второго файла «Финмодель» (лист «План»): краевые значения
/// (Год + Квартал) превращаются в <see cref="PeriodStart"/>/<see cref="PeriodEnd"/>
/// формата <c>"{Year}Q{N}"</c>. <see cref="Title"/> — фиксированная константа
/// «Модель из эксель файла» (видимое имя в Visary).
/// Идемпотентности на сервере нет: повторный POST породит дубликат.
/// Caller обязан pre-check'ить через <see cref="ListView.IListViewClient.FindFmModelsAsync"/>.
/// См. doc_project/110-finmodel-plan-and-fmmodel.md.
/// </summary>
public sealed class FmModelCreateRequest
{
    public string Title { get; set; } = null!;
    public string? ProjectCode { get; set; }
    public int ABProjectID { get; set; }
    public int ABConstructionSiteID { get; set; }
    public string PeriodStart { get; set; } = null!;
    public string PeriodEnd { get; set; } = null!;
    /// <summary>
    /// Квартал ввода в эксплуатацию (формат <c>"{Year}Q{N}"</c>). Опционально:
    /// если в основном файле строка «Этап 1.» / колонка «Ввод в эксплуатацию»
    /// не нашлись — отправляется null, остальные поля POST работают как раньше.
    /// См. <see cref="FmModelRaw.CommisioningPeriod"/> и doc 139 v1.3.
    /// </summary>
    public string? CommisioningPeriod { get; set; }
}

/// <summary>
/// POST <c>/api/visary/crud/fmmodelversion</c> — создание версии Финмодели. Импорт
/// «Финмодель» создаёт одну версию со стандартным <see cref="Title"/>
/// «Версия - Перенос из Эксель» сразу после <see cref="FmModelCreateRequest"/>,
/// и наполняет её записями <see cref="InputDataCreateRequest"/>.
/// Тело по HAR заказчика:
/// <code>{"FMModelID":48,"FMModel":{"ID":48},"Title":"Версия - Перенос из Эксель"}</code>
/// Идемпотентности на сервере нет — caller обязан pre-check'ить через
/// <see cref="ListView.IListViewClient.GetFmModelVersionsByModelAsync"/>.
/// См. doc_project/112-finmodel-version-and-inputdata.md.
/// </summary>
public sealed class FmModelVersionCreateRequest
{
    public int FMModelID { get; set; }
    public VisaryRef FMModel { get; set; } = null!;
    public string Title { get; set; } = null!;
}

/// <summary>
/// POST <c>/api/visary/crud/inputdata</c> — создание записи «Входные данные»
/// внутри версии Финмодели. Тело по HAR заказчика:
/// <code>{"FMModelVersionID":217,"FMModelVersion":{"ID":217},"FMPeriod":"2024Q1",
///       "Code":{"Title":"Продажа квартиры (план)","ID":20},
///       "Summ":243685102,"Amount":2459.85,"Cost":99065.03,"Percent":0}</code>
/// <para/>
/// • <see cref="Code"/> — VisaryRef в справочник <c>inputdatacode</c> (резолвится
///   через <see cref="ListView.IListViewClient.ListInputDataCodesAsync"/>).
/// • <see cref="Percent"/> по контракту заказчика — всегда 0.
/// • Импортер создаёт по одной записи на (RoomKind × Квартал) — нулевые периоды
///   тоже отправляются (план = 0), как требует заказчик (doc 112 §3).
/// </summary>
public sealed class InputDataCreateRequest
{
    public int FMModelVersionID { get; set; }
    public VisaryRef FMModelVersion { get; set; } = null!;
    public string FMPeriod { get; set; } = null!;
    public VisaryRef Code { get; set; } = null!;
    public double Summ { get; set; }
    public double Amount { get; set; }
    public double Cost { get; set; }
    public double Percent { get; set; }
}

/// <summary>
/// POST <c>/api/visary/crud/projectaudit</c> — создание «Заключения». Тело по HAR:
/// <code>{"Date":"2026-06-17T08:41:39Z","Status":10,"Project":{"ID":4653},
///        "ProjectID":4653,"Stage":110}</code>
/// • <see cref="Stage"/>=110 — «Итоговое заключение КА БП7» (единственный тип,
///   с которым работает импорт). • <see cref="Status"/>=10 — начальный статус.
/// • <see cref="ConstructionSite"/> опционален: HAR сервер сам подцепил Site
///   по проекту, но если нужно зафиксировать конкретный объект — передаём его.
///   Импорт всегда явно передаёт <see cref="ConstructionSite"/>, чтобы исключить
///   попадание Заключения на чужой Site проекта.
/// См. doc_project/139-finmodel-installments-and-conclusion.md.
/// </summary>
public sealed class ProjectAuditCreateRequest
{
    public string Date { get; set; } = null!;
    public int Status { get; set; } = 10;
    public int Stage { get; set; } = 110;
    public int ProjectID { get; set; }
    public VisaryRef Project { get; set; } = null!;
    public VisaryRef? ConstructionSite { get; set; }
}

/// <summary>
/// POST <c>/api/visary/crud/dataforfm</c> — одна «строка ФМ» на (RoomKind × DataSet).
/// Тело по HAR:
/// <code>{"DataSetForFMID":8030,"DataSetForFM":{"ID":8030},
///        "Title":"Данные по Квартирам",
///        "RoomKind":{"Title":"Квартира","ID":3}}</code>
/// Indicator не передаём (заказчик: «не надо заполнять поле Indicator
/// и искать для него значения»). См. doc 141.
/// </summary>
public sealed class DataForFmCreateRequest
{
    public int DataSetForFMID { get; set; }
    public VisaryRef DataSetForFM { get; set; } = null!;
    public string Title { get; set; } = null!;
    public VisaryRef RoomKind { get; set; } = null!;
}

/// <summary>
/// PATCH <c>/api/visary/crud/datasetforfm/{id}?forceUpdate=false</c> — запись долей
/// рассрочек по одной схеме (равномерная / единовременная / ДКП).
/// Один PATCH = одна схема (одна тройка полей <c>{Prefix}OwnShare</c>,
/// <c>{Prefix}PostpShare</c>, <c>{Prefix}RoomKinds</c>). Если в файле включены
/// сразу несколько схем — мапер делает несколько PATCH-ов подряд, всякий раз
/// перечитывая <see cref="RowVersion"/>.
/// <para/>
/// Тело гибкое: точные имена полей зависят от схемы (см. HAR;
/// <see cref="OwnSharePropertyName"/>/<see cref="PostpSharePropertyName"/>/
/// <see cref="RoomKindsPropertyName"/> определяются caller-ом). Из-за этого
/// сериализатор делает payload вручную, а не через сильно типизированный объект.
/// См. doc_project/139-finmodel-installments-and-conclusion.md.
/// </summary>
public sealed class DataSetForFmInstallmentsPatchRequest
{
    public int ID { get; set; }
    public long RowVersion { get; set; }
    public string OwnSharePropertyName { get; set; } = null!;
    public string PostpSharePropertyName { get; set; } = null!;
    public string RoomKindsPropertyName { get; set; } = null!;
    public double? OwnShare { get; set; }
    public double? PostpShare { get; set; }
    public IReadOnlyList<VisaryRef> RoomKinds { get; set; } = Array.Empty<VisaryRef>();
}

/// <summary>
/// POST <c>/api/visary/crud/dealpercentbet</c> — создание процентной ставки по сделке.
/// Тело по примеру заказчика (см. doc 139 v1.4 + doc 144 v1.1 — `PercentKind`
/// не отправляем, `Rate` отправляем как раньше):
/// <code>{"DealID":91,"Deal":{"ID":91},"LmID":"18-09-2025-15-50-51",
///        "Rate":100,"PercentBetType":{"Title":"Фиксированная (базовая)","ID":7}}</code>
/// <para/>
/// • <see cref="LmID"/> — строковый идентификатор формата
///   <c>"dd-MM-yyyy-HH-mm-ss-fff-{Code}-{idx}"</c> (момент импорта + код + индекс
///   для UNIQUE-индекса `UX_DealPercentBet_LmID`).
/// • <see cref="Rate"/> — значение в процентах (число > 1 = «как есть»,
///   ≤ 1 = доля → парсер ×100).
/// • <see cref="PercentBetType"/> — ссылка на справочник <c>percentbettype</c>
///   (резолвится по <c>Code</c> через
///   <see cref="ListView.IListViewClient.FindPercentBetTypeByCodeAsync"/>).
/// <para/>
/// Поле <c>PercentKind</c> сознательно убрано (doc 144 v1.1): «Вид ставки»
/// (Floating/Fixed) Visary определяет сам по типу ставки <see cref="PercentBetType"/>;
/// импорт не должен его проставлять.
/// </summary>
public sealed class DealPercentBetCreateRequest
{
    public int DealID { get; set; }
    public VisaryRef Deal { get; set; } = null!;
    public string LmID { get; set; } = null!;
    public double Rate { get; set; }
    public VisaryRef PercentBetType { get; set; } = null!;
}

/// <summary>
/// POST <c>/api/visary/crud/dealmonthlydata</c> — помесячные данные по сделке
/// (раздел «Инвестиционный кредит: Этап 1» листа Outputs).
/// <para/>
/// Тело по примеру заказчика (см. doc 142):
/// <code>{"Deal":{"ID":91},"Year":2025,"Month":4,"PrincipalDebtAmount":1,
///        "SimpleInterestAmount":2,"CapitalizedInterestAmount":3,
///        "PrincipalRepaymentAmount":4,"InterestRepaymentAmount":5}</code>
/// <para/>
/// Значения — суммы в рублях (после умножения парсером на единицу измерения
/// строки: тыс.руб → ×1 000, млн руб → ×1 000 000, руб → ×1). Пустая ячейка,
/// прочерк или 0 — отправляются как 0.
/// </summary>
public sealed class DealMonthlyDataCreateRequest
{
    public VisaryRef Deal { get; set; } = null!;
    public int Year { get; set; }
    public int Month { get; set; }
    public double PrincipalDebtAmount { get; set; }
    public double SimpleInterestAmount { get; set; }
    public double CapitalizedInterestAmount { get; set; }
    public double PrincipalRepaymentAmount { get; set; }
    public double InterestRepaymentAmount { get; set; }
}
