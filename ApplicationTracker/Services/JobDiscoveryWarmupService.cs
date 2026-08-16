namespace ApplicationTracker.Services;

public sealed class JobDiscoveryWarmupService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<JobDiscoveryWarmupService> logger;

    public JobDiscoveryWarmupService(
        IServiceScopeFactory scopeFactory,
        ILogger<JobDiscoveryWarmupService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider
                    .GetRequiredService<JobDiscoveryService>();

                await service.SearchAsync(
                    new JobDiscoverySearchRequest
                    {
                        UnitedStatesOnly = false,
                        Page = 1,
                        PageSize = 5
                    },
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "The job discovery cache could not be warmed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken);
        }
    }
}
