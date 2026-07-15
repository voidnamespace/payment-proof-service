namespace PaymentProof.Api.Provider;

public interface IProviderClient
{
    Task<ProviderCallResult> CreatePaymentAsync(
        ProviderPaymentRequest payment,
        CancellationToken cancellationToken);
}
