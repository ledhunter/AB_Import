using System.Collections.Generic;

namespace KiloImportService.Api.Domain.Visary;

public sealed class ListViewResponse<T>
{
    public List<T> Rows { get; set; } = new();
    public int TotalRows { get; set; }
}
