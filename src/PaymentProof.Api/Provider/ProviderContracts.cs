using System.Text.Json.Serialization;

namespace PaymentProof.Api.Provider;

public sealed record ProviderPaymentRequest(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("currency")] string Currency);

public sealed record ProviderPaymentResponse(
    [property: JsonPropertyName("providerPaymentId")] string ProviderPaymentId,
    [property: JsonPropertyName("status")] string Status);

public enum ProviderCallOutcome
{
    Accepted,
    RetryableFailure,
    PermanentFailure
}

public sealed record ProviderCallResult(
    ProviderCallOutcome Outcome,
    string? ProviderPaymentId,
    string? Error)
{
    public static ProviderCallResult Accepted(string providerPaymentId) =>
        new(ProviderCallOutcome.Accepted, providerPaymentId, null);

    public static ProviderCallResult Retryable(string error) =>
        new(ProviderCallOutcome.RetryableFailure, null, error);

    public static ProviderCallResult Permanent(string error) =>
        new(ProviderCallOutcome.PermanentFailure, null, error);
}
