using FCG.Contracts;
using FCG.Notifications.Processing;
using MassTransit;

namespace FCG.Notifications.Messaging;

public static class NotificationsMessaging
{
    public static IServiceCollection AddNotificationsMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new(); settings.Validate();
        var queue = configuration["Queues:Identity"] ?? "fcg-notifications-identity";
        if (string.IsNullOrWhiteSpace(queue) || queue.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidOperationException("Queues:Identity requires a valid durable endpoint name.");
        var paymentQueue = configuration["Queues:Payments"] ?? "fcg-notifications-payments";
        if (string.IsNullOrWhiteSpace(paymentQueue) || paymentQueue == queue
            || paymentQueue.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidOperationException("Queues:Payments requires its own valid durable endpoint name.");
        services.AddSingleton(settings);
        services.AddScoped<PaymentNotificationHandler>();
        services.AddScoped<PendingContactReconciler>();
        services.AddScoped<IdentityProjectionHandler>();
        services.AddMassTransit(bus =>
        {
            bus.ConfigureHealthCheckOptions(options => { options.Tags.Clear(); options.Tags.Add("diagnostic"); });
            bus.AddConsumer<PaymentNotificationConsumer>();
            bus.AddConsumer<IdentityConsumer<UserCreatedEvent>>();
            bus.AddConsumer<IdentityConsumer<UserStateChangedEvent>>();
            bus.AddConsumer<IdentityConsumer<UserStateSnapshotEvent>>();
            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(settings.Host, settings.Port, settings.VirtualHost, host =>
                { host.Username(settings.Username); host.Password(settings.Password); host.PublisherConfirmation = true; });
                rabbit.ConfigureJsonSerializerOptions(options =>
                {
                    options.Converters.Insert(0, new PaymentProcessedEventConverter());
                    options.Converters.Insert(0, new IdentityEventConverter<UserCreatedEvent>());
                    options.Converters.Insert(0, new IdentityEventConverter<UserStateChangedEvent>());
                    options.Converters.Insert(0, new IdentityEventConverter<UserStateSnapshotEvent>());
                    return options;
                });
                rabbit.ReceiveEndpoint(paymentQueue, endpoint =>
                {
                    endpoint.UseMessageRetry(retry =>
                    {
                        retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                        retry.Ignore<ArgumentException>();
                    });
                    endpoint.ConfigureConsumer<PaymentNotificationConsumer>(context);
                });
                rabbit.ReceiveEndpoint(queue, endpoint =>
                {
                    endpoint.UseMessageRetry(retry =>
                    {
                        retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                        retry.Ignore<ArgumentException>();
                    });
                    endpoint.ConfigureConsumer<IdentityConsumer<UserCreatedEvent>>(context);
                    endpoint.ConfigureConsumer<IdentityConsumer<UserStateChangedEvent>>(context);
                    endpoint.ConfigureConsumer<IdentityConsumer<UserStateSnapshotEvent>>(context);
                });
            });
        });
        services.Configure<MassTransitHostOptions>(options => { options.WaitUntilStarted = false; options.StopTimeout = TimeSpan.FromSeconds(5); });
        services.AddHealthChecks().AddCheck<NotificationTransportHealthCheck>("notifications-transport", tags: ["ready"]);
        return services;
    }
}
