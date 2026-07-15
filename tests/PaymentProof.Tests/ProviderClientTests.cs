using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentProof.Api.Provider;

namespace PaymentProof.Tests;

public sealed class ProviderClientTests
{
    [Fact]
    public async Task CreatePaymentSendsRequiredHeadersAndBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new
            {
                providerPaymentId = "provider-payment-1",
                status = "ACCEPTED"
            })
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://provider/")
        };
        var client = new ProviderClient(httpClient, NullLogger<ProviderClient>.Instance);

        var result = await client.CreatePaymentAsync(
            new ProviderPaymentRequest("operation-1", "1000.00", "RUB"),
            CancellationToken.None);

        Assert.Equal(ProviderCallOutcome.Accepted, result.Outcome);
        Assert.Equal("provider-payment-1", result.ProviderPaymentId);
        Assert.Equal("operation-1", handler.IdempotencyKey);
        Assert.Equal("operation-1", handler.CorrelationId);
        Assert.Equal("/payments", handler.RequestUri!.AbsolutePath);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("operation-1", body.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("1000.00", body.RootElement.GetProperty("amount").GetString());
        Assert.Equal("RUB", body.RootElement.GetProperty("currency").GetString());
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task ServiceUnavailableIsRetryable()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new ProviderClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://provider/") },
            NullLogger<ProviderClient>.Instance);

        var result = await client.CreatePaymentAsync(
            new ProviderPaymentRequest("operation-2", "1.00", "RUB"),
            CancellationToken.None);

        Assert.Equal(ProviderCallOutcome.RetryableFailure, result.Outcome);
    }

    [Fact]
    public async Task BrokenAcceptedResponseIsRetryableBecauseResultIsAmbiguous()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("not-json")
        });
        var client = new ProviderClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://provider/") },
            NullLogger<ProviderClient>.Instance);

        var result = await client.CreatePaymentAsync(
            new ProviderPaymentRequest("operation-3", "1.00", "RUB"),
            CancellationToken.None);

        Assert.Equal(ProviderCallOutcome.RetryableFailure, result.Outcome);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string? IdempotencyKey { get; private set; }
        public string? CorrelationId { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            CorrelationId = request.Headers.GetValues("X-Correlation-ID").Single();
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
