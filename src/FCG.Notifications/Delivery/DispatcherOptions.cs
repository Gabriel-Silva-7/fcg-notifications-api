namespace FCG.Notifications.Delivery;

public sealed class DispatcherOptions
{
    public int PollSeconds { get; set; } = 1;
    public int LeaseSeconds { get; set; } = 30;
    public int BackoffSeconds { get; set; } = 5;
    public void Validate()
    {
        if (PollSeconds is < 1 or > 60 || LeaseSeconds is < 1 or > 3600 || BackoffSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Dispatcher intervals must be positive and within their supported bounds.");
    }
}
