using System.Text.Json;

namespace Visary.Api.Dto;

public sealed class ConstructionSiteIndicatorRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public VisaryRef? ConstructionSite { get; set; }
    public double? GoalValue { get; set; }
    public string? GoalDate { get; set; }
    public VisaryRef? Indicator { get; set; }
    public int? Group { get; set; }
    public VisaryRef? Project { get; set; }
    public string? Comment { get; set; }
    public int? SortOrder { get; set; }
    public double? MainValue { get; set; }
    public string? MainTextValue { get; set; }
    public string? LastUpdate { get; set; }
    public double? LastPlanValue { get; set; }
    public double? LastForecastValue { get; set; }
    public double? LastValue { get; set; }
    // Visary возвращает MainSource то как строку (источник=ручной ввод), то как число
    // (FK на источник из справочника). Принимаем сырой JsonElement и оставляем разбор caller-у.
    public JsonElement? MainSource { get; set; }
    public DateTime? Version { get; set; }
}

public sealed class ConstructionSiteIndicatorValueRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public VisaryRef? ConstructionSiteIndicator { get; set; }
    public string? Date { get; set; }
    public double? Value { get; set; }
    public double? PlanValue { get; set; }
    public double? ForecastValue { get; set; }
    public int? Stage { get; set; }
    public bool IsUnlimited { get; set; }
    public int? IndicatorGroup { get; set; }
    public string? TextValue { get; set; }
    public VisaryRef? Site { get; set; }
    public int SortOrder { get; set; }
    public DateTime? Version { get; set; }
}

public sealed class DealRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? LmID { get; set; }
    public string? DocNumber { get; set; }
    public VisaryRef? ConstructionProject { get; set; }
    public VisaryRef? Organization { get; set; }
    public string? GroupName { get; set; }
    public double? CreditSum { get; set; }
    public string? DealStartDate { get; set; }
    public string? DealEndDate { get; set; }
}

public sealed class OrganizationRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    // Visary возвращает Status как числовой код (см. OrganizationFull). Принимаем сырой
    // JsonElement, чтобы не падать, если поле когда-то начнёт приходить строкой.
    public JsonElement? Status { get; set; }
    public string? INN { get; set; }
    public string? ClientID { get; set; }
    public string? Address { get; set; }
    public string? Code { get; set; }
    public string? OGRN { get; set; }
    public string? KPP { get; set; }
    public bool? Hidden { get; set; }
    // Группа компаний (companygroup). В listview Visary возвращает поле "Group" как
    // {ID, Title, Hidden} при привязке через PATCH /crud/organization. В колонках уже
    // запрашиваем "Group" (OrganizationColumns), но в первой версии DTO поле было
    // опущено — добавили под FinModel CompanyGroup-flow (doc 100).
    public VisaryRef? Group { get; set; }
}

/// <summary>
/// Минимальный DTO «Группа компаний» (<c>companygroup</c>) — справочник материнских
/// холдингов, к которым привязываются дочерние Organization (поле <c>Group</c>).
/// Используется в FinModel-импорте: по значению «Группа компаний» из E14 листа
/// Inputs ищем запись в Visary, при единственном попадании — PATCH-им организацию-
/// застройщик (см. doc_project/100-finmodel-companygroup-link.md).
/// </summary>
public sealed class CompanyGroupRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? Code { get; set; }
    public bool? Hidden { get; set; }
}

