using System.Text;
using KiloImportService.Api.Domain.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KiloImportService.Api.Tests.Pipeline;

/// <summary>
/// Тесты LocalFileStorage используют уникальную папку в %TEMP% и удаляют её
/// в Dispose, чтобы тесты не оставляли мусор и не зависели от порядка.
/// </summary>
public class LocalFileStorageTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kilo-tests-" + Guid.NewGuid().ToString("N"));
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ImportStorage:Path"] = _root,
            })
            .Build();
        _storage = new LocalFileStorage(cfg, NullLogger<LocalFileStorage>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SaveAsync_WritesFile_AndReturnsRelativePath()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var rel = await _storage.SaveAsync(content, "test.csv", CancellationToken.None);

        Assert.NotEmpty(rel);
        // Путь относительный — содержит yyyy/MM/dd/{guid}_test.csv.
        Assert.EndsWith("test.csv", rel);
        Assert.DoesNotContain('\\', rel);

        // Файл реально лежит на диске.
        var full = Path.Combine(_root, rel);
        Assert.True(File.Exists(full));
        Assert.Equal("hello", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsContent()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("world"));
        var rel = await _storage.SaveAsync(input, "data.xlsx", CancellationToken.None);

        await using var read = await _storage.OpenReadAsync(rel, CancellationToken.None);
        using var sr = new StreamReader(read);
        Assert.Equal("world", await sr.ReadToEndAsync());
    }

    [Fact]
    public async Task SaveAsync_SanitizesInvalidFileNameChars()
    {
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var dirtyName = "bad<>name?.csv";
        var rel = await _storage.SaveAsync(content, dirtyName, CancellationToken.None);

        // Все недопустимые символы заменяются на '_'.
        Assert.DoesNotContain('<', rel);
        Assert.DoesNotContain('>', rel);
        Assert.DoesNotContain('?', rel);
        Assert.Contains("bad__name_.csv", rel);
    }

    [Fact]
    public async Task SaveAsync_ResetsContentPositionToZero()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        using var ms = new MemoryStream(bytes);
        ms.Position = ms.Length; // имитируем уже прочитанный поток
        var rel = await _storage.SaveAsync(ms, "f.csv", CancellationToken.None);
        var actual = await File.ReadAllTextAsync(Path.Combine(_root, rel));
        Assert.Equal("payload", actual);
    }

    [Fact]
    public async Task SaveAsync_TwoFilesWithSameName_ProduceDifferentPaths()
    {
        using var a = new MemoryStream(Encoding.UTF8.GetBytes("a"));
        using var b = new MemoryStream(Encoding.UTF8.GetBytes("b"));
        var p1 = await _storage.SaveAsync(a, "same.csv", CancellationToken.None);
        var p2 = await _storage.SaveAsync(b, "same.csv", CancellationToken.None);
        Assert.NotEqual(p1, p2);
    }

    // ─────────────── Path Traversal hardening (см. doc 144) ───────────────
    // На Linux '\' НЕ входит в Path.GetInvalidFileNameChars, поэтому без
    // Path.GetFileName в Sanitize старая версия пропускала кросс-OS payload
    // `..\..\..\evil`. После фикса любые path-составляющие вырезаются.

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\..\\windows\\system32\\drivers\\etc\\hosts")]
    [InlineData("/etc/shadow")]
    [InlineData("\\\\server\\share\\evil")]
    public async Task SaveAsync_StripsPathComponentsFromOriginalFileName(string dirty)
    {
        using var content = new MemoryStream(new byte[] { 1 });
        var rel = await _storage.SaveAsync(content, dirty, CancellationToken.None);
        // Финальный файл лежит ровно внутри _root, без побега наверх.
        var full = Path.GetFullPath(Path.Combine(_root, rel));
        var rootFull = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(rootFull, full, StringComparison.Ordinal);
        Assert.True(File.Exists(full));
        // Имя файла НЕ содержит '/' / '\' (path-разделители вырезаны).
        var leaf = Path.GetFileName(rel);
        Assert.DoesNotContain('/', leaf);
        Assert.DoesNotContain('\\', leaf);
    }

    [Theory]
    // Кросс-OS payload'ы: '..' отлавливается явной Contains-проверкой ДО Path.Combine,
    // '/etc/shadow' — Path.IsPathRooted true на Linux+Windows; OS-зависимые
    // 'C:\...' / '\\server\share' оставляем за пределами теста (на Linux IsPathRooted
    // там false и containment-check «пройдёт» — но File.OpenRead просто не найдёт файл).
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\etc\\passwd")]
    [InlineData("/etc/shadow")]
    public async Task OpenReadAsync_RejectsTraversalAndAbsolutePaths(string maliciousPath)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _storage.OpenReadAsync(maliciousPath, CancellationToken.None));
    }

    [Fact]
    public async Task OpenReadAsync_RejectsEmptyOrWhitespacePath()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.OpenReadAsync("", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.OpenReadAsync("   ", CancellationToken.None));
    }
}
