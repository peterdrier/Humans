using System.Reflection;
using Humans.Web.Helpers;
using ImageMagick;
using Xunit;

namespace Humans.Integration.Tests;

public class ProfilePictureProcessorTests
{
    private static byte[] LoadTestHeic()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(
            "Humans.Integration.Tests.TestData.sample.heic")!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void ResizeProfilePicture_ProducesJpeg_ForHeicInput()
    {
        var heicData = LoadTestHeic();

        var result = ProfilePictureProcessor.ResizeProfilePicture(heicData);

        Assert.NotNull(result);
        Assert.Equal("image/jpeg", result.Value.ContentType);
        Assert.True(result.Value.Data.Length > 0);

        // Verify the output is a valid JPEG (starts with FF D8)
        Assert.Equal(0xFF, result.Value.Data[0]);
        Assert.Equal(0xD8, result.Value.Data[1]);
    }

    [Fact]
    public void ResizeProfilePicture_ResizesLargeHeic_ToMaxDimension()
    {
        var heicData = LoadTestHeic();

        var result = ProfilePictureProcessor.ResizeProfilePicture(heicData);

        Assert.NotNull(result);

        using var decoded = new MagickImage(result.Value.Data);
        var longSide = Math.Max(decoded.Width, decoded.Height);
        Assert.True(longSide <= 1000, $"Long side {longSide} exceeds 1000px limit");
    }

    [Fact]
    public void ResizeProfilePicture_ReturnsNull_ForCorruptData()
    {
        var corruptData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };

        var result = ProfilePictureProcessor.ResizeProfilePicture(corruptData);

        Assert.Null(result);
    }

    [Fact]
    public void ResizeProfilePicture_StillWorksForJpeg()
    {
        using var testImage = new MagickImage(MagickColors.Red, 100, 100);
        testImage.Format = MagickFormat.Jpeg;
        var jpegData = testImage.ToByteArray();

        var result = ProfilePictureProcessor.ResizeProfilePicture(jpegData);

        Assert.NotNull(result);
        Assert.Equal("image/jpeg", result.Value.ContentType);
        Assert.True(result.Value.Data.Length > 0);
    }
}
