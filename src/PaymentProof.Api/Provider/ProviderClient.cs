using System.Net;
using System.Net.Http.Json;

namespace PaymentProof.Api.Provider;

public sealed class ProviderClient(HttpClient httpClient, ILogger<ProviderClient> logger)
    : IProviderClient
{
    public async Task<ProviderCallResult> CreatePaymentAsync(
        ProviderPaymentRequest payment,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "payments")
        {
            Content = JsonContent.Create(payment)
        };

        request.Headers.TryAddWithoutValidation("Idempotency-Key", payment.OperationId);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", payment.OperationId);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return ProviderCallResult.Retryable("Provider returned 503 Service Unavailable");
            }

            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var error = $"Provider returned {(int)response.StatusCode}: {Truncate(responseBody)}";
                return ProviderCallResult.Permanent(error);
            }

            ProviderPaymentResponse? providerResponse;
            try
            {
                providerResponse = await response.Content
                    .ReadFromJsonAsync<ProviderPaymentResponse>(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Provider returned an unreadable 202 response for {OperationId}",
                    payment.OperationId);
                return ProviderCallResult.Retryable("Provider returned an unreadable 202 response");
            }

            if (providerResponse is null
                || string.IsNullOrWhiteSpace(providerResponse.ProviderPaymentId))
            {
                return ProviderCallResult.Retryable(
                    "Provider returned 202 without providerPaymentId");
            }

            return ProviderCallResult.Accepted(providerResponse.ProviderPaymentId);
        }
        catch (HttpRequestException exception)
        {
            return ProviderCallResult.Retryable(Truncate(exception.Message));
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderCallResult.Retryable($"Provider request timed out: {Truncate(exception.Message)}");
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 900 ? value : value[..900];
}