public sealed class RoomRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Site { get; set; }
    public VisaryRef? Section { get; set; }
    public string? Number { get; set; }
    public string? Floor { get; set; }
    public VisaryRef? Kind { get; set; }
    public int? RoomsNumber { get; set; }
    public bool? IsStudio { get; set; }
    public double? TotalArea { get; set; }
    public double? LivingArea { get; set; }
    public string? Description { get; set; }
    public double? Cost { get; set; }
    public bool? IsSeparateEntrance { get; set; }
    public bool? IsShowcaseWindows { get; set; }
    public double? TotalAreaWithoutSummerRoom { get; set; }
    public double? SummerRoomArea { get; set; }
    public double? CostForOne { get; set; }
    public string? ExplicationNumber { get; set; }
    public string? BuildingSection { get; set; }
    public string? UniqueNumber { get; set; }
    public double? ProjectArea { get; set; }
    public VisaryRef? RoomPurpose { get; set; }
    public VisaryRef? ParkingPlaceType { get; set; }
    public string? CadastralNumber { get; set; }
    public bool? IsWithdrawn { get; set; }
    // В listview RoomCategory приходит скаляром (int), а в crud-ответе — VisaryRef.
    // Принимаем оба варианта через JsonElement; при необходимости разобрать на стороне caller-а.
    public JsonElement? RoomCategory { get; set; }
    // В listview Active*/Candidate* поля приходят разной формы — иногда скаляром,
    // иногда VisaryRef. Принимаем как JsonElement; маппер использует только Number/Title и т.п.,
    // эти поля для бизнес-логики не нужны.
    public JsonElement? ActiveShareAgreement { get; set; }
    public JsonElement? CandidateShareAgreement { get; set; }
    public JsonElement? ActiveEscrowAccount { get; set; }
    public JsonElement? CandidateEscrowAccount { get; set; }
    public double? CalculatedCostPerM { get; set; }
    public double? MarketCostPerM { get; set; }
    public double? ZalogCostPerM { get; set; }
}

public sealed class CadastralAreaRaw
{
    public int ID { get; set; }
    public string? CadastralNum { get; set; }
    public double? Area { get; set; }
    public string? UnstructuredArea { get; set; }
    public string? EGRNNumber { get; set; }
    public VisaryRef? LandCategory { get; set; }
    public long? RowVersion { get; set; }
    public bool? Hidden { get; set; }
}

public sealed class PercentBetRaw
{
    public int ID { get; set; }
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
    public bool? SpecialRateCalc { get; set; }
    public double? BasePart { get; set; }
    public double? FloatRateMin { get; set; }
    public double? FloatRateMax { get; set; }
    public bool? Advance { get; set; }
    public DateTime? DateCreate { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public sealed class ConstructionSectionRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public VisaryRef? ConstructionSite { get; set; }
    public VisaryRef? Type { get; set; }
    public int? Stage { get; set; }
    public bool? HasUndergroundStage { get; set; }
    public string? Description { get; set; }
    public bool? HasLift { get; set; }
    public int? ResQuantity { get; set; }
    public int? NonresQuantity { get; set; }
    public int? OtherNonresQuantity { get; set; }
    public int? ParkingQuantity { get; set; }
    public double? ResProjectArea { get; set; }
    public double? ResAreaWithoutSummerRoom { get; set; }
    public double? NonresArea { get; set; }
    public double? OtherNonresArea { get; set; }
    public double? ParkingArea { get; set; }
    public double? AvgResArea { get; set; }
    public double? AvgResAreaWithoutSummerRoom { get; set; }
    public double? ResPercentage { get; set; }
    public string? SectionID { get; set; }
    public double? ClaimedCost { get; set; }
    public VisaryRef? BuildingMaterial { get; set; }
    public double? CostPerUnit { get; set; }
    public double? TotalCost { get; set; }
    public DateTime? Version { get; set; }
}

public sealed class ShareAgreementRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? Number { get; set; }
    public string? Date { get; set; }
    public string? ConstructionPermitNumber { get; set; }
    public string? ConstructionPermitDate { get; set; }
    public string? ProjectNumber { get; set; }
    public string? ProjectTitle { get; set; }
    public string? DeveloperPIN { get; set; }
    public string? DeveloperINN { get; set; }
    public string? StateRegistrationStatus { get; set; }
    public string? StateRegistrationNumber { get; set; }
    public string? FilingDate { get; set; }
    public string? RegistrationDate { get; set; }
    public string? SerialNumber { get; set; }
    public string? RoomKind { get; set; }
    public string? HouseNumber { get; set; }
    public string? SectionNumber { get; set; }
    public string? RoomNumber { get; set; }
    public string? ConditionalNumber { get; set; }
    public double? TotalArea { get; set; }
    public double? TotalLivingArea { get; set; }
    public string? CadastralNumber { get; set; }
    public double? Cost { get; set; }
    public string? Deadline { get; set; }
    public double? DepositedAmount { get; set; }
    public bool? IsBorrowedFunds { get; set; }
    public bool? IsPreferentialRate { get; set; }
    public double? BudgetFundsAmount { get; set; }
    public string? Street { get; set; }
    public string? DepositorFullName { get; set; }
    public double? MotherFundAmount { get; set; }
    public bool? IsRegisteredProvided { get; set; }
    public string? HouseNumberPermit { get; set; }
    public VisaryRef? Site { get; set; }
    public VisaryRef? Project { get; set; }
    public string? StageNumber { get; set; }
    public VisaryRef? Room { get; set; }
    // ValidityStatus иногда приходит числом, иногда строкой — принимаем оба варианта.
    // Маппер импорта rooms-form это поле не использует.
    public JsonElement? ValidityStatus { get; set; }
    public VisaryRef? RoomKindRef { get; set; }
}

