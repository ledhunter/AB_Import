using System.Collections.Generic;

namespace Visary.Api.Dto;

public sealed class ListViewResponse<T>
{
    public List<T> Data { get; set; } = new();
    public int Total { get; set; }
}
