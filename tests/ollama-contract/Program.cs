using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

const int MaxResponseBytes = 32768;
const int MaxAnswerLength = 240;
const int MaxUnknownLength = 240;
const string DefaultModel = "qwen3.5:9b";

static string Validate(string? modelText)
{
    if (modelText is null || modelText.Length == 0) return "empty-model-response";
    if (modelText.Length > 4096) return "oversize-model-response";
    try
    {
        using var parsed = JsonDocument.Parse(modelText);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return "root-not-object";
        if (root.EnumerateObject().Count() != 2 ||
            !root.TryGetProperty("answer", out var answer) ||
            !root.TryGetProperty("unknown", out var unknown) ||
            answer.ValueKind != JsonValueKind.String ||
            unknown.ValueKind != JsonValueKind.String)
            return "schema-mismatch";
        var answerLength = answer.GetString()!.Length;
        var unknownLength = unknown.GetString()!.Length;
        if (answerLength is < 5 or > MaxAnswerLength)
            return "answer-length-invalid(" + answerLength + ")";
        if (unknownLength is < 5 or > MaxUnknownLength)
            return "unknown-length-invalid(" + unknownLength + ")";
        return "valid";
    }
    catch (JsonException)
    {
        return "invalid-json-or-extra-text";
    }
}

static void SelfTest()
{
    var samples = new (string? Text, string Expected)[]
    {
        (null, "empty-model-response"),
        ("", "empty-model-response"),
        ("not json", "invalid-json-or-extra-text"),
        ("{} trailing", "invalid-json-or-extra-text"),
        ("{}", "schema-mismatch"),
        ("[]", "root-not-object"),
        ("{\"answer\":\"fine\",\"unknown\":\"none yet\",\"extra\":1}", "schema-mismatch"),
        ("{\"answer\":\"valid\",\"unknown\":\"\"}", "unknown-length-invalid(0)"),
        ("{\"answer\":\"A concise answer\",\"unknown\":\"Runtime unverified\"}", "valid"),
        (new string('x', 4097), "oversize-model-response")
    };
    foreach (var (sample, expected) in samples)
    {
        var actual = Validate(sample);
        if (actual != expected)
            throw new InvalidOperationException(
                $"Contract self-test failed: expected {expected}, got {actual}.");
    }
    Console.WriteLine("GREEN: 10 deterministic schema-contract cases passed (no model required).");
}

if (args.Length == 1 && args[0] == "--self-test")
{
    SelfTest();
    return;
}

if (args.Length > 2 || (args.Length == 1 && !int.TryParse(args[0], out _)))
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/ollama-contract -- [attempts 1..10] [model]");
    Environment.ExitCode = 2;
    return;
}
var attempts = args.Length > 0 && int.TryParse(args[0], out var count) ? count : 5;
var model = args.Length > 1 ? args[1] : DefaultModel;
if (attempts is < 1 or > 10 || model.Length is < 1 or > 100 ||
    model.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' or '/')))
{
    Console.Error.WriteLine("Invalid attempt count or model name.");
    Environment.ExitCode = 2;
    return;
}

using var client = new HttpClient
{
    BaseAddress = new Uri("http://127.0.0.1:11434/"),
    Timeout = TimeSpan.FromSeconds(90)
};
var schema = new
{
    type = "object",
    properties = new
    {
        answer = new { type = "string", minLength = 5, maxLength = MaxAnswerLength },
        unknown = new { type = "string", minLength = 5, maxLength = MaxUnknownLength }
    },
    required = new[] { "answer", "unknown" },
    additionalProperties = false
};
var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
for (var i = 1; i <= attempts; i++)
{
    string result;
    try
    {
        using var response = await client.PostAsJsonAsync("api/generate", new
        {
            model,
            prompt = "Respond ONLY as a JSON object with answer and unknown. " +
                     "Both string fields must be at least 5 and at most 240 characters. " +
                     "Question: Does the C# text 'app.MapGet' alone prove that an HTTP endpoint " +
                     "was registered at runtime? Answer cautiously and identify uncertainty.",
            format = schema,
            stream = false,
            think = false,
            options = new { temperature = 0, num_predict = 220 }
        });
        if (response.StatusCode != HttpStatusCode.OK)
        {
            result = "provider-http-" + (int)response.StatusCode;
        }
        else
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                if (memory.Length + read > MaxResponseBytes) break;
                memory.Write(buffer, 0, read);
            }
            if (memory.Length + read > MaxResponseBytes)
                result = "oversize-provider-envelope";
            else
            {
                memory.Position = 0;
                try
                {
                    using var envelope = await JsonDocument.ParseAsync(memory);
                    var root = envelope.RootElement;
                    result = root.TryGetProperty("done", out var done) &&
                        done.ValueKind == JsonValueKind.True &&
                        root.TryGetProperty("response", out var modelResponse) &&
                        modelResponse.ValueKind == JsonValueKind.String
                        ? Validate(modelResponse.GetString())
                        : "invalid-provider-envelope";
                }
                catch (JsonException)
                {
                    result = "invalid-provider-envelope";
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        result = "provider-timeout";
    }
    catch (HttpRequestException)
    {
        result = "provider-unavailable";
    }
    outcomes[result] = outcomes.GetValueOrDefault(result) + 1;
    Console.WriteLine($"Attempt {i}/{attempts}: {result}");
}
Console.WriteLine("Contract summary: " +
    string.Join(", ", outcomes.OrderBy(k => k.Key, StringComparer.Ordinal)
        .Select(k => k.Key + "=" + k.Value)));
if (outcomes.GetValueOrDefault("valid") != attempts)
{
    Console.Error.WriteLine("RED: one or more model attempts did not meet the host schema contract.");
    Environment.ExitCode = 1;
}
else Console.WriteLine("GREEN: all independent local Ollama structured-output attempts passed.");
