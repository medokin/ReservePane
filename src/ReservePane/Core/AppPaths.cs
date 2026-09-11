using System.IO;

namespace ReservePane.Core;

public sealed record AppPaths(
    string ClaudeCredentialsPath,
    string CodexAuthPath,
    string OpenCodeAuthPath,
    string GrokAuthPath,
    string SettingsPath,
    string LogPath)
{
    public string OllamaPrivateKeyPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "id_ed25519");

    public static AppPaths FromEnvironment()
    {
        string userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetEnvironmentVariable("APPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string applicationDirectory = Path.Combine(appData, "ReservePane");
        string? grokHome = Environment.GetEnvironmentVariable("GROK_HOME");
        string grokDirectory = string.IsNullOrWhiteSpace(grokHome)
            ? Path.Combine(userProfile, ".grok")
            : grokHome;

        return new AppPaths(
            Path.Combine(userProfile, ".claude", ".credentials.json"),
            Path.Combine(userProfile, ".codex", "auth.json"),
            Path.Combine(userProfile, ".local", "share", "opencode", "auth.json"),
            Path.Combine(grokDirectory, "auth.json"),
            Path.Combine(applicationDirectory, "settings.json"),
            Path.Combine(applicationDirectory, "log.txt"))
        {
            OllamaPrivateKeyPath = Path.Combine(userProfile, ".ollama", "id_ed25519"),
        };
    }
}
