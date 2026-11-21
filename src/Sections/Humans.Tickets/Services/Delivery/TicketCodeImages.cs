using System.Security.Cryptography;
using Humans.Base.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using QRCoder;

namespace Humans.Tickets.Services.Delivery;

/// <summary>
/// The QR image of a ticket's barcode. Mail clients block <c>data:</c> images, so the ticket email links an image
/// URL; its token is protected so the anonymous endpoint renders only codes Humans issued, never arbitrary text.
/// </summary>
internal sealed class TicketCodeImages(IDataProtectionProvider dataProtection, IOptions<EmailSettings> settings)
{
    private const string ProtectorPurpose = "TicketCodeImage";
    internal const string RoutePrefix = "/Tickets/Mine/Code/";

    private readonly IDataProtector _protector = dataProtection.CreateProtector(ProtectorPurpose);

    public Uri ImageUrl(string barcode) =>
        new($"{settings.Value.BaseUrl.TrimEnd('/')}{RoutePrefix}{_protector.Protect(barcode)}");

    /// <summary>Null for a token Humans did not mint.</summary>
    public string? BarcodeFor(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        try
        {
            return _protector.Unprotect(token);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Quartile error correction survives a scuffed or glare-washed phone screen at the gate.</summary>
    public static byte[] Png(string barcode, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(barcode, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    public static string PngDataUri(string barcode) => "data:image/png;base64," + Convert.ToBase64String(Png(barcode));
}
