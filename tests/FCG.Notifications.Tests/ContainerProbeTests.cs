using FCG.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Xunit;

public sealed class ContainerProbeTests
{
    [Theory]
    [InlineData(200, 0)]
    [InlineData(503, 1)]
    public async Task Probe_returns_exit_code_from_real_http_response(int status, int expected)
    {
        await using var server = await StartServer(status);
        Assert.Equal(expected, await ContainerProbe.RunAsync(Address(server)));
    }

    [Fact]
    public async Task Probe_returns_failure_when_server_is_unavailable()
    {
        var server = await StartServer(200);
        var url = Address(server);
        await server.DisposeAsync();
        Assert.Equal(1, await ContainerProbe.RunAsync(url));
    }

    [Fact]
    public async Task Probe_times_out_when_server_never_responds()
    {
        await using var server = await StartServer(200, stall: true);
        var result = await ContainerProbe.RunAsync(Address(server)).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, result);
    }

    private static async Task<WebApplication> StartServer(int status, bool stall = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build();
        server.MapGet("/health/ready", async context =>
        {
            if (stall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            context.Response.StatusCode = status;
            await context.Response.WriteAsync("probe response");
        });
        await server.StartAsync();
        return server;
    }

    private static Uri Address(WebApplication server) => new(
        server.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single() + "/health/ready");
}
