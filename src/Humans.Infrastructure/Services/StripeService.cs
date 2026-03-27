using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Humans.Infrastructure.Services;

public class StripeSettings
{
    /// <summary>Stripe restricted API key — populated from STRIPE_API_KEY env var.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);
}

public class StripeService : IStripeService
{
    private readonly StripeSettings _settings;
    private readonly ILogger<StripeService> _logger;

    public StripeService(IOptions<StripeSettings> settings, ILogger<StripeService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured => _settings.IsConfigured;

    public async Task<StripePaymentDetails?> GetPaymentDetailsAsync(
        string paymentIntentId, CancellationToken ct = default)
    {
        var client = new StripeClient(_settings.ApiKey);
        var piService = new PaymentIntentService(client);

        var pi = await piService.GetAsync(paymentIntentId, new PaymentIntentGetOptions
        {
            Expand = ["latest_charge.balance_transaction"]
        }, cancellationToken: ct);

        var charge = pi.LatestCharge;
        if (charge is null)
        {
            _logger.LogDebug("PaymentIntent {Id} has no charge", paymentIntentId);
            return null;
        }

        // Payment method type and detail
        var pmd = charge.PaymentMethodDetails;
        var methodType = pmd?.Type ?? "unknown";
        string? methodDetail = null;
        if (string.Equals(methodType, "card", StringComparison.Ordinal) && pmd?.Card is not null)
            methodDetail = pmd.Card.Brand;

        // Fee breakdown from BalanceTransaction
        decimal stripeFee = 0;
        decimal applicationFee = 0;
        var bt = charge.BalanceTransaction;
        if (bt?.FeeDetails is not null)
        {
            foreach (var fd in bt.FeeDetails)
            {
                if (string.Equals(fd.Type, "stripe_fee", StringComparison.Ordinal))
                    stripeFee += fd.Amount / 100m;
                else if (string.Equals(fd.Type, "application_fee", StringComparison.Ordinal))
                    applicationFee += fd.Amount / 100m;
            }
        }

        return new StripePaymentDetails(
            PaymentMethod: methodType,
            PaymentMethodDetail: methodDetail,
            StripeFee: stripeFee,
            ApplicationFee: applicationFee);
    }
}
