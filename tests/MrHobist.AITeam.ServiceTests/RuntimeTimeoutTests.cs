using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Infrastructure.Runtime;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// 2026-09-23: 12 dk'lik HttpClient zaman asimi Opus turunu kesti; TaskCanceledException is kanalinda "kullanici iptali"
/// sanildi, calisma sessizce Running'de asili kaldi. Zaman asimi gorunur hataya donmeli, gercek iptal iptal kalmali.
/// </summary>
public sealed class RuntimeTimeoutTests
{
    /// <summary>Cevabi hic gelmeyen runtime: istek iptal edilene kadar bekler.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("ulasilmaz");
        }
    }

    private static PythonAgentRuntimeClient Client(TimeSpan timeout)
        => new(new HttpClient(new HangingHandler()) { BaseAddress = new Uri("http://127.0.0.1:5090"), Timeout = timeout });

    private static RuntimeTurnRequest Request()
        => new("sistem", [new RuntimeMessage("user", "merhaba")], Provider.Anthropic, "claude-opus-5-5", null);

    [Fact]
    public async Task Zaman_asimi_iptal_degil_gorunur_runtime_hatasidir()
    {
        var ex = await Assert.ThrowsAsync<RuntimeErrorException>(() => Client(TimeSpan.FromMilliseconds(100)).TurnAsync(Request(), CancellationToken.None));
        Assert.Contains("zaman asimi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cagiranin_iptali_iptal_olarak_kalir()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(TimeSpan.FromMinutes(5)).TurnAsync(Request(), cts.Token));
    }
}
