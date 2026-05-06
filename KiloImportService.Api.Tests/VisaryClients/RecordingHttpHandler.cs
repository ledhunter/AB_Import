using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KiloImportService.Api.Tests.VisaryClients;

/// <summary>
/// Тестовый HttpMessageHandler: запоминает каждый HTTP-запрос и отдаёт сценарий ответов.
/// Нужен, чтобы контракт-тесты могли проверить URL, HTTP-метод, заголовки и тело без
/// реального похода в Visary.
/// </summary>
public sealed class RecordingHttpHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> RequestBodies { get; } = new();

    private readonly Queue<HttpResponseMessage> _responses = new();

    public RecordingHttpHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(ct));
        return _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
    }
}
