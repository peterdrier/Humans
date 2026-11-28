using AwesomeAssertions;
using Humans.Tickets.Services.Delivery;
using Humans.Tickets.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Humans.Tickets.Tests.Services.Delivery;

public sealed class TicketCodeImagesTests
{
    private const string Barcode = "H7K2M9QX4T8VB3NRP";

    [HumansFact]
    public void ImageUrl_Token_ResolvesToBarcode()
    {
        var images = TestTicketCodeImages.Create();

        var url = images.ImageUrl(Barcode);

        url.ToString().Should().StartWith($"{TestTicketsEmails.BaseUrl}{TicketCodeImages.RoutePrefix}");
        url.ToString().Should().NotContain(Barcode, "the URL must not be a generator for any text");
        images.BarcodeFor(url.Segments[^1]).Should().Be(Barcode);
    }

    [HumansTheory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData(Barcode)]
    public void BarcodeFor_RefusesTokensHumansDidNotMint(string token)
    {
        TestTicketCodeImages.Create().BarcodeFor(token).Should().BeNull();
    }

    [HumansFact]
    public void BarcodeFor_RefusesATokenFromAnotherKeyRing()
    {
        var token = TestTicketCodeImages.Create(new EphemeralDataProtectionProvider()).ImageUrl(Barcode).Segments[^1];

        TestTicketCodeImages.Create(new EphemeralDataProtectionProvider()).BarcodeFor(token).Should().BeNull();
    }

    [HumansFact]
    public void Png_IsAPngImage()
    {
        TicketCodeImages.Png(Barcode).Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        TicketCodeImages.PngDataUri(Barcode).Should().StartWith("data:image/png;base64,");
    }
}
