using PdfSharp.Fonts;

namespace KiloImportService.Api.Pdf;

/// <summary>
/// FontResolver для PDFsharp — отдаёт TTF DejaVu Sans (поддерживает кириллицу),
/// читая с диска по списку возможных путей. Регистрируется один раз в Program.cs.
///
/// Почему DejaVu, а не Arial: Arial есть только на Windows, DejaVu — стандарт в Alpine
/// (`apk add ttf-dejavu` → `/usr/share/fonts/dejavu/`). Поддержка Windows-fallback
/// нужна только для локальной разработки без Docker.
/// </summary>
public sealed class PdfFontResolver : IFontResolver
{
    public const string DefaultFamily = "DejaVu Sans";

    // Кэш загруженных TTF — IFontResolver вызывается часто.
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // Возможные пути к шрифтам — пробуем по порядку.
    private static readonly string[] RegularPaths =
    [
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",              // Alpine ttf-dejavu
        "/usr/share/fonts/TTF/DejaVuSans.ttf",                 // Arch / некоторые Linux
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",     // Debian/Ubuntu
        @"C:\Windows\Fonts\arial.ttf",                         // Windows fallback (dev)
    ];

    private static readonly string[] BoldPaths =
    [
        "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        @"C:\Windows\Fonts\arialbd.ttf",
    ];

    public byte[]? GetFont(string faceName)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(faceName, out var cached)) return cached;

            var paths = faceName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                ? BoldPaths
                : RegularPaths;

            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                var bytes = File.ReadAllBytes(path);
                _cache[faceName] = bytes;
                return bytes;
            }

            throw new FileNotFoundException(
                $"TTF не найден для '{faceName}'. Проверьте, что в Docker установлен `apk add ttf-dejavu`, " +
                $"либо положите шрифт по одному из путей: {string.Join(", ", paths)}");
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Любой запрашиваемый family (Arial, Helvetica, sans-serif, …) сводим к DejaVu —
        // у нас всего два варианта начертания.
        var faceName = isBold ? "DejaVuSans-Bold" : "DejaVuSans";
        return new FontResolverInfo(faceName);
    }
}
