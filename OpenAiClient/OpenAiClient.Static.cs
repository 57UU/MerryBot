using CommonLib;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenAiClient;

public partial class OpenAiCompatible
{
    private const int _defaultMaxConcurrency = 5;
    private static SemaphoreSlim _semaphore = new(_defaultMaxConcurrency, _defaultMaxConcurrency);
    private static int _maxConcurrency = _defaultMaxConcurrency;
    private static readonly object _semaphoreLock = new();
    public static int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            lock (_semaphoreLock)
            {
                _maxConcurrency = value;
                _semaphore = new SemaphoreSlim(value, value);
            }
        }
    }

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    private static async Task<string> SendRequestAsync(HttpClient client, string url, string jsonData, ISimpleLogger logger)
    {
        await _semaphore.WaitAsync();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            req.Content.Headers.ContentType = JsonMediaType;

            HttpResponseMessage response = await client.SendAsync(req);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.Error($"OpenAiClient API Error");

                string rep = await response.Content.ReadAsStringAsync();

                var err = JsonSerializer.Deserialize<ApiResponse>(rep)!;
                StringBuilder sb = new("内容问题：");
                foreach (var i in err.ContentFilters)
                {
                    sb.Append($"[{i.Role}:{i.Level}]");
                }
                sb.AppendLine(rep);
                throw new Exception(sb.ToString());


            }
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception)
        {
            throw;
        }
        finally
        {
            _semaphore.Release(1);
        }
    }
}