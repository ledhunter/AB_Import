using KiloImportService.Api.Domain.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace KiloImportService.Api.Tests.Pipeline;

public class ImportSessionCancellationTests
{
    private static ImportSessionCancellation Create() =>
        new(NullLogger<ImportSessionCancellation>.Instance);

    [Fact]
    public void Register_ReturnsTokenThatIsNotCancelled()
    {
        using var c = Create();
        var token = c.Register(Guid.NewGuid());
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_TokenForRegisteredSession_BecomesCancelled()
    {
        using var c = Create();
        var id = Guid.NewGuid();
        var token = c.Register(id);

        var ok = c.Cancel(id);

        Assert.True(ok);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_UnknownSession_ReturnsFalse()
    {
        using var c = Create();
        Assert.False(c.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public void Unregister_RemovesCts_AndCancelAfterReturnsFalse()
    {
        using var c = Create();
        var id = Guid.NewGuid();
        var token = c.Register(id);
        c.Unregister(id);
        Assert.False(c.Cancel(id));
        // Токен после unregister больше не связан с реестром,
        // но и не должен внезапно перейти в Cancelled — Dispose CTS тоже не Cancel'ит.
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void DoubleRegister_ForSameSession_OldTokenStaysAlive_NewTokenWorks()
    {
        using var c = Create();
        var id = Guid.NewGuid();
        var oldToken = c.Register(id);
        // Старый CTS уже dispose'нут — игнорируем oldToken после повторной регистрации.
        var newToken = c.Register(id);

        Assert.False(newToken.IsCancellationRequested);
        c.Cancel(id);
        Assert.True(newToken.IsCancellationRequested);

        // Старый токен мог либо стать Cancelled (если CTS не dispose), либо
        // ObjectDisposedException на доступе. Главное — Cancel прошёл для нового.
        // Документируем нынешнее поведение: старый токен остаётся не-Cancelled
        // (мы вызываем Dispose до проверки IsCancellationRequested).
        Assert.False(oldToken.IsCancellationRequested);
    }
}
