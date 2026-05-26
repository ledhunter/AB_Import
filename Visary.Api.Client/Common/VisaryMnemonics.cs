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
