using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FCG.Notifications.Persistence;
using FCG.Notifications.Processing;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Operations;

internal static class RecoveryCommand
{
    private const string Usage = "Usage: recover-notifications conflicts|intents [--limit 1..100] | conflict|intent --id GUID | replay|resume --id GUID [--dry-run | --apply]";
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    { var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new JsonStringEnumConverter()); return json; }
    public static async Task<int> RunAsync(string[] args)
    {
        var options = Parse(args);
        if (options is null) { Console.Error.WriteLine(Usage); return 2; }
        try
        {
            var builder = Host.CreateApplicationBuilder(Array.Empty<string>()); builder.Logging.ClearProviders();
            var connection = builder.Configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connection)) { Console.Error.WriteLine("Recovery configuration unavailable."); return 1; }
            builder.Services.AddDbContext<NotificationsDbContext>(db => db.UseNpgsql(connection));
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddScoped<IdentityProjectionHandler>(); builder.Services.AddScoped<PaymentNotificationHandler>();
            builder.Services.AddScoped<NotificationRecoveryService>();
            using var host = builder.Build(); // Never Start: no HTTP, broker, dispatcher or migrations.
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
            if (options.Operation is "replay" or "resume")
            {
                var recovery = scope.ServiceProvider.GetRequiredService<NotificationRecoveryService>();
                var result = options.Operation == "resume" ? await recovery.ResumeIntentAsync(options.Id!.Value, options.Apply, ct)
                    : options.Apply ? await recovery.ReplayConflictAsync(options.Id!.Value, ct) : await recovery.PreviewConflictAsync(options.Id!.Value, ct);
                Write(new { operation = options.Operation, id = options.Id, dryRun = !options.Apply, result.Outcome, result.Reason, result.PlannedEffects });
                return result.ExitCode;
            }
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            if (options.Operation is "conflicts" or "conflict")
            {
                var query = db.MessageConflicts.AsNoTracking(); if (options.Id is { } id) query = query.Where(x => x.Id == id);
                var items = await query.OrderBy(x => x.Resolved).ThenBy(x => x.FirstSeenAtUtc).ThenBy(x => x.Id).Take(options.Limit)
                    .Select(x => new { x.Id, x.Kind, x.BusinessId, x.EventId, x.EventVersion, x.MessageType, x.TransportMessageId,
                        x.Reason, x.PayloadHash, x.Observations, x.Resolved, x.FirstSeenAtUtc, x.LastSeenAtUtc, x.ResolvedAtUtc }).ToArrayAsync(ct);
                var attempts = options.Id is { } target ? await db.RecoveryAttempts.AsNoTracking().Where(x => x.ConflictId == target)
                    .OrderByDescending(x => x.AttemptedAtUtc).ThenBy(x => x.Id).Take(100)
                    .Select(x => new { x.Id, x.AttemptedAtUtc, x.Operation, x.Outcome, x.Reason }).ToArrayAsync(ct) : [];
                Write(new { operation = options.Operation, dryRun = true, items, operatorAttempts = attempts });
                return options.Id.HasValue && items.Length == 0 ? 3 : 0;
            }
            else
            {
                var query = db.NotificationIntents.AsNoTracking(); if (options.Id is { } id) query = query.Where(x => x.Id == id);
                var items = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(options.Limit)
                    .Select(x => new { x.Id, x.Kind, x.BusinessId, x.UserId, x.GameId, x.Price, x.State, x.ContactEventVersion,
                        x.CreatedAtUtc, x.PreparedAtUtc, x.Attempts, x.LeaseVersion, x.LeaseExpiresAtUtc, x.NextAttemptAtUtc, x.SentAtUtc, x.LastFailureCode }).ToArrayAsync(ct);
                var attempts = options.Id is { } target ? await db.RecoveryAttempts.AsNoTracking().Where(x => x.IntentId == target)
                    .OrderByDescending(x => x.AttemptedAtUtc).ThenBy(x => x.Id).Take(100)
                    .Select(x => new { x.Id, x.AttemptedAtUtc, x.Operation, x.Outcome, x.Reason }).ToArrayAsync(ct) : [];
                var deliveries = options.Id is { } deliveryTarget ? await db.NotificationAttempts.AsNoTracking().Where(x => x.IntentId == deliveryTarget)
                    .OrderByDescending(x => x.ClaimedAtUtc).ThenBy(x => x.Id).Take(100)
                    .Select(x => new { x.Id, x.LeaseVersion, x.ClaimedAtUtc, x.ExpiresAtUtc, x.CompletedAtUtc, x.Result }).ToArrayAsync(ct) : [];
                Write(new { operation = options.Operation, dryRun = true, items, operatorAttempts = attempts, deliveryAttempts = deliveries });
                return options.Id.HasValue && items.Length == 0 ? 3 : 0;
            }
        }
        catch (Exception error) { Console.Error.WriteLine("Notification recovery failed: " + error.GetType().Name); return 1; }
    }
    private static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
    private sealed record Options(string Operation, Guid? Id, int Limit, bool Apply);
    private static Options? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("conflicts" or "conflict" or "intents" or "intent" or "replay" or "resume")) return null;
        var operation = args[0]; Guid? id = null; var limit = 20; var apply = false;
        var plural = operation is "conflicts" or "intents"; var action = operation is "replay" or "resume";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i]; if (!seen.Add(flag)) return null;
            if (flag is "--apply" or "--dry-run") { if (!action) return null; apply = flag == "--apply"; }
            else if (flag == "--id")
            { if (plural || ++i >= args.Length || !Guid.TryParse(args[i], out var parsed) || parsed == Guid.Empty) return null; id = parsed; }
            else if (flag == "--limit")
            { if (!plural || ++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100) return null; }
            else return null;
        }
        if (seen.Contains("--apply") && seen.Contains("--dry-run") || !plural && id is null) return null;
        return new(operation, id, plural ? limit : 1, apply);
    }
}
