using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace ZhipuClient;

#pragma warning disable CS8618 
public class StreamChunk
{
    public string Id { get; set; }
    public string RequestId { get; set; }
    public long Created { get; set; }
    public string Model { get; set; }
    public List<StreamChoice> Choices { get; set; } = new List<StreamChoice>();
    public Usage? Usage { get; set; }
}

public class StreamChoice
{
    public int Index { get; set; }
    public StreamDelta Delta { get; set; }
    public string? FinishReason { get; set; }
}
#pragma warning restore CS8618 
public class StreamDelta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
}

public class StreamingHttpRequest : IDisposable
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private Stream? _responseStream;
    private StreamReader? _streamReader;
    private bool _disposed;

    public StreamingHttpRequest(HttpClient client)
    {
        _client = client;
        _jsonOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
    }

    public async IAsyncEnumerable<StreamChunk> SendRequestAsync(
        string url,
        Dictionary<string, object> requestData,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);

        string jsonData = JsonSerializer.Serialize(requestData, _jsonOptions);
        req.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (headers != null)
        {
            foreach (var header in headers)
            {
                req.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var response = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"API请求失败: {response.StatusCode}, 响应: {errorContent}");
        }

        _responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        _streamReader = new StreamReader(_responseStream, Encoding.UTF8);

        await foreach (var chunk in ParseSseStreamAsync(_streamReader, cancellationToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<StreamChunk> ParseSseStreamAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StringBuilder buffer = new StringBuilder();

        while (true)
        {
            // Check if we've reached the end of the stream by peeking at the next character
            if (reader.Peek() < 0)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrEmpty(line))
            {
                if (buffer.Length > 0)
                {
                    var chunk = ParseSseData(buffer.ToString());
                    if (chunk != null)
                    {
                        yield return chunk;
                    }
                    buffer.Clear();
                }
                continue;
            }

            if (line.StartsWith("data:"))
            {
                string data = line.Substring(5).Trim();
                if (data == "[DONE]")
                {
                    break;
                }
                buffer.Append(data);
            }
        }

        if (buffer.Length > 0)
        {
            var chunk = ParseSseData(buffer.ToString());
            if (chunk != null)
            {
                yield return chunk;
            }
        }
    }

    private StreamChunk? ParseSseData(string jsonString)
    {
        try
        {
            var json = JsonSerializer.Deserialize<StreamChunk>(jsonString, _jsonOptions);
            return json;
        }
        catch
        {
            try
            {
                var node = JsonNode.Parse(jsonString);
                var chunk = new StreamChunk();

                if (node?["id"] != null)
                    chunk.Id = node["id"]!.GetValue<string>();
                if (node?["request_id"] != null)
                    chunk.RequestId = node["request_id"]!.GetValue<string>();
                if (node?["created"] != null)
                    chunk.Created = node["created"]!.GetValue<long>();
                if (node?["model"] != null)
                    chunk.Model = node["model"]!.GetValue<string>();

                if (node?["choices"] != null && node["choices"] is JsonArray choicesArray)
                {
                    foreach (var choiceNode in choicesArray)
                    {
                        var choice = new StreamChoice();
                        if (choiceNode?["index"] != null)
                            choice.Index = choiceNode["index"]!.GetValue<int>();
                        if (choiceNode?["finish_reason"] != null)
                            choice.FinishReason = choiceNode["finish_reason"]?.GetValue<string>();

                        if (choiceNode?["delta"] != null)
                        {
                            var deltaNode = choiceNode["delta"];
                            var delta = new StreamDelta();

                            if (deltaNode?["role"] != null)
                                delta.Role = deltaNode["role"]!.GetValue<string>();
                            if (deltaNode?["content"] != null)
                                delta.Content = deltaNode["content"]?.GetValue<string>();
                            if (deltaNode?["reasoning_content"] != null)
                                delta.ReasoningContent = deltaNode["reasoning_content"]?.GetValue<string>();

                            choice.Delta = delta;
                        }

                        chunk.Choices.Add(choice);
                    }
                }

                return chunk;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _streamReader?.Dispose();
            _responseStream?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
