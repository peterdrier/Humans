using Humans.Base.Interfaces;
using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal sealed class SurveyImageUploads(IFileStorage fileStorage, ILogger<SurveyService> logger)
{
    internal const int MaxInformationImages = 5;
    private const int MaxInformationImageFileNameLength = 256;
    private const long MaxInformationImageBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedInformationImageContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedInformationImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    internal async Task<PreparedInformationImages> PrepareAsync(
        Guid surveyId,
        SurveyEditInput input,
        Survey? existing,
        CancellationToken ct)
    {
        var existingImages = existing?.Questions
            .SelectMany(question => question.InformationImages ?? [])
            .ToDictionary(image => image.Id)
            ?? new Dictionary<Guid, SurveyInformationImage>();
        var newStoragePaths = new List<string>();
        var preparedQuestions = new List<QuestionInput>(input.Questions.Count);

        try
        {
            foreach (var question in input.Questions)
            {
                var questionId = question.Id ?? Guid.NewGuid();
                if (question.Type != SurveyQuestionType.Information)
                {
                    preparedQuestions.Add(question with
                    {
                        Id = questionId,
                        InformationImages = null,
                    });
                    continue;
                }

                var requestedImages = question.InformationImages ?? [];
                if (requestedImages.Count > MaxInformationImages)
                {
                    return await RejectAsync(new("Surveys_Validation_ImagesLimit",
                        $"An Information item can have at most {MaxInformationImages} images.", MaxInformationImages));
                }

                var preparedImages = new List<InformationImageInput>(requestedImages.Count);
                foreach (var requested in requestedImages)
                {
                    if (requested.Upload is { } upload)
                    {
                        var imageError = ValidateInformationImage(upload);
                        if (imageError is not null) return await RejectAsync(imageError);
                        var fileName = DisplayFileName(upload.FileName);
                        // Replacements get a fresh key so a failed database save cannot delete or
                        // overwrite the still-authoritative existing file.
                        var imageId = Guid.NewGuid();
                        var extension = Path.GetExtension(fileName);
                        var storagePath = $"uploads/surveys/{surveyId}/{questionId}/{imageId}{extension}";
                        await fileStorage.SaveAsync(storagePath, upload.Content, ct);
                        newStoragePaths.Add(storagePath);
                        preparedImages.Add(requested with
                        {
                            Id = imageId,
                            StoragePath = storagePath,
                            ContentType = upload.ContentType,
                            FileName = fileName,
                            Upload = null,
                        });
                        continue;
                    }

                    if (requested.Id is { } existingId
                        && existingImages.TryGetValue(existingId, out var persisted))
                    {
                        preparedImages.Add(requested with
                        {
                            StoragePath = persisted.StoragePath,
                            ContentType = persisted.ContentType,
                            FileName = persisted.FileName,
                            Upload = null,
                        });
                        continue;
                    }

                    return await RejectAsync(new("Surveys_Validation_ImageRequired",
                        "Select an image file for every image row. " +
                        "If a previous save failed, select the file again."));
                }

                preparedQuestions.Add(question with
                {
                    Id = questionId,
                    IsRequired = false,
                    RatingMin = null,
                    RatingMax = null,
                    RatingMinLabel = LocalizedText.Empty,
                    RatingMaxLabel = LocalizedText.Empty,
                    Options = [],
                    GridSelectionMode = null,
                    GridRows = null,
                    InformationImages = preparedImages,
                });
            }
        }
        catch
        {
            await DeleteBestEffortAsync(newStoragePaths, CancellationToken.None);
            throw;
        }

        return new PreparedInformationImages(
            input with { Questions = preparedQuestions },
            newStoragePaths);

        async Task<PreparedInformationImages> RejectAsync(SurveyAuthoringError error)
        {
            await DeleteBestEffortAsync(newStoragePaths, CancellationToken.None);
            return new(input, [], error);
        }
    }

    private static SurveyAuthoringError? ValidateInformationImage(SurveyImageUpload upload)
    {
        var fileName = DisplayFileName(upload.FileName);
        if (upload.Length <= 0)
        {
            return new("Surveys_Validation_ImageEmpty", "The selected image is empty.");
        }
        if (!AllowedInformationImageContentTypes.Contains(upload.ContentType))
        {
            return new("Surveys_Validation_ImageContentType", "Only JPEG, PNG, and WebP images are allowed.");
        }
        if (upload.Length > MaxInformationImageBytes)
        {
            return new("Surveys_Validation_ImageSize", "Each Information image must be under 10 MB.");
        }
        if (!AllowedInformationImageExtensions.Contains(Path.GetExtension(fileName)))
        {
            return new("Surveys_Validation_ImageExtension", "Image filenames must end in .jpg, .jpeg, .png, or .webp.");
        }
        if (fileName.Length > MaxInformationImageFileNameLength)
        {
            return new("Surveys_Validation_ImageFileNameLength",
                $"Image filename must be {MaxInformationImageFileNameLength} characters or fewer.", MaxInformationImageFileNameLength);
        }

        return null;
    }

    private static string DisplayFileName(string fileName) => fileName.Split('/', '\\').Last();

    internal async Task DeleteBestEffortAsync(
        IEnumerable<string> storagePaths,
        CancellationToken ct)
    {
        foreach (var storagePath in storagePaths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await fileStorage.DeleteAsync(storagePath, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete survey Information image {StoragePath}", storagePath);
            }
        }
    }

    internal sealed record PreparedInformationImages(
        SurveyEditInput Input,
        IReadOnlyList<string> NewStoragePaths,
        SurveyAuthoringError? Error = null);
}
