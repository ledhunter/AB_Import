using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Visary.Api.Common;
using Visary.Api.Dto;

namespace Visary.Api.CRUD;

public interface ICrudClient
{
    // ─── Update / Create / Patch / Link (модифицирующие) ─────────────────────
    Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId, int finishingMaterialId, CancellationToken ct = default);

    Task<bool> UpdateSiteEstateClassAsync(
        int siteId, int estateClassId, CancellationToken ct = default);

    Task<bool> UpdateSiteAddressAsync(
        int siteId, string address, CancellationToken ct = default);

    Task<bool> PatchSiteAsync(
        int siteId, SitePatchRequest request, CancellationToken ct = default);

    Task<ConstructionSiteRaw> CreateSiteAsync(
        SiteCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchProjectAsync(
        int projectId, ProjectPatchRequest request, CancellationToken ct = default);

    Task<ConstructionProjectRaw> CreateProjectAsync(
        ProjectCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchIndicatorValueAsync(
        int valueId, IndicatorValuePatchRequest request, CancellationToken ct = default);

    Task<CadastralAreaRaw> CreateCadastralAreaAsync(
        CadastralAreaCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchCadastralAreaAsync(
        int areaId, CadastralAreaPatchRequest request, CancellationToken ct = default);

    Task<bool> LinkCadastralAreaToSiteAsync(
        int siteId, int areaId, CancellationToken ct = default);

    Task<PercentBetRaw> CreatePercentBetAsync(
        PercentBetCreateRequest request, CancellationToken ct = default);

    Task<ConstructionSectionRaw> CreateSectionAsync(
        SectionCreateRequest request, CancellationToken ct = default);

    Task<RoomRaw> CreateRoomAsync(
        RoomCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchRoomAsync(
        int roomId, RoomPatchRequest request, CancellationToken ct = default);

    Task<ShareAgreementRaw> CreateShareAgreementAsync(
        ShareAgreementCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchShareAgreementAsync(
        int shareAgreementId, ShareAgreementPatchRequest request, CancellationToken ct = default);

    Task<WbsRaw> CreateWbsAsync(
        WbsCreateRequest request, CancellationToken ct = default);

    Task<bool> PatchWbsAsync(
        int wbsId, WbsPatchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создать строку графика финансирования (<c>costitem</c>) — одна квартальная сумма
    /// по подстатье ИСР. См. <see cref="CostItemCreateRequest"/>.
    /// Идемпотентности на сервере нет: caller обязан pre-check'ить уже существующие
    /// записи через <see cref="ListView.IListViewClient.GetCostItemsByWbsAsync"/>.
    /// </summary>
    Task<CostItemRaw> CreateCostItemAsync(
        CostItemCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// PATCH строки ГФ. <c>forceUpdate=true</c> — без предварительного GET ради RowVersion
    /// (тот же приём, что для Room/ShareAgreement/WBS).
    /// </summary>
    Task<bool> PatchCostItemAsync(
        int costItemId, CostItemPatchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создать <c>fmmodel</c> — Финмодель проекта/объекта. POST <c>/api/visary/crud/fmmodel</c>.
    /// Идемпотентности на сервере нет: перед вызовом — pre-check через
    /// <see cref="ListView.IListViewClient.FindFmModelsAsync"/> по (ABProjectID, ABConstructionSiteID).
    /// См. doc_project/110-finmodel-plan-and-fmmodel.md.
    /// </summary>
    Task<FmModelRaw> CreateFmModelAsync(
        FmModelCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создать версию Финмодели (<c>fmmodelversion</c>) — дочерний объект Финмодели,
    /// куда складываются «входные данные» (<c>inputdata</c>) по видам помещений.
    /// POST <c>/api/visary/crud/fmmodelversion</c>. Идемпотентности на сервере нет —
    /// caller обязан pre-check'ить через
    /// <see cref="ListView.IListViewClient.GetFmModelVersionsByModelAsync"/>.
    /// См. doc_project/112-finmodel-version-and-inputdata.md.
    /// </summary>
    Task<FmModelVersionRaw> CreateFmModelVersionAsync(
        FmModelVersionCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создать запись «Входные данные» (<c>inputdata</c>) внутри версии Финмодели.
    /// POST <c>/api/visary/crud/inputdata</c>. Импорт создаёт по одной записи
    /// на (Квартал × RoomKind); идемпотентности на сервере нет — caller обязан
    /// pre-check'ить уже привязанные записи через
    /// <see cref="ListView.IListViewClient.GetInputDataByVersionAsync"/>.
    /// </summary>
    Task<InputDataRaw> CreateInputDataAsync(
        InputDataCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Привязка существующей <c>inputdata</c>-записи к версии Финмодели через
    /// listview-onetomany эндпоинт (по HAR заказчика):
    /// <c>POST /api/visary/listview/inputdata/onetomany/FMModelVersion?associationId={versionId}</c>
    /// с body, содержащим <c>Filter ["ID","=",{inputDataId}]</c>. Возвращает <c>true</c>
    /// при успехе. Используется сразу после <see cref="CreateInputDataAsync"/>.
    /// </summary>
    Task<bool> LinkInputDataToVersionAsync(
        int versionId, int inputDataId, CancellationToken ct = default);

    /// <summary>
    /// Создать «Заключение» (<c>projectaudit</c>). POST <c>/api/visary/crud/projectaudit</c>.
    /// Импорт Финмодели после успешных Бюджета+ГФ создаёт Заключение типа
    /// «Итоговое заключение КА БП7» (<see cref="ProjectAuditCreateRequest.Stage"/>=110).
    /// Идемпотентности на сервере нет — caller pre-check'ит через
    /// <see cref="ListView.IListViewClient.FindProjectAuditsBySiteAsync"/>.
    /// См. doc_project/139-finmodel-installments-and-conclusion.md.
    /// </summary>
    Task<ProjectAuditRaw> CreateProjectAuditAsync(
        ProjectAuditCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создать «строку ФМ» (<c>dataforfm</c>) — одна на вид помещения. POST
    /// <c>/api/visary/crud/dataforfm</c>. Идемпотентности на сервере нет; caller
    /// pre-check'ит через
    /// <see cref="ListView.IListViewClient.GetDataForFmByDataSetAsync"/>.
    /// </summary>
    Task<DataForFmRaw> CreateDataForFmAsync(
        DataForFmCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// PATCH «Набор данных для ФМ» (<c>datasetforfm</c>) — записать тройку полей
    /// одной схемы рассрочки (равномерная / единовременная / ДКП). Сервер требует
    /// <c>RowVersion</c> в теле — caller обязан получить его через
    /// <see cref="GetDataSetForFmByIdAsync"/> непосредственно перед PATCH.
    /// При повторном PATCH в рамках одного импорта (для второй/третьей схемы)
    /// <c>RowVersion</c> необходимо перечитывать заново.
    /// </summary>
    Task<bool> PatchDataSetForFmInstallmentsAsync(
        DataSetForFmInstallmentsPatchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Прочитать «Набор данных для ФМ» по ID — нужен для получения актуального
    /// <see cref="DataSetForFmRaw.RowVersion"/> перед PATCH-ом рассрочек.
    /// GET <c>/api/visary/crud/datasetforfm/{id}</c>.
    /// </summary>
    Task<DataSetForFmRaw> GetDataSetForFmByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Создать «Процентную ставку по сделке» (<c>dealpercentbet</c>). POST
    /// <c>/api/visary/crud/dealpercentbet</c>. Импорт Финмодели вызывает этот метод
    /// один раз на каждую включённую ставку «Этап 1» (LM10/LM20/LM30/LM40) перед
    /// созданием Заключения. Идемпотентности на сервере нет — повторный импорт
    /// породит вторую запись на ту же (Deal, PercentKind); в первой версии не
    /// делаем pre-check (поведение озвучено заказчиком: «каждый импорт = новые
    /// ставки», по аналогии с projectaudit).
    /// </summary>
    Task<DealPercentBetRaw> CreateDealPercentBetAsync(
        DealPercentBetCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создать «Помесячные данные по сделке» (<c>dealmonthlydata</c>). POST
    /// <c>/api/visary/crud/dealmonthlydata</c>. Импорт Финмодели вызывает один
    /// раз на (Deal, ТекущийГод, ТекущийМесяц) с 5 числовыми полями из раздела
    /// «Инвестиционный кредит: Этап 1» листа Outputs. Идемпотентности на сервере
    /// нет (по аналогии с <see cref="CreateDealPercentBetAsync"/> и projectaudit).
    /// </summary>
    Task<DealMonthlyDataRaw> CreateDealMonthlyDataAsync(
        DealMonthlyDataCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создать запись <c>typedimportwbs</c> — TypedJournal-задание импорта бюджета (XLSX)
    /// из файла, уже загруженного в файловое хранилище. Поле <c>File</c> в request — это
    /// link-токен, возвращённый <see cref="FileStorage.IFileStorageClient.GetFileLinkAsync"/>.
    /// Visary триггерит фоновую джобу импорта; статус опрашивается через
    /// <see cref="GetTypedImportWbsByIdAsync"/>.
    /// </summary>
    Task<TypedImportWbsRaw> CreateTypedImportWbsAsync(
        TypedImportWbsCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Прочитать текущее состояние <c>typedimportwbs</c> (поле <see cref="TypedImportWbsRaw.Status"/>
    /// и счётчики ошибок/предупреждений). Используется автоматическим pipeline загрузки
    /// бюджета для polling-а статуса перед запуском импорта ГФ Главы 1.
    /// HAR (Context/har импорт бюджета.txt) подтверждает endpoint
    /// <c>GET /api/visary/crud/typedimportwbs/{id}</c>.
    /// </summary>
    Task<TypedImportWbsRaw> GetTypedImportWbsByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Привязывает Organization участником Site. Соответствует шагу из puml
    /// «Добавить в Участники Объекта найденную Организацию с ролью Застройщик».
    /// Реализован через manytomany-link, как и <see cref="LinkCadastralAreaToSiteAsync"/>.
    /// ⚠️ Backend Visary ставит роль на основе типа связи; если роль на стенде задаётся
    /// иначе (отдельной сущностью ParticipantRole), потребуется дополнительный вызов.
    /// </summary>
    Task<bool> LinkOrganizationToSiteAsync(
        int siteId, int organizationId, CancellationToken ct = default);

    /// <summary>
    /// Создать запись <c>organization</c> в Visary. Используется импортом Финмодели,
    /// когда по ИНН (ClientID) не нашлось существующей организации — создаём новую
    /// с парой полей Title + ClientID, затем привязываем к проекту через
    /// <see cref="CreateProjectManagementAsync"/>.
    /// POST <c>/api/visary/crud/organization</c>.
    /// </summary>
    Task<OrganizationRaw> CreateOrganizationAsync(
        OrganizationCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создать сделку (<c>deal</c>) внутри проекта. POST <c>/api/visary/crud/deal</c>.
    /// Используется FinModel-импортом, когда pre-check сделки в проекте
    /// (см. <see cref="ListView.IListViewClient.GetDealsByProjectAsync"/>) не нашёл
    /// сделки по паре <c>LmID</c>+<c>DocNumber</c> — сделка создаётся прямо здесь,
    /// чтобы продолжить применение параметров Объекта. См. doc 104 v1.1.
    /// </summary>
    Task<DealRaw> CreateDealAsync(
        DealCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Записать привязку организации к группе компаний (поле <c>Group</c>).
    /// Используется FinModel-импортом (doc 100): когда по ИНН найдена/создана
    /// организация-застройщик и в файле указано наименование ГК, мы PATCH-им
    /// её с <c>Group: {ID, Title, Hidden:false}</c>. RowVersion и текущее значение
    /// <c>Group</c> читаются через GET <c>/crud/organization/{id}</c> (<see cref="OrganizationFull"/>).
    /// </summary>
    /// <param name="organizationId">ID организации в Visary.</param>
    /// <param name="groupId">ID группы компаний (из <c>companygroup</c>).</param>
    /// <param name="groupTitle">Наименование группы (для тела запроса, как принято в Visary UI).</param>
    Task<bool> UpdateOrganizationGroupAsync(
        int organizationId, int groupId, string groupTitle, CancellationToken ct = default);

    /// <summary>
    /// Создать запись <c>projectmanagement</c> (связка «Проект ↔ Организация ↔ Роль»).
    /// Соответствует POST <c>/api/visary/crud/projectmanagement</c>.
    /// </summary>
    Task<ProjectManagementRaw> CreateProjectManagementAsync(
        ProjectManagementCreateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Привязать существующую запись <c>projectmanagement</c> к объекту строительства
    /// через manytomany. Соответствует POST
    /// <c>/api/visary/listview/constructionsite/manytomany/projectmanagement/link?associationId={siteId}&amp;ids={pmId}</c>.
    /// </summary>
    Task<bool> LinkProjectManagementToSiteAsync(
        int siteId, int projectManagementId, CancellationToken ct = default);

    // ─── GET by ID (чтение, через /api/visary/crud/{mnemonic}/{id}) ──────────
    // Возвращают *Full DTO с полным набором полей сущности — в отличие от
    // listview-методов, которые возвращают только подмножество, явно перечисленное в Columns[].
    Task<TEntity> GetByIdAsync<TEntity>(string mnemonic, int id, CancellationToken ct = default);

    Task<ConstructionProjectFull>            GetProjectByIdFullAsync(int id, CancellationToken ct = default);
    Task<ConstructionSiteFull>               GetSiteByIdFullAsync(int id, CancellationToken ct = default);
    Task<ConstructionSectionFull>            GetSectionByIdAsync(int id, CancellationToken ct = default);
    Task<ConstructionSiteIndicatorFull>      GetIndicatorByIdAsync(int id, CancellationToken ct = default);
    Task<ConstructionSiteIndicatorValueFull> GetIndicatorValueByIdAsync(int id, CancellationToken ct = default);
    Task<RoomFull>                           GetRoomByIdAsync(int id, CancellationToken ct = default);
    Task<CadastralAreaFull>                  GetCadastralAreaByIdAsync(int id, CancellationToken ct = default);
    Task<PercentBetFull>                     GetPercentBetByIdAsync(int id, CancellationToken ct = default);
    Task<ShareAgreementFull>                 GetShareAgreementByIdAsync(int id, CancellationToken ct = default);
    Task<DealFull>                           GetDealByIdAsync(int id, CancellationToken ct = default);
    Task<OrganizationFull>                   GetOrganizationByIdAsync(int id, CancellationToken ct = default);

    // ─── GET by ID для справочников ──────────────────────────────────────────
    Task<TownRaw>                GetTownByIdAsync(int id, CancellationToken ct = default);
    Task<RegionRaw>              GetRegionByIdAsync(int id, CancellationToken ct = default);
    Task<ProjectTypeRaw>         GetProjectTypeByIdAsync(int id, CancellationToken ct = default);
    Task<InflationCalcMethodRaw> GetInflationCalcMethodByIdAsync(int id, CancellationToken ct = default);
    Task<EstateClassRaw>         GetEstateClassByIdAsync(int id, CancellationToken ct = default);
    Task<BuildingMaterialRaw>    GetBuildingMaterialByIdAsync(int id, CancellationToken ct = default);
    Task<FinishingMaterialRaw>   GetFinishingMaterialByIdAsync(int id, CancellationToken ct = default);
    Task<RoomKindRaw>            GetRoomKindByIdAsync(int id, CancellationToken ct = default);
}

public sealed class CrudClient : VisaryHttpBase<CrudClient>, ICrudClient
{
    public CrudClient(
        HttpClient http,
        IOptionsMonitor<VisaryOptions> options,
        ILogger<CrudClient> log)
        : base(http, options, log) { }

    // ─── ConstructionSite ────────────────────────────────────────────────────

    public async Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId, int finishingMaterialId, CancellationToken ct)
    {
        // 1. GET текущий site по CRUD endpoint — нам нужен актуальный RowVersion (long)
        //    для optimistic locking. Listview-эндпоинт возвращает Version:DateTime,
        //    что для PATCH /crud/ не подходит — поэтому идём через /crud/.
        //    Используем переиспользуемый GetCrudByIdAsync<ConstructionSiteFull> из
        //    VisaryHttpBase (тот же, что и для остальных GET-методов в этом клиенте).
        var current = await GetCrudByIdAsync<ConstructionSiteFull>(
            VisaryMnemonics.Site, siteId, ct);
        if (current is null)
            throw new KeyNotFoundException($"ConstructionSite с ID={siteId} не найден в Visary");

        // 2. PATCH с RowVersion + FinishingMaterial как VisaryRef ({ ID }).
        //    forceUpdate=false — сервер сравнивает наш RowVersion с актуальным.
        //    Под forceUpdate=true Visary внутри пытается «дописать» поля в загруженный
        //    JObject и падает с "Property RowVersion already exists" — поэтому false,
        //    как в PatchSiteAsync. См. doc_project/56-site-finishing-material-update-crud.md.
        var body = new
        {
            ID = siteId,
            current.RowVersion,
            FinishingMaterial = new { ID = finishingMaterialId },
        };
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}/{siteId}?forceUpdate=false",
            body, $"{VisaryMnemonics.Site}/{siteId}", ct);

        _log.LogInformation("CrudClient.UpdateSiteFinishingMaterialAsync: siteId={SiteId} success", siteId);
        return true;
    }

    public async Task<bool> UpdateSiteEstateClassAsync(
        int siteId, int estateClassId, CancellationToken ct)
    {
        // Аналогично UpdateSiteFinishingMaterialAsync: GET текущий site (для RowVersion)
        // → PATCH /crud/{site}/{id}?forceUpdate=false с FK как VisaryRef ({ ID }).
        // См. doc_project/63-site-finishing-material-update-crud.md.
        var current = await GetCrudByIdAsync<ConstructionSiteFull>(
            VisaryMnemonics.Site, siteId, ct);
        if (current is null)
            throw new KeyNotFoundException($"ConstructionSite с ID={siteId} не найден в Visary");

        var body = new
        {
            ID = siteId,
            current.RowVersion,
            EstateClass = new { ID = estateClassId },
        };
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}/{siteId}?forceUpdate=false",
            body, $"{VisaryMnemonics.Site}/{siteId}", ct);

        _log.LogInformation("CrudClient.UpdateSiteEstateClassAsync: siteId={SiteId} success", siteId);
        return true;
    }

    public async Task<bool> UpdateSiteAddressAsync(
        int siteId, string address, CancellationToken ct)
    {
        // Тот же паттерн, что в UpdateSiteFinishingMaterialAsync / UpdateSiteEstateClassAsync:
        // GET /crud/{site}/{id} ради актуального RowVersion → PATCH с forceUpdate=false.
        // Address — простой строковый атрибут (не FK), поэтому в body передаётся как строка,
        // без обёртки VisaryRef. См. doc_project/63-site-finishing-material-update-crud.md.
        var current = await GetCrudByIdAsync<ConstructionSiteFull>(
            VisaryMnemonics.Site, siteId, ct);
        if (current is null)
            throw new KeyNotFoundException($"ConstructionSite с ID={siteId} не найден в Visary");

        var body = new
        {
            ID = siteId,
            current.RowVersion,
            Address = address,
        };
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}/{siteId}?forceUpdate=false",
            body, $"{VisaryMnemonics.Site}/{siteId}", ct);

        _log.LogInformation("CrudClient.UpdateSiteAddressAsync: siteId={SiteId} success", siteId);
        return true;
    }

    public Task<bool> PatchSiteAsync(int siteId, SitePatchRequest request, CancellationToken ct)
    {
        ApplyEntityId(request, siteId, r => r.ID, (r, v) => r.ID = v, nameof(siteId));
        _log.LogDebug("Visary → PATCH constructionsite id={Id}", siteId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}/{siteId}?forceUpdate=false",
            request, $"{VisaryMnemonics.Site}/{siteId}", siteId, ct,
            $"CrudClient.PatchSiteAsync: siteId={{Id}} success");
    }

    public async Task<ConstructionSiteRaw> CreateSiteAsync(SiteCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Site);
        var result = await PostCrudAsync<ConstructionSiteRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}", request, VisaryMnemonics.Site, ct);
        _log.LogInformation("CrudClient.CreateSiteAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionProject ─────────────────────────────────────────────────

    public Task<bool> PatchProjectAsync(int projectId, ProjectPatchRequest request, CancellationToken ct)
    {
        ApplyEntityId(request, projectId, r => r.ID, (r, v) => r.ID = v, nameof(projectId));
        _log.LogDebug("Visary → PATCH constructionproject id={Id}", projectId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Project}/{projectId}?forceUpdate=false",
            request, $"{VisaryMnemonics.Project}/{projectId}", projectId, ct,
            $"CrudClient.PatchProjectAsync: projectId={{Id}} success");
    }

    public async Task<ConstructionProjectRaw> CreateProjectAsync(ProjectCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Project);
        var result = await PostCrudAsync<ConstructionProjectRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Project}", request, VisaryMnemonics.Project, ct);
        _log.LogInformation("CrudClient.CreateProjectAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionSiteIndicatorValue (ТЭП) ────────────────────────────────

    public Task<bool> PatchIndicatorValueAsync(
        int valueId, IndicatorValuePatchRequest request, CancellationToken ct)
    {
        // Optimistic locking: caller обязан прислать актуальный RowVersion (получить через
        // GetIndicatorValueByIdAsync). forceUpdate=false — сервер сравнит RowVersion и
        // вернёт 409, если запись изменилась. См. doc_project/63 (тот же паттерн для Site).
        ApplyEntityId(request, valueId, r => r.ID, (r, v) => r.ID = v, nameof(valueId));
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.SiteIndicatorValue, valueId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.SiteIndicatorValue}/{valueId}?forceUpdate=false",
            request, $"{VisaryMnemonics.SiteIndicatorValue}/{valueId}", valueId, ct,
            $"CrudClient.PatchIndicatorValueAsync: valueId={{Id}} success");
    }

    // ─── CadastralArea (ЗУ) ──────────────────────────────────────────────────

    public async Task<CadastralAreaRaw> CreateCadastralAreaAsync(
        CadastralAreaCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.CadastralArea);
        var result = await PostCrudAsync<CadastralAreaRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.CadastralArea}", request, VisaryMnemonics.CadastralArea, ct);
        _log.LogInformation("CrudClient.CreateCadastralAreaAsync: created id={Id}", result.ID);
        return result;
    }

    public Task<bool> PatchCadastralAreaAsync(
        int areaId, CadastralAreaPatchRequest request, CancellationToken ct)
    {
        ApplyEntityId(request, areaId, r => r.ID, (r, v) => r.ID = v, nameof(areaId));
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.CadastralArea, areaId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.CadastralArea}/{areaId}?forceUpdate=false",
            request, $"{VisaryMnemonics.CadastralArea}/{areaId}", areaId, ct,
            $"CrudClient.PatchCadastralAreaAsync: areaId={{Id}} success");
    }

    public async Task<bool> LinkCadastralAreaToSiteAsync(int siteId, int areaId, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}/manytomany/{Linked}/link siteId={SiteId} areaId={AreaId}",
            VisaryMnemonics.Site, VisaryMnemonics.CadastralArea, siteId, areaId);
        using var req = NewRequest(HttpMethod.Post,
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}/manytomany/{VisaryMnemonics.CadastralArea}/link?associationId={siteId}&ids={areaId}");
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        _log.LogInformation("CrudClient.LinkCadastralAreaToSiteAsync: siteId={SiteId} areaId={AreaId} success", siteId, areaId);
        return true;
    }

    // ─── PercentBet ──────────────────────────────────────────────────────────

    public async Task<PercentBetRaw> CreatePercentBetAsync(PercentBetCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.PercentBet);
        var result = await PostCrudAsync<PercentBetRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.PercentBet}", request, VisaryMnemonics.PercentBet, ct);
        _log.LogInformation("CrudClient.CreatePercentBetAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── ConstructionSection ─────────────────────────────────────────────────

    public async Task<ConstructionSectionRaw> CreateSectionAsync(SectionCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Section);
        var result = await PostCrudAsync<ConstructionSectionRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Section}", request, VisaryMnemonics.Section, ct);
        _log.LogInformation("CrudClient.CreateSectionAsync: created id={Id}", result.ID);
        return result;
    }

    // ─── Room ────────────────────────────────────────────────────────────────

    public async Task<RoomRaw> CreateRoomAsync(RoomCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Room);
        var result = await PostCrudAsync<RoomRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Room}", request, VisaryMnemonics.Room, ct);
        _log.LogInformation("CrudClient.CreateRoomAsync: created id={Id}", result.ID);
        return result;
    }

    /// <summary>
    /// PATCH Room. Используется <c>forceUpdate=true</c>, чтобы импорт не требовал
    /// предварительной выборки RoomFull ради RowVersion (по аналогии с PATCH ТЭП).
    /// </summary>
    public Task<bool> PatchRoomAsync(int roomId, RoomPatchRequest request, CancellationToken ct)
    {
        // forceUpdate=true ⇒ ID/RowVersion в теле НЕ отправляются (Visary падает 500
        // «Can not add property RowVersion to JObject» при их наличии). Поля ID/RowVersion
        // в RoomPatchRequest nullable + WhenWritingNull → не попадают в JSON.
        request.ID = null;
        request.RowVersion = null;
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.Room, roomId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Room}/{roomId}?forceUpdate=true",
            request, $"{VisaryMnemonics.Room}/{roomId}", roomId, ct,
            $"CrudClient.PatchRoomAsync: roomId={{Id}} success");
    }

    // ─── ShareAgreement (ДДУ) ────────────────────────────────────────────────

    public async Task<ShareAgreementRaw> CreateShareAgreementAsync(
        ShareAgreementCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.ShareAgreement);
        var result = await PostCrudAsync<ShareAgreementRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.ShareAgreement}", request, VisaryMnemonics.ShareAgreement, ct);
        _log.LogInformation("CrudClient.CreateShareAgreementAsync: created id={Id}", result.ID);
        return result;
    }

    /// <summary>
    /// PATCH ShareAgreement (ДДУ). Используется <c>forceUpdate=true</c>, чтобы избежать
    /// дополнительной выборки ShareAgreementFull ради RowVersion при импорте.
    /// </summary>
    public Task<bool> PatchShareAgreementAsync(
        int shareAgreementId, ShareAgreementPatchRequest request, CancellationToken ct)
    {
        // См. комментарий в PatchRoomAsync: forceUpdate=true ⇒ убираем ID/RowVersion из тела.
        request.ID = null;
        request.RowVersion = null;
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.ShareAgreement, shareAgreementId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.ShareAgreement}/{shareAgreementId}?forceUpdate=true",
            request, $"{VisaryMnemonics.ShareAgreement}/{shareAgreementId}", shareAgreementId, ct,
            $"CrudClient.PatchShareAgreementAsync: shareAgreementId={{Id}} success");
    }

    // ─── WBS (ИСР — главы и подстатьи бюджета) ───────────────────────────────

    public async Task<WbsRaw> CreateWbsAsync(WbsCreateRequest request, CancellationToken ct)
    {
        // POST /api/visary/crud/wbs — Code (КБК) присваивается сервером автоматически
        // на основании ParentID + порядка создания. Глава = ParentID null, подстатья
        // = ParentID указанной главы. ConstructionSite опционален (главу проекта можно
        // создать без привязки к ОКСу; подстатью — обычно с ОКСом).
        _log.LogDebug("Visary → POST {Mnemonic}", VisaryMnemonics.Wbs);
        var result = await PostCrudAsync<WbsRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Wbs}", request, VisaryMnemonics.Wbs, ct);
        _log.LogInformation("CrudClient.CreateWbsAsync: created id={Id} code={Code} parentId={ParentId}",
            result.ID, result.Code ?? "(server-assigned)", request.ParentID);
        return result;
    }

    /// <summary>
    /// PATCH WBS-записи (главы или подстатьи бюджета). Используется <c>forceUpdate=true</c>
    /// — тот же приём, что для Room/ShareAgreement: ID/RowVersion из тела убираются (Visary
    /// падает 500 «Can not add property RowVersion to JObject» при их наличии). Импорт
    /// бюджета вызывает этот метод для обновления <c>DeclaredSum</c>/<c>ConfirmedSum</c>
    /// существующей подстатьи (идемпотентность повторного импорта — без дублирования).
    /// </summary>
    public Task<bool> PatchWbsAsync(int wbsId, WbsPatchRequest request, CancellationToken ct)
    {
        // forceUpdate=true ⇒ ID/RowVersion в теле НЕ отправляются. Поля nullable +
        // WhenWritingNull → не попадают в JSON. См. PatchRoomAsync / PatchShareAgreementAsync.
        request.ID = null;
        request.RowVersion = null;
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.Wbs, wbsId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Wbs}/{wbsId}?forceUpdate=true",
            request, $"{VisaryMnemonics.Wbs}/{wbsId}", wbsId, ct,
            $"CrudClient.PatchWbsAsync: wbsId={{Id}} success");
    }

    // ─── CostItem (ГФ — график финансирования подстатьи ИСР) ────────────────

    public async Task<CostItemRaw> CreateCostItemAsync(
        CostItemCreateRequest request, CancellationToken ct)
    {
        // POST /api/visary/crud/costitem — тело: { WBSID, WBS:{ID}, PlanSum, PlanPeriod, Status }.
        // Сервер возвращает CostItemRaw c назначенным ID. Status=70 (Plan) — единственный
        // используемый импортом статус, см. CostItemStatus.Plan.
        _log.LogDebug("Visary → POST {Mnemonic} wbsId={WbsId}", VisaryMnemonics.CostItem, request.WBSID);
        var result = await PostCrudAsync<CostItemRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.CostItem}", request, VisaryMnemonics.CostItem, ct);
        _log.LogInformation(
            "CrudClient.CreateCostItemAsync: created id={Id} wbsId={WbsId} planSum={Sum} period={Start:O}..{End:O}",
            result.ID, request.WBSID, request.PlanSum,
            request.PlanPeriod?.Start, request.PlanPeriod?.End);
        return result;
    }

    public Task<bool> PatchCostItemAsync(
        int costItemId, CostItemPatchRequest request, CancellationToken ct)
    {
        // forceUpdate=true ⇒ ID/RowVersion в теле НЕ отправляются (Visary падает 500
        // «Can not add property RowVersion to JObject» при их наличии). Те же поля
        // nullable + WhenWritingNull в DTO. См. PatchWbsAsync/PatchRoomAsync.
        request.ID = null;
        request.RowVersion = null;
        _log.LogDebug("Visary → PATCH {Mnemonic} id={Id}", VisaryMnemonics.CostItem, costItemId);
        return PatchAndReportAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.CostItem}/{costItemId}?forceUpdate=true",
            request, $"{VisaryMnemonics.CostItem}/{costItemId}", costItemId, ct,
            $"CrudClient.PatchCostItemAsync: costItemId={{Id}} success");
    }

    // ─── FmModel (Финмодель) ────────────────────────────────────────────────

    public async Task<FmModelRaw> CreateFmModelAsync(
        FmModelCreateRequest request, CancellationToken ct)
    {
        // POST /api/visary/crud/fmmodel — тело по примеру заказчика:
        //   { Title, ProjectCode, ABProjectID, ABConstructionSiteID, PeriodStart, PeriodEnd }
        // Сервер возвращает FmModelRaw с ID. Идемпотентности нет — повторный POST
        // даст дубликат; caller обязан pre-check'ить через FindFmModelsAsync.
        _log.LogDebug(
            "Visary → POST {Mnemonic} projectId={ProjectId} siteId={SiteId} period={Start}..{End}",
            VisaryMnemonics.FmModel, request.ABProjectID, request.ABConstructionSiteID,
            request.PeriodStart, request.PeriodEnd);
        var result = await PostCrudAsync<FmModelRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.FmModel}",
            request, VisaryMnemonics.FmModel, ct);
        _log.LogInformation(
            "CrudClient.CreateFmModelAsync: created id={Id} title='{Title}' projectId={ProjectId} siteId={SiteId} period={Start}..{End}",
            result.ID, request.Title, request.ABProjectID, request.ABConstructionSiteID,
            request.PeriodStart, request.PeriodEnd);
        return result;
    }

    // ─── FmModelVersion / InputData ──────────────────────────────────────────

    public async Task<FmModelVersionRaw> CreateFmModelVersionAsync(
        FmModelVersionCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic} fmModelId={FmModelId} title='{Title}'",
            VisaryMnemonics.FmModelVersion, request.FMModelID, request.Title);
        var result = await PostCrudAsync<FmModelVersionRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.FmModelVersion}",
            request, VisaryMnemonics.FmModelVersion, ct);
        _log.LogInformation(
            "CrudClient.CreateFmModelVersionAsync: created id={Id} fmModelId={FmModelId} title='{Title}'",
            result.ID, request.FMModelID, request.Title);
        return result;
    }

    public async Task<InputDataRaw> CreateInputDataAsync(
        InputDataCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic} versionId={VersionId} period={Period} codeId={CodeId} summ={Summ}",
            VisaryMnemonics.InputData, request.FMModelVersionID,
            request.FMPeriod, request.Code?.ID, request.Summ);
        var result = await PostCrudAsync<InputDataRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.InputData}",
            request, VisaryMnemonics.InputData, ct);
        _log.LogInformation(
            "CrudClient.CreateInputDataAsync: created id={Id} versionId={VersionId} period={Period} codeId={CodeId} summ={Summ}",
            result.ID, request.FMModelVersionID, request.FMPeriod,
            request.Code?.ID, request.Summ);
        return result;
    }

    public async Task<bool> LinkInputDataToVersionAsync(
        int versionId, int inputDataId, CancellationToken ct)
    {
        // По HAR заказчика — POST listview/inputdata/onetomany/FMModelVersion с
        // associationId={versionId} и body, описывающим конкретную inputdata-строку
        // через Filter ["ID","=",{inputDataId}]. Тот же набор Columns, что отдаёт UI.
        var body = new
        {
            Mnemonic = VisaryMnemonics.InputData,
            Filter = System.Text.Json.JsonSerializer.Serialize(
                new object[] { "ID", "=", inputDataId }),
            PageSkip = 0,
            PageSize = 1,
            Columns = InputDataLinkColumns,
        };
        var url = $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.InputData}/onetomany/FMModelVersion?associationId={versionId}";
        _log.LogDebug(
            "Visary → POST {Mnemonic}/onetomany/FMModelVersion versionId={VersionId} inputDataId={Id}",
            VisaryMnemonics.InputData, versionId, inputDataId);
        var bodyJson = System.Text.Json.JsonSerializer.Serialize(body, JsonOptions);
        using var req = NewRequest(HttpMethod.Post, url);
        req.Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        _log.LogInformation(
            "CrudClient.LinkInputDataToVersionAsync: versionId={VersionId} inputDataId={Id} success",
            versionId, inputDataId);
        return true;
    }

    // Колонки для link-вызова inputdata (HAR заказчика). Сервер требует ровно этот
    // набор, иначе 400. Включён в CrudClient (а не ListViewClient), поскольку
    // используется только парным шагом «POST /crud/inputdata → POST link».
    private static readonly string[] InputDataLinkColumns =
        ["ID", "Title", "FMModelVersion", "FMPeriod", "Code", "CreditLineCode",
         "ConstructionSiteCode", "CodeGroup", "Summ", "Amount", "Cost", "Percent",
         "Replicate", "Group"];

    // ─── ProjectAudit / DataForFm / DataSetForFm (Заключение + рассрочки) ────

    public async Task<ProjectAuditRaw> CreateProjectAuditAsync(
        ProjectAuditCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic} projectId={ProjectId} siteId={SiteId} stage={Stage} status={Status}",
            VisaryMnemonics.ProjectAudit, request.ProjectID,
            request.ConstructionSite?.ID, request.Stage, request.Status);
        var result = await PostCrudAsync<ProjectAuditRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.ProjectAudit}",
            request, VisaryMnemonics.ProjectAudit, ct);
        _log.LogInformation(
            "CrudClient.CreateProjectAuditAsync: created id={Id} projectId={ProjectId} siteId={SiteId} stage={Stage}",
            result.ID, request.ProjectID, request.ConstructionSite?.ID, request.Stage);
        return result;
    }

    public async Task<DataForFmRaw> CreateDataForFmAsync(
        DataForFmCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic} dataSetId={DataSetId} roomKindId={RoomKindId}",
            VisaryMnemonics.DataForFm, request.DataSetForFMID, request.RoomKind.ID);
        var result = await PostCrudAsync<DataForFmRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.DataForFm}",
            request, VisaryMnemonics.DataForFm, ct);
        _log.LogInformation(
            "CrudClient.CreateDataForFmAsync: created id={Id} dataSetId={DataSetId} roomKindId={RoomKindId} title='{Title}'",
            result.ID, request.DataSetForFMID, request.RoomKind.ID, request.Title);
        return result;
    }

    public Task<DataSetForFmRaw> GetDataSetForFmByIdAsync(int id, CancellationToken ct)
        => GetCrudByIdAsync<DataSetForFmRaw>(VisaryMnemonics.DataSetForFm, id, ct);

    public async Task<DealPercentBetRaw> CreateDealPercentBetAsync(
        DealPercentBetCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic} dealId={DealId} percentKind={Kind} betTypeId={BetTypeId} rate={Rate}",
            VisaryMnemonics.DealPercentBet, request.DealID,
            request.PercentKind, request.PercentBetType.ID, request.Rate);
        var result = await PostCrudAsync<DealPercentBetRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.DealPercentBet}",
            request, VisaryMnemonics.DealPercentBet, ct);
        _log.LogInformation(
            "CrudClient.CreateDealPercentBetAsync: created id={Id} dealId={DealId} percentKind={Kind} rate={Rate}",
            result.ID, request.DealID, request.PercentKind, request.Rate);
        return result;
    }

    public async Task<DealMonthlyDataRaw> CreateDealMonthlyDataAsync(
        DealMonthlyDataCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic} dealId={DealId} year={Year} month={Month}",
            VisaryMnemonics.DealMonthlyData, request.Deal.ID, request.Year, request.Month);
        var result = await PostCrudAsync<DealMonthlyDataRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.DealMonthlyData}",
            request, VisaryMnemonics.DealMonthlyData, ct);
        _log.LogInformation(
            "CrudClient.CreateDealMonthlyDataAsync: created id={Id} dealId={DealId} year={Year} month={Month}",
            result.ID, request.Deal.ID, request.Year, request.Month);
        return result;
    }

    public async Task<bool> PatchDataSetForFmInstallmentsAsync(
        DataSetForFmInstallmentsPatchRequest request, CancellationToken ct)
    {
        // Build payload вручную, поскольку имена 3 полей (OwnShare/PostpShare/RoomKinds)
        // зависят от схемы рассрочки (равномерная DDUSteady*, единовременная DDUOnetime*,
        // ДКП DKP*) — типизированный класс с фиксированными именами не подходит.
        // RoomKinds — массив объектов { "Object": { ID, Title } } по HAR.
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [request.OwnSharePropertyName] = request.OwnShare,
            [request.PostpSharePropertyName] = request.PostpShare,
            [request.RoomKindsPropertyName] = request.RoomKinds
                .Select(rk => new { Object = new { rk.Title, rk.ID } })
                .ToArray(),
            ["ID"] = request.ID,
            ["RowVersion"] = request.RowVersion,
        };
        var url = $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.DataSetForFm}/{request.ID}?forceUpdate=false";
        await PatchCrudAsync(url, payload,
            $"{VisaryMnemonics.DataSetForFm}/{request.ID}", ct);
        _log.LogInformation(
            "CrudClient.PatchDataSetForFmInstallmentsAsync: id={Id} prefix='{Prefix}' kinds={Count} success",
            request.ID, request.OwnSharePropertyName, request.RoomKinds.Count);
        return true;
    }

    // ─── TypedImportWbs (TypedJournal-импорт бюджета) ────────────────────────

    public async Task<TypedImportWbsRaw> CreateTypedImportWbsAsync(
        TypedImportWbsCreateRequest request, CancellationToken ct)
    {
        // POST /api/visary/crud/typedimportwbs — тело по HAR (Context/har импорт бюджета.txt).
        // Visary при создании записи триггерит фоновую обработку импорта бюджета.
        // Ответ — короткий (~11 байт): JSON с ID созданной записи (иногда — голый int).
        _log.LogDebug("Visary → POST {Mnemonic} (projectId={ProjectId}, siteId={SiteId}, importType={ImportType})",
            VisaryMnemonics.TypedImportWbs, request.ProjectID, request.ConstructionSiteID, request.ImportType);
        var result = await PostCrudAsync<TypedImportWbsRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.TypedImportWbs}",
            request, VisaryMnemonics.TypedImportWbs, ct);
        _log.LogInformation(
            "CrudClient.CreateTypedImportWbsAsync: created id={Id} (siteId={SiteId})",
            result.ID, request.ConstructionSiteID);
        return result;
    }

    public Task<TypedImportWbsRaw> GetTypedImportWbsByIdAsync(int id, CancellationToken ct)
        => GetCrudByIdAsync<TypedImportWbsRaw>(VisaryMnemonics.TypedImportWbs, id, ct);

    // ─── Organization ↔ Site link ────────────────────────────────────────────

    public async Task<bool> LinkOrganizationToSiteAsync(int siteId, int organizationId, CancellationToken ct)
    {
        _log.LogDebug("Visary → POST {Mnemonic}/manytomany/{Linked}/link siteId={SiteId} orgId={OrgId}",
            VisaryMnemonics.Site, VisaryMnemonics.Organization, siteId, organizationId);
        using var req = NewRequest(HttpMethod.Post,
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}/manytomany/{VisaryMnemonics.Organization}/link?associationId={siteId}&ids={organizationId}");
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        _log.LogInformation("CrudClient.LinkOrganizationToSiteAsync: siteId={SiteId} orgId={OrgId} success",
            siteId, organizationId);
        return true;
    }

    public async Task<OrganizationRaw> CreateOrganizationAsync(
        OrganizationCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic} title='{Title}' clientId='{ClientId}'",
            VisaryMnemonics.Organization, request.Title, request.ClientID);
        var result = await PostCrudAsync<OrganizationRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Organization}",
            request, VisaryMnemonics.Organization, ct);
        _log.LogInformation(
            "CrudClient.CreateOrganizationAsync: created id={Id} title='{Title}' clientId='{ClientId}'",
            result.ID, result.Title, result.ClientID);
        return result;
    }

    public async Task<DealRaw> CreateDealAsync(
        DealCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic} projectId={ProjectId} docNumber='{DocNumber}' lmId='{LmId}'",
            VisaryMnemonics.Deal, request.ConstructionProjectID, request.DocNumber, request.LmID);
        var result = await PostCrudAsync<DealRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Deal}",
            request, VisaryMnemonics.Deal, ct);
        _log.LogInformation(
            "CrudClient.CreateDealAsync: created id={Id} projectId={ProjectId} docNumber='{DocNumber}' lmId='{LmId}'",
            result.ID, request.ConstructionProjectID, result.DocNumber, result.LmID);
        return result;
    }

    public async Task<bool> UpdateOrganizationGroupAsync(
        int organizationId, int groupId, string groupTitle, CancellationToken ct)
    {
        // Тот же паттерн, что в UpdateSiteFinishingMaterialAsync / UpdateSiteAddressAsync:
        // GET /crud/organization/{id} ради актуального RowVersion → PATCH с
        // forceUpdate=false + Group как VisaryRef ({ ID, Title, Hidden:false }).
        // Title в теле — копия имени группы, как в Visary UI (с ним PATCH идёт чище в
        // логах сервера: видно, ЧТО за группу мы хотим привязать; ID — авторитативен).
        // См. doc_project/100-finmodel-companygroup-link.md.
        var current = await GetCrudByIdAsync<OrganizationFull>(
            VisaryMnemonics.Organization, organizationId, ct);
        if (current is null)
            throw new KeyNotFoundException(
                $"Organization с ID={organizationId} не найдена в Visary");

        var body = new
        {
            ID = organizationId,
            current.RowVersion,
            Group = new { ID = groupId, Title = groupTitle, Hidden = false },
        };
        await PatchCrudAsync(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Organization}/{organizationId}?forceUpdate=false",
            body, $"{VisaryMnemonics.Organization}/{organizationId}", ct);

        _log.LogInformation(
            "CrudClient.UpdateOrganizationGroupAsync: orgId={OrgId} groupId={GroupId} title='{Title}' success",
            organizationId, groupId, groupTitle);
        return true;
    }

    // ─── ProjectManagement (создание + manytomany link к Site) ──────────────

    public async Task<ProjectManagementRaw> CreateProjectManagementAsync(
        ProjectManagementCreateRequest request, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic} projectId={ProjectId} orgId={OrgId} roleId={RoleId}",
            VisaryMnemonics.ProjectManagement,
            request.Project.ID, request.Organization.ID, request.Role.ID);
        var result = await PostCrudAsync<ProjectManagementRaw>(
            $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.ProjectManagement}",
            request, VisaryMnemonics.ProjectManagement, ct);
        _log.LogInformation("CrudClient.CreateProjectManagementAsync: created id={Id}", result.ID);
        return result;
    }

    public async Task<bool> LinkProjectManagementToSiteAsync(
        int siteId, int projectManagementId, CancellationToken ct)
    {
        _log.LogDebug(
            "Visary → POST {Mnemonic}/manytomany/{Linked}/link siteId={SiteId} pmId={PmId}",
            VisaryMnemonics.Site, VisaryMnemonics.ProjectManagement, siteId, projectManagementId);
        using var req = NewRequest(HttpMethod.Post,
            $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.Site}/manytomany/{VisaryMnemonics.ProjectManagement}/link?associationId={siteId}&ids={projectManagementId}");
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        _log.LogInformation(
            "CrudClient.LinkProjectManagementToSiteAsync: siteId={SiteId} pmId={PmId} success",
            siteId, projectManagementId);
        return true;
    }

    // ─── GET by ID (полные DTO через /crud/{mnemonic}/{id}) ──────────────────

    public Task<TEntity> GetByIdAsync<TEntity>(string mnemonic, int id, CancellationToken ct)
        => GetCrudByIdAsync<TEntity>(mnemonic, id, ct);

    public Task<ConstructionProjectFull>            GetProjectByIdFullAsync(int id, CancellationToken ct)             => GetCrudByIdAsync<ConstructionProjectFull>(VisaryMnemonics.Project, id, ct);
    public Task<ConstructionSiteFull>               GetSiteByIdFullAsync(int id, CancellationToken ct)                => GetCrudByIdAsync<ConstructionSiteFull>(VisaryMnemonics.Site, id, ct);
    public Task<ConstructionSectionFull>            GetSectionByIdAsync(int id, CancellationToken ct)                 => GetCrudByIdAsync<ConstructionSectionFull>(VisaryMnemonics.Section, id, ct);
    public Task<ConstructionSiteIndicatorFull>      GetIndicatorByIdAsync(int id, CancellationToken ct)               => GetCrudByIdAsync<ConstructionSiteIndicatorFull>(VisaryMnemonics.SiteIndicator, id, ct);
    public Task<ConstructionSiteIndicatorValueFull> GetIndicatorValueByIdAsync(int id, CancellationToken ct)          => GetCrudByIdAsync<ConstructionSiteIndicatorValueFull>(VisaryMnemonics.SiteIndicatorValue, id, ct);
    public Task<RoomFull>                           GetRoomByIdAsync(int id, CancellationToken ct)                    => GetCrudByIdAsync<RoomFull>(VisaryMnemonics.Room, id, ct);
    public Task<CadastralAreaFull>                  GetCadastralAreaByIdAsync(int id, CancellationToken ct)           => GetCrudByIdAsync<CadastralAreaFull>(VisaryMnemonics.CadastralArea, id, ct);
    public Task<PercentBetFull>                     GetPercentBetByIdAsync(int id, CancellationToken ct)              => GetCrudByIdAsync<PercentBetFull>(VisaryMnemonics.PercentBet, id, ct);
    public Task<ShareAgreementFull>                 GetShareAgreementByIdAsync(int id, CancellationToken ct)          => GetCrudByIdAsync<ShareAgreementFull>(VisaryMnemonics.ShareAgreement, id, ct);
    public Task<DealFull>                           GetDealByIdAsync(int id, CancellationToken ct)                    => GetCrudByIdAsync<DealFull>(VisaryMnemonics.Deal, id, ct);
    public Task<OrganizationFull>                   GetOrganizationByIdAsync(int id, CancellationToken ct)            => GetCrudByIdAsync<OrganizationFull>(VisaryMnemonics.Organization, id, ct);

    public Task<TownRaw>                GetTownByIdAsync(int id, CancellationToken ct)                => GetCrudByIdAsync<TownRaw>(VisaryMnemonics.Town, id, ct);
    public Task<RegionRaw>              GetRegionByIdAsync(int id, CancellationToken ct)              => GetCrudByIdAsync<RegionRaw>(VisaryMnemonics.Region, id, ct);
    public Task<ProjectTypeRaw>         GetProjectTypeByIdAsync(int id, CancellationToken ct)         => GetCrudByIdAsync<ProjectTypeRaw>(VisaryMnemonics.ProjectType, id, ct);
    public Task<InflationCalcMethodRaw> GetInflationCalcMethodByIdAsync(int id, CancellationToken ct) => GetCrudByIdAsync<InflationCalcMethodRaw>(VisaryMnemonics.InflationCalcMethod, id, ct);
    public Task<EstateClassRaw>         GetEstateClassByIdAsync(int id, CancellationToken ct)         => GetCrudByIdAsync<EstateClassRaw>(VisaryMnemonics.EstateClass, id, ct);
    public Task<BuildingMaterialRaw>    GetBuildingMaterialByIdAsync(int id, CancellationToken ct)    => GetCrudByIdAsync<BuildingMaterialRaw>(VisaryMnemonics.BuildingMaterial, id, ct);
    public Task<FinishingMaterialRaw>   GetFinishingMaterialByIdAsync(int id, CancellationToken ct)   => GetCrudByIdAsync<FinishingMaterialRaw>(VisaryMnemonics.FinishingMaterial, id, ct);
    public Task<RoomKindRaw>            GetRoomKindByIdAsync(int id, CancellationToken ct)            => GetCrudByIdAsync<RoomKindRaw>(VisaryMnemonics.RoomKind, id, ct);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // Caller передал ID в URL и в DTO. Если ID в DTO ненулевой и не совпадает — это
    // ошибка вызывающего, лучше упасть громко, чем тихо переписать поле.
    private static void ApplyEntityId<TRequest>(
        TRequest request, int routeId,
        Func<TRequest, int> getter, Action<TRequest, int> setter,
        string routeParamName)
    {
        var bodyId = getter(request);
        if (bodyId != 0 && bodyId != routeId)
            throw new ArgumentException(
                $"request.ID={bodyId} не совпадает с {routeParamName}={routeId}", nameof(request));
        setter(request, routeId);
    }

    private async Task<bool> PatchAndReportAsync(
        string url, object body, string logLabel, int id,
        CancellationToken ct, string successTemplate)
    {
        await PatchCrudAsync(url, body, logLabel, ct);
        _log.LogInformation(successTemplate, id);
        return true;
    }

    private async Task<TEntity> PostCrudAsync<TEntity>(
        string url, object body, string logLabel, CancellationToken ct)
    {
        var bodyJson = System.Text.Json.JsonSerializer.Serialize(body, JsonOptions);
        _log.LogInformation("Visary → POST {Url} body={Body}", url, bodyJson);

        using var req = NewRequest(HttpMethod.Post, url);
        req.Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleErrorAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<TEntity>(JsonOptions, ct);
        _log.LogInformation("Visary ← 200 POST {Label}", logLabel);
        return result!;
    }

    private async Task PatchCrudAsync(string url, object body, string logLabel, CancellationToken ct)
    {
        var bodyJson = System.Text.Json.JsonSerializer.Serialize(body, JsonOptions);
        _log.LogInformation("Visary → PATCH {Url} body={Body}", url, bodyJson);

        using var req = NewRequest(HttpMethod.Patch, url);
        req.Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(req, ct);
        await HandleAuthErrorAsync(response, ct);
        await HandleConflictAsync(response, ct, logLabel);
        await HandleErrorAsync(response, ct);
        _log.LogInformation("Visary ← 200 PATCH {Label}", logLabel);
    }

    // GET по ID через /crud/{mnemonic}/{id} живёт в VisaryHttpBase.GetCrudByIdAsync<T>
    // и используется во всех Get*ByIdAsync-методах этого клиента, включая
    // UpdateSiteFinishingMaterialAsync (для чтения актуального RowVersion).
}
