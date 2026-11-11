using System.Diagnostics;
using Xunit;

namespace FCG.Notifications.Tests.Recovery;

public sealed class RecoveryCliArgumentTests
{
    [Fact]
    public async Task Invalid_arguments_are_rejected_without_starting_host_or_echoing_input()
    {
        var result = await Run(null, "recover-notifications", "PRIVATE-INVALID-ARGUMENT");
        Assert.Equal(2, result.ExitCode); Assert.Contains("Usage:", result.Error);
        Assert.DoesNotContain("PRIVATE-INVALID-ARGUMENT", result.Output + result.Error);
        Assert.DoesNotContain("   at ", result.Error);
    }

    [Theory]
    [InlineData("intents", "--limit", "0")]
    [InlineData("conflicts", "--limit", "101")]
    [InlineData("intent", "--id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("replay", "--id", "invalid")]
    public async Task Invalid_selection_is_rejected_before_configuration(string operation, string flag, string value)
    { Assert.Equal(2, (await Run(null, "recover-notifications", operation, flag, value)).ExitCode); }

    [Fact]
    public async Task Ambiguous_apply_and_unknown_verb_cannot_start_host()
    {
        Assert.Equal(2, (await Run(null, "recover-notifications", "resume", "--id", Guid.NewGuid().ToString(), "--dry-run", "--apply")).ExitCode);
        Assert.Equal(2, (await Run(null, "recover-notification", "conflicts")).ExitCode);
    }

    internal static Task<(int ExitCode, string Output, string Error)> Run(string? connection, params string[] args) => RunConfigured(connection, null, args);

    internal static async Task<(int ExitCode, string Output, string Error)> RunConfigured(string? connection, IReadOnlyDictionary<string, string?>? extra, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("Jwt__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("RabbitMq__", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        if (connection is not null) start.Environment["ConnectionStrings__Default"] = connection;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production"; start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Database__MigrateOnStartup"] = "true";
        start.Environment["RabbitMq__Host"] = "127.0.0.1"; start.Environment["RabbitMq__Port"] = "1";
        if (extra is not null) foreach (var pair in extra) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); return (process.ExitCode, await output, await error); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
