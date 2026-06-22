namespace Visary.Api.Common;

public static class VisaryMnemonics
{
    public const string Project = "constructionproject";
    public const string Site = "constructionsite";
    public const string SiteIndicator = "constructionsiteindicator";
    public const string SiteIndicatorValue = "constructionsiteindicatorvalue";
    public const string Section = "constructionsection";
    public const string Room = "room";
    public const string Deal = "deal";
    public const string Organization = "organization";
    public const string PercentBet = "percentbet";
    public const string CadastralArea = "cadastralarea";
    public const string ShareAgreement = "shareagreement";
    public const string Wbs = "wbs";
    public const string TypedImportWbs = "typedimportwbs";
    public const string ProjectManagement = "projectmanagement";
    public const string CostItem = "costitem";
    public const string CompanyGroup = "companygroup";
    public const string FmModel = "fmmodel";
    public const string FmModelVersion = "fmmodelversion";
    public const string InputData = "inputdata";
    /// <summary>
    /// Справочник «Код фин. модели» (<c>fmcode</c>) — содержит ID/Code/Title и
    /// классификацию (Group=Доходы/Расходы, Sign=±1, Method, Priority …). Используется
    /// как <see cref="Dto.InputDataCreateRequest.Code"/> в <c>inputdata</c>.
    /// Заменил ранее опробованный <c>inputdatacode</c>, которого нет на стенде (404).
    /// См. doc_project/112-finmodel-version-and-inputdata.md v1.1.
    /// </summary>
    public const string FmCode = "fmcode";

    // ─── Заключение + рассрочки (БП7) ───────────────────────────────────
    // Доработка doc 139: после Budget+ГФ Финмодель создаёт «Заключение»
    // (тип «Итоговое заключение КА БП7»), на сервере автоматически
    // подтягивается DataSetForFm (одна запись на Site+Project), затем
    // мапер создаёт по одной DataForFm на каждый «1 - Да» вид помещения
    // и PATCH-ит DataSetForFm полями <Prefix>OwnShare/PostpShare/RoomKinds.
    /// <summary>
    /// «Заключение» (сущность <c>projectaudit</c>). Создаётся с
    /// <c>Stage=110</c> = «Итоговое заключение КА БП7», <c>Status=10</c>.
    /// См. doc_project/139-finmodel-installments-and-conclusion.md.
    /// </summary>
    public const string ProjectAudit = "projectaudit";
    /// <summary>
    /// «Набор данных для ФМ» (<c>datasetforfm</c>) — родительская запись для
    /// <see cref="DataForFm"/>. Создаётся автоматически сервером при POST
    /// <c>projectaudit</c>; импортер только находит её по (Site, Project) и
    /// PATCH-ит поля рассрочек (DDU(O|S|P)…OwnShare/PostpShare/RoomKinds).
    /// </summary>
    public const string DataSetForFm = "datasetforfm";
    /// <summary>
    /// «Данные для ФМ» (<c>dataforfm</c>) — строка под <see cref="DataSetForFm"/>,
    /// одна на (DataSetForFmID × RoomKind). Импортер создаёт по одной на каждый
    /// вид помещения, для которого в Excel «Этап 1» стоит «1 - Да».
    /// </summary>
    public const string DataForFm = "dataforfm";

    /// <summary>
    /// Справочник «Тип процентной ставки» (<c>percentbettype</c>). У каждой
    /// строки уникальный <c>Code</c> вида «LM10»/«LM20»/«LM30»/«LM40»; импорт
    /// Финмодели резолвит Code → (ID, Title) перед POST <see cref="DealPercentBet"/>.
    /// См. doc_project/139-finmodel-installments-and-conclusion.md (раздел про
    /// ставки в блоке «Финансирование»).
    /// </summary>
    public const string PercentBetType = "percentbettype";

    /// <summary>
    /// «Процентная ставка по сделке» (<c>dealpercentbet</c>) — связка
    /// (Deal × PercentBetType × PercentKind) с числовым значением <c>Rate</c>.
    /// Импорт Финмодели создаёт по одной записи на каждую включённую ставку
    /// «Этапа 1» (LM10/LM20/LM30/LM40), если в файле она не помечена «0 - Нет».
    /// </summary>
    public const string DealPercentBet = "dealpercentbet";

    /// <summary>
    /// «Помесячные данные по сделке» (<c>dealmonthlydata</c>) — связка
    /// (Deal × Year × Month) с 5 числовыми полями: PrincipalDebtAmount,
    /// SimpleInterestAmount, CapitalizedInterestAmount, PrincipalRepaymentAmount,
    /// InterestRepaymentAmount. Импорт Финмодели создаёт ОДНУ запись на текущий
    /// месяц по разделу «Инвестиционный кредит: Этап 1» листа Outputs.
    /// См. doc 142.
    /// </summary>
    public const string DealMonthlyData = "dealmonthlydata";

    // ─── Справочники ────────────────────────────────────────────────────
    public const string Town                = "town";
    public const string Region              = "region";
    public const string ProjectType         = "projecttype";
    public const string InflationCalcMethod = "inflationcalcmethod";
    public const string EstateClass         = "estateclass";
    public const string BuildingMaterial    = "buildingmaterial";
    public const string FinishingMaterial   = "finishingmaterial";
    public const string RoomKind            = "roomkind";
}