/// <summary>
/// WBS (ИСР — иерархическая структура работ): главы и подстатьи бюджета объекта строительства.
/// Самоссылающаяся иерархия: <c>Глава 1</c> (Code="1.", ParentID=null)
///   → <c>Затраты на приобретение прав на ЗУ</c> (Code="1.1.", ParentID=ID главы).
/// Code присваивается сервером автоматически на основании Parent + порядка создания.
/// </summary>
public sealed class WbsRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? Code { get; set; }
    public int? ParentID { get; set; }
    public VisaryRef? Parent { get; set; }
    public int? ProjectID { get; set; }
    public VisaryRef? Project { get; set; }
    public VisaryRef? ConstructionSite { get; set; }
    public double? DeclaredSum { get; set; }
    public double? ConfirmedSum { get; set; }
}

/// <summary>
/// CostItem — одна строка графика финансирования (ГФ) подстатьи ИСР: квартальная
/// плановая сумма по конкретной <see cref="Wbs"/>-записи.
/// <para>
/// Период (<see cref="PlanPeriod"/>) — закрытый интервал с границами квартала
/// (Q3 2026 = <c>2026-07-01..2026-09-30</c>). <see cref="PlanQuarter"/> и
/// <see cref="PlanYear"/> на стороне Visary derived из <see cref="PlanPeriod"/>
/// — в POST-запросах их передавать НЕ нужно.
/// </para>
/// <para>
/// <see cref="Status"/> = <see cref="Dto.CostItemStatus.Plan"/> (число 70) — единственный
/// используемый импортом статус. Имя константы подсмотрено в HAR (<c>Context/har ГФ.txt</c>).
/// </para>
/// </summary>
public sealed class CostItemRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public int? WBSID { get; set; }
    public VisaryRef? WBS { get; set; }
    public double? PlanSum { get; set; }
    public CostItemPeriod? PlanPeriod { get; set; }
    public int? Status { get; set; }
    public int? PlanMonth { get; set; }
    public int? PlanQuarter { get; set; }
    public int? PlanYear { get; set; }
    public VisaryRef? ProjectDoc { get; set; }
    public DateTime? Version { get; set; }
}

/// <summary>
/// Период ГФ-записи. Visary хранит как пару ISO-дат (UTC); в HAR значения
/// формата <c>"2026-07-01T04:17:00.000Z"</c> — лишняя часть времени игнорируется
/// сервером, можно слать <c>00:00:00Z</c>.
/// </summary>
public sealed class CostItemPeriod
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
}
