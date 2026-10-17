using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class Program
{
    private const int MaximumNativeMessageBytes = 1024 * 1024;
    private const int MaximumSnapshotEnvelopeBytes = 192 * 1024;
    private const int MaximumApplyResultBytes = 16 * 1024;
    private static readonly Uri SnapshotUri = new("http://127.0.0.1:39174/v1/edge-policy/snapshot/local");
    private static readonly Uri ApplyResultUri = new("http://127.0.0.1:39174/v1/edge-policy/apply-result/local");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static async Task<int> Main()
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var gate = new SemaphoreSlim(1, 1);

        while (true)
        {
            byte[]? messageBytes;
            try { messageBytes = await ReadNativeMessageAsync(input, MaximumNativeMessageBytes).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
            {
                Console.Error.WriteLine("Native message input failed: " + exception.Message);
                return 1;
            }
            if (messageBytes is null) return 0;

            try
            {
                using var message = JsonDocument.Parse(messageBytes, new JsonDocumentOptions { MaxDepth = 12 });
                if (message.RootElement.ValueKind != JsonValueKind.Object ||
                    !message.RootElement.TryGetProperty("messageType", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Native message has no messageType.");

                switch (typeElement.GetString())
                {
                    case "hello":
                    case "resync":
                        await SendSnapshotAsync(client, output, gate).ConfigureAwait(false);
                        break;
                    case "websitePolicy.applyResult":
                        if (messageBytes.Length > MaximumApplyResultBytes)
                            throw new InvalidDataException("Apply result exceeds the size limit.");
                        await ForwardApplyResultAsync(client, messageBytes, output, gate).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException("Unsupported native message type.");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              IOException or InvalidDataException or JsonException or
                                              InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine("Native message handling failed: " + exception.Message);
            }
        }
    }

    private static async Task SendSnapshotAsync(HttpClient client, Stream output, SemaphoreSlim gate)
    {
        using var response = await client.GetAsync(SnapshotUri, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Student Agent snapshot request failed.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is { } length && length > MaximumSnapshotEnvelopeBytes)
            throw new InvalidDataException("Student Agent snapshot exceeds the size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumSnapshotEnvelopeBytes)
                throw new InvalidDataException("Student Agent snapshot exceeds the size limit.");
            buffer.Write(chunk, 0, read);
        }

        using var envelope = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        if (envelope.RootElement.ValueKind != JsonValueKind.Object ||
            !envelope.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.String ||
            !envelope.RootElement.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String ||
            !envelope.RootElement.TryGetProperty("publicKeyPem", out var publicKey) || publicKey.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Student Agent returned an invalid signed snapshot envelope.");

        using var outputBuffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outputBuffer))
        {
            writer.WriteStartObject();
            writer.WriteString("messageType", "websitePolicy.agentEnvelope");
            writer.WritePropertyName("envelope");
            envelope.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }
        await WriteNativeMessageAsync(output, outputBuffer.ToArray(), gate).ConfigureAwait(false);
    }

    private static async Task ForwardApplyResultAsync(HttpClient client, byte[] messageBytes, Stream output,
        SemaphoreSlim gate)
    {
        using var message = JsonDocument.Parse(messageBytes, new JsonDocumentOptions { MaxDepth = 12 });
        var resultBytes = JsonSerializer.SerializeToUtf8Bytes(message.RootElement, JsonOptions);
        using var content = new ByteArrayContent(resultBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        using var response = await client.PostAsync(ApplyResultUri, content).ConfigureAwait(false);
        await WriteNativeMessageAsync(output, JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "websitePolicy.resultAck",
            accepted = response.IsSuccessStatusCode
        }), gate).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadNativeMessageAsync(Stream input, int maximumBytes)
    {
        var header = new byte[sizeof(int)];
        var first = await input.ReadAsync(header.AsMemory(0, 1)).ConfigureAwait(false);
        if (first == 0) return null;
        await ReadExactlyAsync(input, header.AsMemory(1)).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maximumBytes)
            throw new InvalidDataException("Native message length is invalid.");
        var payload = new byte[length];
        await ReadExactlyAsync(input, payload).ConfigureAwait(false);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await input.ReadAsync(destination[offset..]).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Native message ended before its declared length.");
            offset += read;
        }
    }

    private static async Task WriteNativeMessageAsync(Stream output, byte[] payload, SemaphoreSlim gate)
    {
        if (payload.Length is <= 0 or > MaximumNativeMessageBytes)
            throw new InvalidDataException("Native response length is invalid.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header).ConfigureAwait(false);
            await output.WriteAsync(payload).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
