namespace KiloImportService.Api.Domain.Visary;

public sealed class VisaryApiOptions
{
    public const string SectionName = "Visary";

    public string BaseUrl { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;
    public int SyncPageSize { get; set; } = 200;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
