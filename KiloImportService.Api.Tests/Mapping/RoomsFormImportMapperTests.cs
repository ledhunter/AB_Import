using KiloImportService.Api.Domain.Mapping;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// Тесты на нормализацию значения «Колич. комнат» в импорте Помещений.
/// Реальные пользовательские файлы содержат значения вида «1 к.», «п1», «1п»,
/// «10 к», «3-к» — раньше они отвергались как `invalid_number`. Теперь
/// маппер вытаскивает первую непрерывную группу цифр.
/// </summary>
public class RoomsFormImportMapperTests
{
    [Theory]
    // Базовые: чистое число / пусто / пробельная строка.
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("0", 0)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    // Просьба заказчика: суффиксы «к», «к.», «п» — берём ведущую цифру.
    [InlineData("1 к.", 1)]
    [InlineData("1 к", 1)]
    [InlineData("1к", 1)]
    [InlineData("2 к.", 2)]
    [InlineData("3 ком.", 3)]
    [InlineData("3-к", 3)]
    [InlineData("10 к", 10)]
    // «п1» / «1п» — префиксная буква не должна мешать.
    [InlineData("п1", 1)]
    [InlineData("1п", 1)]
    // Не-цифровые значения: «студия», прочерки, тире.
    [InlineData("студия", null)]
    [InlineData("—", null)]
    [InlineData("-", null)]
    [InlineData("апартамент", null)]
    // Берём ПЕРВЫЙ run — «1 к. 2» это однушка с дополнительной заметкой,
    // а не «комната 12». Так пользователь явно подразумевал «1»; склейка
    // «12» вводила бы фейковые двушки.
    [InlineData("1 к. 2", 1)]
    public void ExtractFirstRunOfDigits_NormalizesUserInput(string? raw, int? expected)
    {
        var actual = RoomsFormImportMapper.ExtractFirstRunOfDigits(raw);
        Assert.Equal(expected, actual);
    }
}
