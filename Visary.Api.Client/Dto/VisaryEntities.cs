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
    public string? MainSource { get; set; }
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
    public string? Status { get; set; }
    public string? INN { get; set; }
    public string? ClientID { get; set; }
    public string? Address { get; set; }
    public string? Code { get; set; }
    public string? OGRN { get; set; }
    public string? KPP { get; set; }
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
    public VisaryRef? RoomCategory { get; set; }
    public VisaryRef? ActiveShareAgreement { get; set; }
    public VisaryRef? CandidateShareAgreement { get; set; }
    public VisaryRef? ActiveEscrowAccount { get; set; }
    public VisaryRef? CandidateEscrowAccount { get; set; }
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
    public string? ValidityStatus { get; set; }
    public VisaryRef? RoomKindRef { get; set; }
}
