using System.Net;
using FCG.Notifications.Tests.Projection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FCG.Notifications.Tests;

[Collection("notifications")]
public sealed class HttpLogPrivacyTests(NotificationFixture fixture)
{
    [Theory]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/missing", HttpStatusCode.NotFound)]
    public async Task Production_omits_sensitive_query_from_http_logs(string path, HttpStatusCode expected)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        var marker = "synthetic-query-" + Guid.NewGuid().ToString("N");

        using var response = await client.GetAsync(path + "?contact=" + marker);

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Contains(marker, StringComparison.Ordinal));
        // O provedor continua ativo para eventos seguros da aplicação.
        app.Services.GetRequiredService<ILogger<HttpLogPrivacyTests>>().LogInformation("privacy-control");
        Assert.Contains(app.Logs.Entries, entry => entry.Contains("privacy-control", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_omits_sensitive_missing_path_from_http_logs()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        var marker = "synthetic-path-" + Guid.NewGuid().ToString("N");

        using var response = await client.GetAsync("/missing/" + marker);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Contains(marker, StringComparison.Ordinal));
    }
}
