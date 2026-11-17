using Microsoft.AspNetCore.Components;

namespace ErrorLensLLM.Dashboard.Components;

/// <summary>A screen that reloads its data on a timer for as long as it is open.</summary>
public abstract class RefreshingPage : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    [Inject] protected DashboardFormat Format { get; set; } = default!;
    [Inject] protected ModuleColors Colors { get; set; } = default!;

    protected CancellationToken Stopping => _stop.Token;

    /// <summary>When the data on screen was read; null until the first load.</summary>
    protected DateTimeOffset? Updated { get; private set; }

    /// <summary>Set while the last refresh failed; the screen keeps showing what it had.</summary>
    protected string? RefreshError { get; private set; }

    protected abstract Task LoadAsync();

    protected override void OnInitialized() => _ = RefreshLoopAsync();

    /// <summary>Runs on first render and again whenever a route parameter changes.</summary>
    protected override Task OnParametersSetAsync() => ReloadAsync();

    protected async Task ReloadAsync()
    {
        await Colors.RefreshAsync(Stopping);
        await LoadAsync();
        Updated = DateTimeOffset.UtcNow;
        RefreshError = null;
    }

    private async Task RefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(Format.Refresh);
        try
        {
            while (await timer.WaitForNextTickAsync(Stopping))
            {
                await InvokeAsync(async () =>
                {
                    try
                    {
                        await ReloadAsync();
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // The database can be briefly away; the next tick tries again
                        RefreshError = e.Message;
                    }

                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Page closed
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }
}
