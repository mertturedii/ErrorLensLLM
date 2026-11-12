using Microsoft.Extensions.Options;

namespace ErrorLensLLM.Demo;

/// <summary>Sends a steady mix of requests to the modules, like students using the system.</summary>
public sealed class TrafficGenerator(IHttpClientFactory http, IOptions<DemoOptions> options, ILogger<TrafficGenerator> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1.0 / Math.Max(1, options.Value.Traffic.RequestsPerSecond));
        log.LogInformation("Trafik basladi: saniyede {Rps} istek", options.Value.Traffic.RequestsPerSecond);

        while (!stoppingToken.IsCancellationRequested)
        {
            _ = SendOneAsync(stoppingToken);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task SendOneAsync(CancellationToken ct)
    {
        // A few ids above the seeded range produce ordinary "not found" traffic
        var ogrenciId = Random.Shared.Next(1, DemoDatabase.StudentCount + 20);
        var roll = Random.Shared.NextDouble();

        try
        {
            using var response = roll switch
            {
                < 0.4 => await http.CreateClient("kayit").GetAsync($"ogrenciler/{ogrenciId}", ct),
                < 0.8 => await http.CreateClient("not").GetAsync($"notlar/{ogrenciId}", ct),
                _ => await http.CreateClient("odeme").PostAsync($"odemeler/{ogrenciId}", null, ct)
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A module being down is part of the scenario; the generator itself only keeps going
        }
    }
}
