using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public sealed class HealthTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Initial_host_reports_healthy(string path)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
    [Theory]
    [InlineData(HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded)]
    public async Task Ready_failure_returns_503_without_affecting_live(HealthStatus status)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddHealthChecks()
                .AddCheck("dependency", () => new HealthCheckResult(status), tags: ["ready"])));
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready");
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_ignores_checks_without_ready_tag()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddHealthChecks()
                .AddCheck("unrelated", () => HealthCheckResult.Unhealthy("test only"))));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
