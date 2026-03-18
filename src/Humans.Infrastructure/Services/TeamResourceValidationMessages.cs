namespace Humans.Infrastructure.Services;

internal static class TeamResourceValidationMessages
{
    public const string InvalidDriveUrl =
        "Invalid Google Drive URL. Please use a folder URL (https://drive.google.com/drive/folders/...) or a file URL (https://docs.google.com/spreadsheets/d/...).";

    public const string InvalidGroupEmail = "Please enter a valid group email address.";
}
