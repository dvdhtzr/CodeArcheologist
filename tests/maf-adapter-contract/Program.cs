using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;

const int MaxLength = 240;
const string DefaultModel = "qwen3.5:9b";
const string Prompt =
    "Respond ONLY as JSON with two string fields, answer and unknown. " +
    "Both must be between 5 and 240 characters. " +
    "Does seeing the C# text 'app.MapGet' alone prove that an HTTP endpoint " +
    "was registered at runtime? Answer cautiously and identify uncertainty.";

static string Validate(string? text)
{
    if (string.IsNullOrEmpty(text)) return "empty-model-response";
    if (text.Length > 4096) return "oversize-model-response";
    try
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return "root-not-object";
        if (root.EnumerateObject().Count() != 2 ||
            !root.TryGetProperty("answer", out var answer) ||
            !root.TryGetProperty("unknown", out var unknown) ||
            answer.ValueKind != JsonValueKind.String ||
            unknown.ValueKind != JsonValueKind.String)
            return "schema-mismatch";
        var answerLength = answer.GetString()!.Length;
        var unknownLength = unknown.GetString()!.Length;
        if (answerLength is < 5 or > MaxLength)
            return "answer-length-invalid(" + answerLength + ")";
        if (unknownLength is < 5 or > MaxLength)
            return "unknown-length-invalid(" + unknownLength + ")";
        return "valid";
    }
    catch (JsonException)
    {
        return "invalid-json-or-extra-text";
    }
}

if (args.Length > 0 && args[0] is "--gate-b-fixture" or "--gate-b-fixture-faults")
{
    Environment.ExitCode = await GateBFixtureRun.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] is "--gate-b-run" or "--gate-b-diagnose" or "--gate-b-faults")
{
    Environment.ExitCode = await GateBLocalRun.RunAsync(args);
    return;
}

if (args.Length == 1 && args[0] == "--gate-c1-self-test")
{
    GateCSelfTests.Run();
    return;
}

if (args.Length == 1 && args[0] == "--gate-b-self-test")
{
    GateBSelfTests.Run();
    return;
}

if (args.Length > 2 ||
    (args.Length > 0 && !int.TryParse(args[0], out _)))
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/maf-adapter-contract -- [attempts 1..10] [model]");
    Environment.ExitCode = 2;
    return;
}
var attempts = args.Length > 0 && int.TryParse(args[0], out var count) ? count : 5;
var model = args.Length > 1 ? args[1] : DefaultModel;
if (attempts is < 1 or > 10 || model.Length is < 1 or > 100 ||
    model.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' or '/')))
{
    Console.Error.WriteLine("Invalid count or model name.");
    Environment.ExitCode = 2;
    return;
}

var schemaText = """
{
  "type": "object",
  "properties": {
    "answer": { "type": "string", "minLength": 5, "maxLength": 240 },
    "unknown": { "type": "string", "minLength": 5, "maxLength": 240 }
  },
  "required": ["answer", "unknown"],
  "additionalProperties": false
}
""";
using var schemaDocument = JsonDocument.Parse(schemaText);
var responseFormat = ChatResponseFormat.ForJsonSchema(
    schemaDocument.RootElement.Clone(), "CodeArcheologistContract",
    "Bounded local structured-output contract.");
using var client = new OllamaApiClient(new Uri("http://127.0.0.1:11434"), model);
AIAgent agent = client.AsAIAgent(
    name: "ContractProbe",
    instructions: "Return ONLY the requested JSON schema. Do not invent runtime facts.");
var options = new AgentRunOptions { ResponseFormat = responseFormat };

var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
for (var i = 1; i <= attempts; i++)
{
    string result;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    try
    {
        // Real Microsoft Agent Framework invocation over OllamaSharp IChatClient,
        // with an explicit JSON-schema response format rather than prompt alone.
        var response = await agent.RunAsync(Prompt, options: options,
            cancellationToken: timeout.Token);
        result = Validate(response.Text);
    }
    catch (OperationCanceledException)
    {
        result = "provider-timeout";
    }
    catch (HttpRequestException)
    {
        result = "provider-unavailable";
    }
    catch (JsonException)
    {
        result = "provider-json-error";
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        // No untrusted response content is printed.
        result = "adapter-exception-" + error.GetType().Name;
    }
    outcomes[result] = outcomes.GetValueOrDefault(result) + 1;
    Console.WriteLine($"Attempt {i}/{attempts}: {result}");
}
Console.WriteLine("MAF/OllamaSharp contract summary: " +
    string.Join(", ", outcomes.OrderBy(k => k.Key, StringComparer.Ordinal)
        .Select(k => k.Key + "=" + k.Value)));
if (outcomes.GetValueOrDefault("valid") != attempts)
{
    Console.Error.WriteLine("RED: MAF/OllamaSharp adapter failed one or more structured-output attempts.");
    Environment.ExitCode = 1;
}
else Console.WriteLine("GREEN: all independent MAF/OllamaSharp structured-output attempts passed.");
