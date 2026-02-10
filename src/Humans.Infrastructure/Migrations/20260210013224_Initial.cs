using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legal_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GitHubPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CurrentCommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Slug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SystemTeamType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PreferredLanguage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "en"),
                    ProfilePictureUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    PreferredEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PreferredEmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    PreferredEmailVerificationSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastConsentReminderSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DeletionRequestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DeletionScheduledFor = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "document_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LegalDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ContentSpanish = table.Column<string>(type: "text", nullable: false),
                    ContentEnglish = table.Column<string>(type: "text", nullable: false),
                    EffectiveFrom = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    RequiresReConsent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ChangesSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_versions_legal_documents_LegalDocumentId",
                        column: x => x.LegalDocumentId,
                        principalTable: "legal_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_claims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_role_claims_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Motivation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AdditionalInfo = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SubmittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ReviewStartedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_applications_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_applications_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "google_resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<string>(type: "text", nullable: false),
                    GoogleId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProvisionedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_google_resources_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_google_resources_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BurnerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LastName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PhoneCountryCode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    City = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    PlaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Bio = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DateOfBirth = table.Column<LocalDate>(type: "date", nullable: true),
                    ProfilePictureData = table.Column<byte[]>(type: "bytea", nullable: true),
                    ProfilePictureContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AdminNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsSuspended = table.Column<bool>(type: "boolean", nullable: false),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_profiles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ValidFrom = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_role_assignments_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_assignments_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_join_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_join_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_join_requests_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_join_requests_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_team_join_requests_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    JoinedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LeftAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_members_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_members_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_claims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_claims_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_logins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_user_logins_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_roles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_user_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsentedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExplicitConsent = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consent_records_document_versions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "document_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consent_records_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_state_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_state_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_application_state_history_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_application_state_history_users_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RelatedEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedEntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SyncSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserEmail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_log_google_resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "google_resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_audit_log_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "contact_fields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CustomLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Visibility = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_fields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contact_fields_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volunteer_history_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    EventName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_history_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volunteer_history_entries_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_join_request_state_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamJoinRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_join_request_state_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_join_request_state_history_team_join_requests_TeamJoin~",
                        column: x => x.TeamJoinRequestId,
                        principalTable: "team_join_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_join_request_state_history_users_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "CreatedAt", "Description", "IsActive", "Name", "Slug", "SystemTeamType", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0001-000000000001"), NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), "All active volunteers with signed required documents", true, "Volunteers", "volunteers", "Volunteers", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000002"), NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), "All team metaleads", true, "Metaleads", "metaleads", "Metaleads", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000003"), NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), "Board members with active role assignments", true, "Board", "board", "Board", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_state_history_ApplicationId",
                table: "application_state_history",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_application_state_history_ChangedAt",
                table: "application_state_history",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_application_state_history_ChangedByUserId",
                table: "application_state_history",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_ReviewedByUserId",
                table: "applications",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_Status",
                table: "applications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_applications_SubmittedAt",
                table: "applications",
                column: "SubmittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_applications_UserId",
                table: "applications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_UserId_Status",
                table: "applications",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_Action",
                table: "audit_log",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_ActorUserId",
                table: "audit_log",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_EntityType_EntityId",
                table: "audit_log",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_OccurredAt",
                table: "audit_log",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_RelatedEntityType_RelatedEntityId",
                table: "audit_log",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_ResourceId",
                table: "audit_log",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_ConsentedAt",
                table: "consent_records",
                column: "ConsentedAt");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_DocumentVersionId",
                table: "consent_records",
                column: "DocumentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_UserId",
                table: "consent_records",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_UserId_DocumentVersionId",
                table: "consent_records",
                columns: new[] { "UserId", "DocumentVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_UserId_ExplicitConsent_ConsentedAt",
                table: "consent_records",
                columns: new[] { "UserId", "ExplicitConsent", "ConsentedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_contact_fields_ProfileId",
                table: "contact_fields",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_contact_fields_ProfileId_Visibility",
                table: "contact_fields",
                columns: new[] { "ProfileId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_CommitSha",
                table: "document_versions",
                column: "CommitSha");

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_EffectiveFrom",
                table: "document_versions",
                column: "EffectiveFrom");

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_LegalDocumentId",
                table: "document_versions",
                column: "LegalDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_GoogleId",
                table: "google_resources",
                column: "GoogleId");

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_IsActive",
                table: "google_resources",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_TeamId",
                table: "google_resources",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_TeamId_GoogleId",
                table: "google_resources",
                columns: new[] { "TeamId", "GoogleId" },
                unique: true,
                filter: "\"IsActive\" = true AND \"TeamId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_UserId",
                table: "google_resources",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_legal_documents_IsActive",
                table: "legal_documents",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_legal_documents_Type",
                table: "legal_documents",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_UserId",
                table: "profiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_CreatedByUserId",
                table: "role_assignments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_RoleName",
                table: "role_assignments",
                column: "RoleName");

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_UserId",
                table: "role_assignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_UserId_RoleName",
                table: "role_assignments",
                columns: new[] { "UserId", "RoleName" },
                filter: "\"ValidTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_UserId_RoleName_ValidFrom",
                table: "role_assignments",
                columns: new[] { "UserId", "RoleName", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_role_claims_RoleId",
                table: "role_claims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "roles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_ChangedAt",
                table: "team_join_request_state_history",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_ChangedByUserId",
                table: "team_join_request_state_history",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_TeamJoinRequestId",
                table: "team_join_request_state_history",
                column: "TeamJoinRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_ReviewedByUserId",
                table: "team_join_requests",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_Status",
                table: "team_join_requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_TeamId",
                table: "team_join_requests",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_TeamId_UserId_Status",
                table: "team_join_requests",
                columns: new[] { "TeamId", "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_UserId",
                table: "team_join_requests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_active_unique",
                table: "team_members",
                columns: new[] { "TeamId", "UserId" },
                unique: true,
                filter: "\"LeftAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_Role",
                table: "team_members",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_UserId",
                table: "team_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_teams_IsActive",
                table: "teams",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_teams_Slug",
                table: "teams",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teams_SystemTeamType",
                table: "teams",
                column: "SystemTeamType");

            migrationBuilder.CreateIndex(
                name: "IX_user_claims_UserId",
                table: "user_claims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_logins_UserId",
                table: "user_logins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_RoleId",
                table: "user_roles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "users",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_users_PreferredEmail",
                table: "users",
                column: "PreferredEmail",
                unique: true,
                filter: "\"PreferredEmailVerified\" = true AND \"PreferredEmail\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "users",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_history_entries_ProfileId",
                table: "volunteer_history_entries",
                column: "ProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_state_history");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "consent_records");

            migrationBuilder.DropTable(
                name: "contact_fields");

            migrationBuilder.DropTable(
                name: "role_assignments");

            migrationBuilder.DropTable(
                name: "role_claims");

            migrationBuilder.DropTable(
                name: "team_join_request_state_history");

            migrationBuilder.DropTable(
                name: "team_members");

            migrationBuilder.DropTable(
                name: "user_claims");

            migrationBuilder.DropTable(
                name: "user_logins");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "user_tokens");

            migrationBuilder.DropTable(
                name: "volunteer_history_entries");

            migrationBuilder.DropTable(
                name: "applications");

            migrationBuilder.DropTable(
                name: "google_resources");

            migrationBuilder.DropTable(
                name: "document_versions");

            migrationBuilder.DropTable(
                name: "team_join_requests");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "profiles");

            migrationBuilder.DropTable(
                name: "legal_documents");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
