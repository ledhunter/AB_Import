namespace KiloImportService.Api.Domain.Visary;

public sealed class ConstructionProjectRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? IdentifierKK { get; set; }
    public string? IdentifierZPLM { get; set; }
    public bool? Hidden { get; set; }
}
