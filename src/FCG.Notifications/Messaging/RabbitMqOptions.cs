namespace FCG.Notifications.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; init; } = "";
    public ushort Port { get; init; } = 5672;
    public string VirtualHost { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    public void Validate()
    {
        if (Uri.CheckHostName(Host) == UriHostNameType.Unknown || Port == 0 || VirtualHost != "/fcg"
            || string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)
            || Username.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or '-')))
            throw new InvalidOperationException("RabbitMq requires a valid host, port, /fcg virtual host and explicit service credentials.");
    }
}
