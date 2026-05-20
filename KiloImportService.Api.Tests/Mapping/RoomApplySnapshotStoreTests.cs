using System.Text.Json;
using KiloImportService.Api.Domain.Mapping;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// Тесты на <see cref="RoomApplySnapshotStore"/> — статические Hash/BuildKey
/// функции. Они — фундамент инкрементального импорта Помещений: при повторной
/// загрузке маппер сравнивает хэш текущего <c>MappedValues</c> с тем, что
/// записан в snapshot. Любое регрессионное изменение в канонизации может
/// сделать diff-skip либо ложноположительным (упустим обновление в Visary),
/// либо ложноотрицательным (на каждый PATCH будем заново стучаться).
///
/// Покрытие:
///   1) хэш стабилен между запусками (snapshot никогда не «протухнет» из-за обновления СЛИ);
///   2) хэш игнорирует порядок ключей в <c>MappedValues</c>;
///   3) хэш ИЗМЕНЯЕТСЯ при изменении значимых полей;
///   4) хэш НЕ меняется при изменении полей, не входящих в HashedMappedFields
///      (Sheet, DeveloperPin, …) — чтобы переименование листа не вызывало re-apply;
///   5) BuildKey нормализует строки (Trim+ToLowerInvariant) и приводит NULL kindId к 0.
/// </summary>
public class RoomApplySnapshotStoreTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ComputeMappedHash_StableAcrossCalls()
    {
        var v = Doc("""
            { "RoomNumber":"15","RoomKindId":3,"SectionTitle":"1.1","BuildingSection":"п1",
              "ProjectArea":42.5,"ShareAgreementNumber":"ДДУ-1" }
            """);

        var h1 = RoomApplySnapshotStore.ComputeMappedHash(v);
        var h2 = RoomApplySnapshotStore.ComputeMappedHash(v);

        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length); // SHA256 hex
    }

    [Fact]
    public void ComputeMappedHash_OrderIndependent()
    {
        // Маппер сериализует Dictionary<string,object?> — порядок ключей не гарантирован.
        // SortedDictionary внутри ComputeMappedHash должен это нейтрализовать.
        var a = Doc("""{ "RoomNumber":"15","RoomKindId":3,"SectionTitle":"1.1" }""");
        var b = Doc("""{ "SectionTitle":"1.1","RoomKindId":3,"RoomNumber":"15" }""");

        Assert.Equal(
            RoomApplySnapshotStore.ComputeMappedHash(a),
            RoomApplySnapshotStore.ComputeMappedHash(b));
    }

    [Fact]
    public void ComputeMappedHash_ChangesWhenSignificantFieldChanges()
    {
        var baseDoc = Doc("""{ "RoomNumber":"15","ProjectArea":42.5,"ShareAgreementNumber":"ДДУ-1" }""");
        var changed = Doc("""{ "RoomNumber":"15","ProjectArea":42.6,"ShareAgreementNumber":"ДДУ-1" }""");

        Assert.NotEqual(
            RoomApplySnapshotStore.ComputeMappedHash(baseDoc),
            RoomApplySnapshotStore.ComputeMappedHash(changed));
    }

    [Fact]
    public void ComputeMappedHash_IgnoresFieldsOutsideHashedSet()
    {
        // Sheet и DeveloperPin не входят в HashedMappedFields — изменение этих
        // диагностических полей не должно ломать diff-skip.
        var baseDoc = Doc("""
            { "RoomNumber":"15","SectionTitle":"1.1","Sheet":"Квартиры","DeveloperPin":"123" }
            """);
        var withDiffSheet = Doc("""
            { "RoomNumber":"15","SectionTitle":"1.1","Sheet":"Квартиры2","DeveloperPin":"999" }
            """);

        Assert.Equal(
            RoomApplySnapshotStore.ComputeMappedHash(baseDoc),
            RoomApplySnapshotStore.ComputeMappedHash(withDiffSheet));
    }

    [Fact]
    public void ComputeMappedHash_NormalizesStringCaseAndWhitespace()
    {
        var a = Doc("""{ "RoomNumber":"15A","SectionTitle":"1.1" }""");
        var b = Doc("""{ "RoomNumber":"  15a  ","SectionTitle":"1.1" }""");

        Assert.Equal(
            RoomApplySnapshotStore.ComputeMappedHash(a),
            RoomApplySnapshotStore.ComputeMappedHash(b));
    }

    [Theory]
    [InlineData("1.1", " 1.1 ")]
    [InlineData("Квартиры", "квартиры")]
    [InlineData("п1", " П1 ")]
    public void BuildKey_NormalizesStrings(string left, string right)
    {
        // Sheet/Section/RoomNumber/BuildingSection нормализуются Trim()+ToLowerInvariant.
        // Без этого «1.1» и « 1.1 » создали бы две разные snapshot-записи, и diff-skip
        // не сработал бы для повторного импорта того же файла, открытого в другой
        // редакции Excel (которая иначе режет пробелы).
        var a = RoomApplySnapshotStore.BuildKey(42, left, left, 3, left, left);
        var b = RoomApplySnapshotStore.BuildKey(42, right, right, 3, right, right);
        Assert.Equal(a, b);
    }

    [Fact]
    public void BuildKey_NullKindIdBecomesZero()
    {
        // Postgres трактует NULL в unique-index как «не равно NULL», что обходило бы
        // дедуп RoomApplySnapshot для строк без RoomKindId. Превращаем null в 0 на
        // уровне ключа — гарантия, что бизнес-ключ строго детерминирован.
        var withNull = RoomApplySnapshotStore.BuildKey(42, "Sheet", "1.1", null, "15", "п1");
        var withZero = RoomApplySnapshotStore.BuildKey(42, "Sheet", "1.1", 0,    "15", "п1");
        Assert.Equal(withZero, withNull);
    }

    [Fact]
    public void BuildKey_DifferentSitesProduceDifferentKeys()
    {
        var k1 = RoomApplySnapshotStore.BuildKey(42, "Квартиры", "1.1", 3, "15", "п1");
        var k2 = RoomApplySnapshotStore.BuildKey(99, "Квартиры", "1.1", 3, "15", "п1");
        Assert.NotEqual(k1, k2);
    }
}
