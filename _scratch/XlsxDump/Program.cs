using System.Text;
using ClosedXML.Excel;

Console.OutputEncoding = Encoding.UTF8;
var path = @"C:\Users\vkgsk\Desktop\AlfaProjects\import\AB_Import\AB_Import\_scratch\sample.xlsx";

using var wb = new XLWorkbook(path);
var outputs = wb.Worksheet("Outputs");

// Find rows that are likely the quarter/year header for the Outputs table
Console.WriteLine("=== Outputs rows 2..50 cols H..T (probable global header) ===");
for (int r = 2; r <= 50; r++)
{
    var parts = new List<string>();
    for (int c = 5; c <= 20; c++)
    {
        var cell = outputs.Cell(r, c);
        string val;
        try { val = cell.GetFormattedString() ?? ""; } catch { val = cell.GetString() ?? ""; }
        val = val.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (val.Length == 0) continue;
        if (val.Length > 14) val = val[..12] + "…";
        parts.Add($"{cell.Address.ColumnLetter}={val}");
    }
    if (parts.Count > 0) Console.WriteLine($"R{r}: " + string.Join(" | ", parts));
}
