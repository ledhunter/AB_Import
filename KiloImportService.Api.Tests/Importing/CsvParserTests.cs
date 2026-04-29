using System.Text;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;

namespace KiloImportService.Api.Tests.Importing;

public class CsvParserTests
{
    private readonly CsvParser _parser = new();

    private static Stream Csv(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public void Format_Is_Csv() => Assert.Equal(FileFormat.Csv, _parser.Format);

    [Fact]
    public async Task ParsesHeadersAndRows_WithCommaDelimiter()
    {
        var csv = "Name,Age\nAlice,30\nBob,25\n";
        var result = await _parser.ParseAsync(Csv(csv));

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "Name", "Age" }, result.Headers);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Alice", result.Rows[0].Cells["Name"]);
        Assert.Equal("30", result.Rows[0].Cells["Age"]);
        Assert.Equal(2, result.Rows[0].SourceRowNumber);
        Assert.Equal(3, result.Rows[1].SourceRowNumber);
        Assert.Equal("CSV", result.Rows[0].Sheet);
    }

    [Fact]
    public async Task ParsesSemicolonDelimiter_AutoDetected()
    {
        var csv = "Name;Age\nAlice;30\nBob;25\n";
        var result = await _parser.ParseAsync(Csv(csv));

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Alice", result.Rows[0].Cells["Name"]);
    }

    [Fact]
    public async Task SkipsEmptyRows()
    {
        var csv = "A,B\n1,2\n\n3,4\n";
        var result = await _parser.ParseAsync(Csv(csv));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("1", result.Rows[0].Cells["A"]);
        Assert.Equal("3", result.Rows[1].Cells["A"]);
    }

    [Fact]
    public async Task TrimsHeadersAndValues()
    {
        var csv = "  Name  ,  Age  \n Alice , 30 \n";
        var result = await _parser.ParseAsync(Csv(csv));

        Assert.Equal(new[] { "Name", "Age" }, result.Headers);
        Assert.Equal("Alice", result.Rows[0].Cells["Name"]);
        Assert.Equal("30", result.Rows[0].Cells["Age"]);
    }

    [Fact]
    public async Task EmptyFile_ReturnsError()
    {
        var result = await _parser.ParseAsync(Csv(""));
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task ReadsUtf8Bom()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = Encoding.UTF8.GetBytes("Имя,Возраст\nИван,30\n");
        var stream = new MemoryStream();
        stream.Write(bom);
        stream.Write(body);
        stream.Position = 0;

        var result = await _parser.ParseAsync(stream);

        Assert.Empty(result.Errors);
        Assert.Equal("Имя", result.Headers[0]);
        Assert.Equal("Иван", result.Rows[0].Cells["Имя"]);
    }

    [Fact]
    public async Task RespectsCancellationToken()
    {
        var csv = "A,B\n1,2\n3,4\n5,6\n";
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _parser.ParseAsync(Csv(csv), cts.Token));
    }
}
