using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FCG.Notifications.Persistence;
using FCG.Notifications.Tests.Projection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static FCG.Notifications.Tests.Recovery.RecoveryCliArgumentTests;
using static FCG.Notifications.Tests.Recovery.NotificationRecoveryTests;
using static FCG.Notifications.Tests.Projection.ContactProjectionTests;

namespace FCG.Notifications.Tests.Recovery;

[Collection("notifications")]
public sealed class RecoveryCliDatabaseTests(NotificationFixture fixture)
{
    [Fact]
    public async Task Real_process_lists_previews_and_applies_compatible_restore_without_payload_or_contact_output()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        var id = await PaymentConflict(app); var before = await Fingerprint(app);
        foreach (var args in new[] { new[] { "conflicts" }, new[] { "conflict", "--id", id.ToString() }, new[] { "intents" } })
        {
            var result = await Run(app.ConnectionString, new[] { "recover-notifications" }.Concat(args).ToArray());
            Assert.Equal(0, result.ExitCode); Assert.Empty(result.Error);
            Assert.DoesNotContain("person@example.com", result.Output); Assert.DoesNotContain("Person", result.Output);
            foreach (var item in JsonDocument.Parse(result.Output).RootElement.GetProperty("items").EnumerateArray())
            { Assert.False(item.TryGetProperty("payload", out _)); Assert.False(item.TryGetProperty("recipientEmail", out _)); Assert.False(item.TryGetProperty("leaseToken", out _)); }
        }
        Assert.Equal(before, await Fingerprint(app));
        var pending = await Run(app.ConnectionString, "recover-notifications", "replay", "--id", id.ToString()); Assert.Equal(4, pending.ExitCode);
        Assert.True(JsonDocument.Parse(pending.Output).RootElement.GetProperty("dryRun").GetBoolean()); Assert.Equal(before, await Fingerprint(app));
        await RestoreBeforeObservation(app); before = await Fingerprint(app);
        var dry = await Run(app.ConnectionString, "recover-notifications", "replay", "--id", id.ToString(), "--dry-run"); Assert.Equal(0, dry.ExitCode);
        Assert.Equal("Apply", JsonDocument.Parse(dry.Output).RootElement.GetProperty("outcome").GetString()); Assert.Equal(before, await Fingerprint(app));
        foreach (var expected in new[] { "Apply", "AlreadyResolved" })
        {
            var result = await Run(app.ConnectionString, "recover-notifications", "replay", "--id", id.ToString(), "--apply"); Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected, JsonDocument.Parse(result.Output).RootElement.GetProperty("outcome").GetString());
            Assert.DoesNotContain(app.ConnectionString, result.Output + result.Error);
        }
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\" WHERE \"Kind\"='PurchaseConfirmation'"));
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"RecoveryAttempts\""));
    }

    [Fact]
    public async Task Real_process_resumes_backoff_without_emission_or_sent_and_inspects_separate_attempt_history()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        await Apply(app, WelcomeIntentTests.Created(Guid.NewGuid()));
        var id = Assert.IsType<Guid>(await app.ScalarAsync("SELECT \"Id\" FROM \"NotificationIntents\""));
        using (var scope = app.Services.CreateScope())
        { var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(); (await db.NotificationIntents.SingleAsync()).NextAttemptAtUtc = DateTimeOffset.UtcNow.AddHours(1); await db.SaveChangesAsync(); }
        var before = await Fingerprint(app);
        var dry = await Run(app.ConnectionString, "recover-notifications", "resume", "--id", id.ToString()); Assert.Equal(0, dry.ExitCode);
        Assert.Equal("Rescheduled", JsonDocument.Parse(dry.Output).RootElement.GetProperty("outcome").GetString()); Assert.Equal(before, await Fingerprint(app));
        var result = await Run(app.ConnectionString, "recover-notifications", "resume", "--id", id.ToString(), "--apply"); Assert.Equal(0, result.ExitCode);
        Assert.Equal("Ready", await app.ScalarAsync("SELECT \"State\" FROM \"NotificationIntents\""));
        Assert.Equal(DBNull.Value, await app.ScalarAsync("SELECT \"SentAtUtc\" FROM \"NotificationIntents\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationAttempts\""));
        var shown = await Run(app.ConnectionString, "recover-notifications", "intent", "--id", id.ToString()); Assert.Equal(0, shown.ExitCode);
        var metadata = JsonDocument.Parse(shown.Output).RootElement;
        Assert.Single(metadata.GetProperty("operatorAttempts").EnumerateArray()); Assert.Empty(metadata.GetProperty("deliveryAttempts").EnumerateArray());
        Assert.DoesNotContain("person@example.com", shown.Output + result.Output); Assert.DoesNotContain("[E-MAIL SIMULADO]", shown.Output + result.Output);
    }

    [Fact]
    public async Task Two_real_operator_processes_preserve_one_effect_and_two_attempts()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); var id = await PaymentConflict(app); await RestoreBeforeObservation(app);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Run(app.ConnectionString, "recover-notifications", "replay", "--id", id.ToString(), "--apply")));
        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal(new[] { "AlreadyResolved", "Apply" }, results.Select(x => JsonDocument.Parse(x.Output).RootElement.GetProperty("outcome").GetString()).Order());
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"NotificationIntents\""));
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"RecoveryAttempts\""));
    }

    [Fact]
    public async Task Cli_ignores_listener_dispatcher_broker_and_migration_flags_and_missing_selection_is_safe()
    {
        await using var app = await fixture.CreateAsync();
        // Database exists but the API has never started: migrate-on-startup must not affect local operations.
        var missingSchema = await Run(app.ConnectionString, "recover-notifications", "conflicts"); Assert.Equal(1, missingSchema.ExitCode);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema='public'"));
        using var client = app.CreateClient(); using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var extra = new Dictionary<string, string?> { ["ASPNETCORE_URLS"] = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, ["Dispatcher__LeaseSeconds"] = "0" };
        var result = await RunConfigured(app.ConnectionString, extra, "recover-notifications", "intents"); Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error); Assert.Empty(JsonDocument.Parse(result.Output).RootElement.GetProperty("items").EnumerateArray());
        foreach (var operation in new[] { "conflict", "intent", "replay", "resume" })
            Assert.Equal(3, (await Run(app.ConnectionString, "recover-notifications", operation, "--id", Guid.NewGuid().ToString())).ExitCode);
    }

    [Fact]
    public async Task Connection_failure_never_echoes_credentials_stack_or_private_arguments()
    {
        await using var app = await fixture.CreateAsync();
        var connection = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Host = "127.0.0.1", Port = 1, Password = "PRIVATE-OPERATOR-PASSWORD" }.ConnectionString;
        var failure = await Run(connection, "recover-notifications", "conflicts"); Assert.Equal(1, failure.ExitCode);
        Assert.DoesNotContain("PRIVATE-OPERATOR-PASSWORD", failure.Output + failure.Error); Assert.DoesNotContain("   at ", failure.Error);
    }
}
