using AwesomeAssertions;
using Humans.Testing;
using Humans.Users.Helpers;
using ImageMagick;

namespace Humans.Users.Tests.Helpers;

public class ProfilePictureProcessorTests
{
    [HumansFact]
    public void ResizeProfilePicture_RemovesLocationMetadataAfterOrientingAndPreservesColorProfile()
    {
        using var upload = new MagickImage(MagickColors.Red, 1200, 600);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)OrientationType.RightTop);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(40), new Rational(25), new Rational(0)]);
        exif.SetValue(ExifTag.Artist, "Camera owner");
        upload.SetProfile(exif);
        upload.Orientation = OrientationType.RightTop;
        upload.SetProfile(ColorProfiles.SRGB);
        upload.Comment = "Private camera comment";
        upload.Format = MagickFormat.Jpeg;
        var bytes = upload.ToByteArray();
        using var original = new MagickImage(bytes);
        original.GetExifProfile().Should().NotBeNull();
        original.Orientation.Should().Be(OrientationType.RightTop);
        var colorProfile = original.GetColorProfile()!.ToByteArray();

        var result = ProfilePictureProcessor.ResizeProfilePicture(bytes);

        result.Should().NotBeNull();
        result!.Value.ContentType.Should().Be("image/jpeg");
        using var picture = new MagickImage(result.Value.Data);
        picture.Width.Should().Be(500);
        picture.Height.Should().Be(1000);
        picture.GetExifProfile().Should().BeNull();
        picture.Comment.Should().BeNullOrEmpty();
        picture.GetColorProfile()!.ToByteArray().Should().Equal(colorProfile);
    }
}
