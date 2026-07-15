namespace PaymentProof.Api.Processing;

public sealed class PaymentDispatchWorker(
    PaymentDispatchProcessor processor,
    ILogger<PaymentDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Payment dispatch worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.ProcessOneDueAsync(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unhandled payment dispatch error");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        logger.LogInformation("Payment dispatch worker stopped");
    }
}
