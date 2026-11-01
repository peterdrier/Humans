using Microsoft.EntityFrameworkCore;

namespace Humans.Feedback.Data;

/// <summary>
/// Empty on purpose: it exists only to carry the migration that drops Feedback's tables. Delete the section once
/// production has applied that migration.
/// </summary>
internal sealed class FeedbackDbContext(DbContextOptions<FeedbackDbContext> options)
    : DbContext(options);
