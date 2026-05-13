using System;
using System.Collections.Generic;

namespace Dalamud.Plugin.Services
{
    public interface IPluginLog
    {
        void Debug(Exception exception, string messageTemplate, params object[] values);
    }
}

namespace ai02.Tests
{
    using Dalamud.Plugin.Services;

    public sealed class TestPluginLog : IPluginLog
    {
        public List<(Exception Exception, string MessageTemplate)> DebugEntries { get; } = new();

        public void Debug(Exception exception, string messageTemplate, params object[] values)
            => DebugEntries.Add((exception, messageTemplate));
    }
}

namespace ai02
{
    [Serializable]
    public sealed class Configuration
    {
        public LlmDecisionConfiguration? LlmDecision { get; set; } = new();
    }

    [Serializable]
    public sealed class LlmDecisionConfiguration
    {
        private const int PreviousDefaultMinIntervalSeconds = 10;
        private const int PreviousDefaultSameSituationCooldownSeconds = 15;
        private const int PreviousDefaultFreshDecisionSeconds = 55;
        private const int PreviousDefaultRoutinePulseIntervalSeconds = 20;
        private const int RelaxedDefaultMinIntervalSeconds = 8;
        private const int RelaxedDefaultSameSituationCooldownSeconds = 14;
        private const int LegacyHighDefaultMinIntervalSeconds = 18;
        private const int LegacyHighDefaultSameSituationCooldownSeconds = 36;

        public bool Enabled { get; set; } = true;
        public string ProviderBaseUrl { get; set; } = "https://api.deepseek.com";
        public string Model { get; set; } = "deepseek-v4-flash";
        public string ApiKeyEnvironmentVariable { get; set; } = "DEEPSEEK_API_KEY";
        public string ApiKey { get; set; } = string.Empty;
        public int RequestTimeoutMs { get; set; } = 4500;
        public int MinIntervalSeconds { get; set; } = 5;
        public int SameSituationCooldownSeconds { get; set; } = 8;
        public bool RoutinePulseEnabled { get; set; } = true;
        public int RoutinePulseIntervalSeconds { get; set; } = 25;
        public int FreshDecisionSeconds { get; set; } = 40;
        public int MaxContextTurns { get; set; } = 6;
        public bool IncludeDebugPayload { get; set; } = true;

        public void Normalize()
        {
            ProviderBaseUrl = string.IsNullOrWhiteSpace(ProviderBaseUrl)
                ? "https://api.deepseek.com"
                : ProviderBaseUrl.Trim().TrimEnd('/');
            Model = string.IsNullOrWhiteSpace(Model) ? "deepseek-v4-flash" : Model.Trim();
            ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
                ? "DEEPSEEK_API_KEY"
                : ApiKeyEnvironmentVariable.Trim();
            ApiKey = ApiKey?.Trim() ?? string.Empty;
            if (MinIntervalSeconds == PreviousDefaultMinIntervalSeconds
                || MinIntervalSeconds == RelaxedDefaultMinIntervalSeconds
                || MinIntervalSeconds == LegacyHighDefaultMinIntervalSeconds)
                MinIntervalSeconds = 5;
            if (SameSituationCooldownSeconds == PreviousDefaultSameSituationCooldownSeconds
                || SameSituationCooldownSeconds == RelaxedDefaultSameSituationCooldownSeconds
                || SameSituationCooldownSeconds == LegacyHighDefaultSameSituationCooldownSeconds)
                SameSituationCooldownSeconds = 8;
            if (RoutinePulseIntervalSeconds == PreviousDefaultRoutinePulseIntervalSeconds)
                RoutinePulseIntervalSeconds = 25;
            if (FreshDecisionSeconds == PreviousDefaultFreshDecisionSeconds)
                FreshDecisionSeconds = 40;
            RequestTimeoutMs = Math.Clamp(RequestTimeoutMs, 1500, 15000);
            MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 3, 120);
            SameSituationCooldownSeconds = Math.Clamp(SameSituationCooldownSeconds, 5, 180);
            RoutinePulseIntervalSeconds = Math.Clamp(RoutinePulseIntervalSeconds, 10, 180);
            FreshDecisionSeconds = Math.Clamp(FreshDecisionSeconds, 10, 180);
            MaxContextTurns = Math.Clamp(MaxContextTurns, 0, 12);
        }
    }
}
