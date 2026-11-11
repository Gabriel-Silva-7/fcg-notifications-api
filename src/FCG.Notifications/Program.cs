using FCG.Notifications;
using FCG.Notifications.Delivery;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using FCG.Notifications.Messaging;
using FCG.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

if (args.FirstOrDefault() == "recover-notifications")
{
    Environment.ExitCode = await FCG.Notifications.Operations.RecoveryCommand.RunAsync(args[1..]);
    return;
}
if (args.Length > 0 && !args[0].StartsWith('-'))
{
    Console.Error.WriteLine("Usage: recover-notifications conflicts|intents | conflict|intent|replay|resume --id GUID");
    Environment.ExitCode = 2;
    return;
}
if (args is ["--healthcheck"])
{
    Environment.ExitCode = await ContainerProbe.RunAsync(new Uri("http://127.0.0.1:8080/health/ready"));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:Default is required for Notifications.");
builder.Services.AddDbContext<NotificationsDbContext>(options =>
{
    options.UseNpgsql(connection);
    if (!builder.Environment.IsDevelopment())
        options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.CommandError, RelationalEventId.ConnectionError,
            CoreEventId.QueryIterationFailed, CoreEventId.SaveChangesFailed));
});
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddNotificationsMessaging(builder.Configuration);
builder.Services.AddScoped<FCG.Notifications.Processing.NotificationRecoveryService>();
var dispatcherOptions = builder.Configuration.GetSection("Dispatcher").Get<DispatcherOptions>() ?? new();
dispatcherOptions.Validate();
builder.Services.AddSingleton(dispatcherOptions);
builder.Services.AddSingleton<INotificationSink, ConsoleNotificationSink>();
builder.Services.AddScoped<NotificationDispatcher>();
builder.Services.AddHostedService<NotificationWorker>();
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddFilter("MassTransit", LogLevel.None);
    // O framework inclui caminho/query brutos nos logs HTTP; podem conter dados sensíveis.
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
}
builder.Services.AddHealthChecks().AddCheck<NotificationStorageHealthCheck>("notifications-storage", tags: ["ready"]);
var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
}
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});
app.Run();

public partial class Program;
