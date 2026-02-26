using ImageMagick;

namespace Humans.Web.Helpers;

internal static class ProfilePictureProcessor
{
    private const int MaxProfilePictureLongSide = 1000;

    internal static (byte[] Data, string ContentType)? ResizeProfilePicture(
        byte[] imageData, ILogger? logger = null)
    {
        try
        {
            using var image = new MagickImage(imageData);
            image.AutoOrient();

            var longSide = Math.Max(image.Width, image.Height);
            if (longSide > MaxProfilePictureLongSide)
            {
                image.Resize(new MagickGeometry(MaxProfilePictureLongSide, MaxProfilePictureLongSide));
            }

            image.Format = MagickFormat.Jpeg;
            image.Quality = 85;
            return (image.ToByteArray(), "image/jpeg");
        }
        catch (MagickException ex)
        {
            logger?.LogWarning(ex, "Failed to process image ({Length} bytes)", imageData.Length);
            return null;
        }
    }
}
