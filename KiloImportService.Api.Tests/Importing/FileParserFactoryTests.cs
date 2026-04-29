using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;

namespace KiloImportService.Api.Tests.Importing;

public class FileParserFactoryTests
{
    private static FileParserFactory CreateFactory() =>
        new(new IFileParser[]
        {
            new CsvParser(),
            new XlsxParser(),
            new XlsParser(),
            new XlsbParser(),
        });

    [Theory]
    [InlineData(FileFormat.Csv,  typeof(CsvParser))]
    [InlineData(FileFormat.Xlsx, typeof(XlsxParser))]
    [InlineData(FileFormat.Xls,  typeof(XlsParser))]
    [InlineData(FileFormat.Xlsb, typeof(XlsbParser))]
    public void GetParser_ReturnsCorrectImplementation(FileFormat format, Type expected)
    {
        var factory = CreateFactory();
        var parser = factory.GetParser(format);
        Assert.IsType(expected, parser);
        Assert.Equal(format, parser.Format);
    }

    [Fact]
    public void DetectFromFileName_WorksForKnownExtensions()
    {
        Assert.Equal(FileFormat.Csv,  FileFormatExtensions.DetectFromFileName("data.csv"));
        Assert.Equal(FileFormat.Xls,  FileFormatExtensions.DetectFromFileName("Report.XLS"));
        Assert.Equal(FileFormat.Xlsx, FileFormatExtensions.DetectFromFileName("path/to/Report.xlsx"));
        Assert.Equal(FileFormat.Xlsb, FileFormatExtensions.DetectFromFileName("Report.xlsb"));
    }

    [Fact]
    public void DetectFromFileName_ReturnsNull_ForUnknown()
    {
        Assert.Null(FileFormatExtensions.DetectFromFileName("data.txt"));
        Assert.Null(FileFormatExtensions.DetectFromFileName("noext"));
        Assert.Null(FileFormatExtensions.DetectFromFileName(""));
    }
}
