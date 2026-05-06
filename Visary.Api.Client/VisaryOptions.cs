namespace Visary.Api.Dto;

public sealed class VisaryOptions
{
    public const string SectionName = "Visary";

    public string BaseUrl { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;

    public int SyncPageSize { get; set; } = 200;
    public int DefaultPageSize { get; set; } = 50;
    public int LargePageSize { get; set; } = 500;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
