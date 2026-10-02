using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using DesktopBuddy.Native;

namespace DesktopBuddy.Ai;

/// <summary>A friendly failure the UI can show as-is (no key, over budget, key rejected...).</summary>
internal sealed class AiUnavailableException(string message) : Exception(message);

/// <summary>
/// Talks to Claude with the key from Credential Manager. Guard rails:
///  - spending cap per month and per day (settings.json), tracked in ai-usage.json
///  - only text the code builds is sent; no window titles, clipboard or file contents
///  - the AI never runs anything; answers are shown to you as text
/// </summary>
internal sealed class AiClient(Settings settings)
{
    private static readonly string UsagePath = Path.Combine(Settings.Folder, "ai-usage.json");
    private static readonly object UsageGate = new();

    // Refusal fallbacks are supported on these models; others get a plain request.
    private static readonly string[] FallbackModels = ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"];

    public bool HasKey => CredentialStore.HasApiKey;

    public sealed class Usage
    {
        public string Month { get; set; } = "";
        public double SpentUsd { get; set; }
        public string Day { get; set; } = "";
        public int CallsToday { get; set; }
    }

    public static Usage CurrentUsage()
    {
        lock (UsageGate)
        {
            Usage u;
            try
            {
                u = File.Exists(UsagePath) ? JsonSerializer.Deserialize<Usage>(File.ReadAllText(UsagePath)) ?? new() : new();
            }
            catch
            {
                u = new();
            }
            string month = DateTime.Now.ToString("yyyy-MM"), day = DateTime.Now.ToString("yyyy-MM-dd");
            if (u.Month != month) { u.Month = month; u.SpentUsd = 0; }
            if (u.Day != day) { u.Day = day; u.CallsToday = 0; }
            return u;
        }
    }

    /// <summary>Sends a conversation (alternating user/assistant, starting with user) and returns the reply text.</summary>
    public async Task<string> Ask(string system, IReadOnlyList<(bool FromUser, string Text)> turns, CancellationToken cancel = default)
    {
        string key = CredentialStore.GetApiKey()
            ?? throw new AiUnavailableException("No API key yet. Tray menu > Set API key… to turn on AI answers.");

        Usage usage = CurrentUsage();
        if (usage.SpentUsd >= settings.AiMonthlyBudgetUsd)
            throw new AiUnavailableException($"This month's AI budget (${settings.AiMonthlyBudgetUsd:0.00}) is used up. Raise AiMonthlyBudgetUsd in settings if you want more.");
        if (usage.CallsToday >= settings.AiMaxCallsPerDay)
            throw new AiUnavailableException($"Daily limit of {settings.AiMaxCallsPerDay} AI questions reached. It resets tomorrow.");

        lock (UsageGate)
        {
            // Reserve this call now so two windows asking at once can't both slip under the cap.
            if (_inFlight >= 2) throw new AiUnavailableException("Still answering your last question. One moment.");
            _inFlight++;
        }
        try
        {
            return await AskCore(key, system, turns, cancel);
        }
        finally
        {
            lock (UsageGate) _inFlight--;
        }
    }

    private static int _inFlight;

    private async Task<string> AskCore(string key, string system, IReadOnlyList<(bool FromUser, string Text)> turns, CancellationToken cancel)
    {
        var client = new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(90), MaxRetries = 1 };
        var request = new MessageCreateParams
        {
            Model = settings.AiModel,
            MaxTokens = 2000,
            System = system,
            Messages = turns.Select(t => new BetaMessageParam
            {
                Role = t.FromUser ? Role.User : Role.Assistant,
                Content = t.Text,
            }).ToList(),
        };
        if (!settings.AiModel.StartsWith("claude-haiku", StringComparison.OrdinalIgnoreCase))
            request = request with { OutputConfig = new BetaOutputConfig { Effort = Effort.Low } }; // short chatty answers
        if (FallbackModels.Contains(settings.AiModel))
            request = request with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(request, cancel);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new AiUnavailableException("Your API key was rejected. Check it in tray menu > Set API key….");
        }
        catch (AnthropicRateLimitException)
        {
            throw new AiUnavailableException("The AI service is rate-limiting this key. Try again in a minute.");
        }
        catch (AnthropicApiException ex)
        {
            Log.Error("AI request failed", ex);
            throw new AiUnavailableException("The AI service returned an error. Try again in a bit (details are in the log).");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw; // the window was closed
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException or IOException)
        {
            // AnthropicIOException (network) and AnthropicInvalidDataException land here too.
            Log.Error("AI request failed (connection)", ex);
            throw new AiUnavailableException("Couldn't reach the AI service. Are you online?");
        }

        Record(response.Usage.InputTokens, response.Usage.OutputTokens);

        if (response.StopReason == "refusal")
            return "I can't help with that one. Try asking it a different way.";

        string text = string.Join("\n", response.Content
            .Select(b => b.TryPickText(out BetaTextBlock? t) ? t.Text : null)
            .Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
        if (text.Length == 0) return "(No answer came back. Try again.)";
        return response.StopReason == "max_tokens" ? text + "\n\n(Answer was cut short. Ask me to continue.)" : text;
    }

    private void Record(long inputTokens, long outputTokens)
    {
        var (inPrice, outPrice) = PricePerMillion(settings.AiModel);
        double cost = inputTokens / 1e6 * inPrice + outputTokens / 1e6 * outPrice;
        lock (UsageGate)
        {
            Usage u = CurrentUsage();
            u.SpentUsd += cost;
            u.CallsToday++;
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(UsagePath, JsonSerializer.Serialize(u));
            }
            catch (Exception ex)
            {
                Log.Error("Saving AI usage failed", ex);
            }
        }
        Log.Info($"AI call: {inputTokens} in / {outputTokens} out tokens, ~${cost:0.0000}");
    }

    /// <summary>USD per million input/output tokens (Anthropic list prices).</summary>
    private static (double In, double Out) PricePerMillion(string model) => model switch
    {
        _ when model.StartsWith("claude-haiku-4-5") => (1, 5),
        _ when model.StartsWith("claude-sonnet-5") => (2, 10),
        _ when model.StartsWith("claude-sonnet-4-6") => (3, 15),
        _ when model.StartsWith("claude-opus-5-5") => (4, 20),
        _ when model.StartsWith("claude-fable") => (10, 50),
        _ => (5, 25), // other Opus models; err on the high side
    };
}
