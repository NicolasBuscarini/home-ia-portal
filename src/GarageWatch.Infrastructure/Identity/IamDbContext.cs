using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Data.Sqlite;

namespace GarageWatch.Infrastructure.Identity;

public sealed class IamDbContext(DbContextOptions<IamDbContext> options, IPublicIdGenerator publicIds)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    internal bool DeferPublicIdCollisionRetries { get; set; }
    private static readonly ValueConverter<DateTimeOffset, long> UtcTicksConverter = new(
        value => value.UtcTicks,
        value => new DateTimeOffset(value, TimeSpan.Zero));
    private static readonly ValueConverter<DateTimeOffset?, long?> NullableUtcTicksConverter = new(
        value => value.HasValue ? value.Value.UtcTicks : null,
        value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);

    public DbSet<CameraAccessGrant> CameraAccessGrants => Set<CameraAccessGrant>();
    public DbSet<CameraContext> CameraContexts => Set<CameraContext>();
    public DbSet<CameraCatalogEntry> CameraCatalog => Set<CameraCatalogEntry>();
    public DbSet<PlatformEvent> PlatformEvents => Set<PlatformEvent>();
    public DbSet<AccessProfile> AccessProfiles => Set<AccessProfile>();
    public DbSet<AccessProfileVersion> AccessProfileVersions => Set<AccessProfileVersion>();
    public DbSet<AccessProfilePermission> AccessProfilePermissions => Set<AccessProfilePermission>();
    public DbSet<UserAccessProfileAssignment> UserAccessProfileAssignments => Set<UserAccessProfileAssignment>();
    public DbSet<UserAccessProfileScope> UserAccessProfileScopes => Set<UserAccessProfileScope>();
    public DbSet<IamMigrationState> IamMigrationStates => Set<IamMigrationState>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<ClipAnalysisDispatchPolicy> ClipAnalysisDispatchPolicies => Set<ClipAnalysisDispatchPolicy>();
    public DbSet<ClipAnalysisJob> ClipAnalysisJobs => Set<ClipAnalysisJob>();
    public DbSet<KnownPerson> KnownPeople => Set<KnownPerson>();
    public DbSet<BiometricEnrollment> BiometricEnrollments => Set<BiometricEnrollment>();
    public DbSet<BiometricPersonLink> BiometricPersonLinks => Set<BiometricPersonLink>();
    public DbSet<FaceExtractorCandidate> FaceExtractorCandidates => Set<FaceExtractorCandidate>();
    public DbSet<FaceExtractorSelection> FaceExtractorSelections => Set<FaceExtractorSelection>();
    public DbSet<ClipRetentionPolicy> ClipRetentionPolicies => Set<ClipRetentionPolicy>();
    public DbSet<ClipPreservation> ClipPreservations => Set<ClipPreservation>();
    public DbSet<PortalModule> PortalModules => Set<PortalModule>();
    public DbSet<PortalModuleMenuItem> PortalModuleMenuItems => Set<PortalModuleMenuItem>();
    public DbSet<PortalUserPreference> PortalUserPreferences => Set<PortalUserPreference>();
    public DbSet<IntegrationCredential> IntegrationCredentials => Set<IntegrationCredential>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAddedPublicIds();
        for (var attempt = 0; ; attempt++)
        {
            try { return base.SaveChanges(acceptAllChangesOnSuccess); }
            catch (DbUpdateException exception) when (!DeferPublicIdCollisionRetries && attempt < 4 && IsPublicIdCollision(exception))
            {
                RegenerateAddedPublicIds();
            }
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAddedPublicIds();
        for (var attempt = 0; ; attempt++)
        {
            try { return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
            catch (DbUpdateException exception) when (!DeferPublicIdCollisionRetries && attempt < 4 && IsPublicIdCollision(exception))
            {
                RegenerateAddedPublicIds();
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<CameraAccessGrant>(entity =>
        {
            entity.HasKey(grant => grant.Id);
            entity.Property(grant => grant.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(grant => grant.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(grant => grant.GrantedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(grant => grant.RevokedAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.HasIndex(grant => new { grant.UserId, grant.CameraId })
                .IsUnique()
                .HasFilter("\"RevokedAtUtc\" IS NULL");
            entity.HasOne(grant => grant.User)
                .WithMany()
                .HasForeignKey(grant => grant.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        ConfigurePublicId<ApplicationUser>(builder, PublicIdPrefixes.UserPrefix);

        builder.Entity<PlatformEvent>(entity =>
        {
            entity.ToTable("PlatformEvents");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.PublicId).HasMaxLength(16);
            entity.Property(audit => audit.SchemaVersion).HasMaxLength(16);
            entity.Property(audit => audit.EventType).HasMaxLength(64).IsRequired();
            entity.Property(audit => audit.Category).HasMaxLength(32);
            entity.Property(audit => audit.Severity).HasMaxLength(16);
            entity.Property(audit => audit.Outcome).HasMaxLength(16);
            entity.Property(audit => audit.Source).HasMaxLength(32);
            entity.Property(audit => audit.SourceVersion).HasMaxLength(64);
            entity.Property(audit => audit.Emitter).HasMaxLength(16);
            entity.Property(audit => audit.ResourceType).HasMaxLength(40);
            entity.Property(audit => audit.ResourceId).HasMaxLength(64);
            entity.Property(audit => audit.CorrelationId).HasMaxLength(128);
            entity.Property(audit => audit.CausationId).HasMaxLength(16);
            entity.Property(audit => audit.RequestId).HasMaxLength(128);
            entity.Property(audit => audit.SessionId).HasMaxLength(128);
            entity.Property(audit => audit.TraceId).HasMaxLength(32);
            entity.Property(audit => audit.SpanId).HasMaxLength(16);
            entity.Property(audit => audit.HttpMethod).HasMaxLength(8);
            entity.Property(audit => audit.HttpRoute).HasMaxLength(256);
            entity.Property(audit => audit.ClientUserAgent).HasMaxLength(512);
            entity.Property(audit => audit.ClientBrowser).HasMaxLength(64);
            entity.Property(audit => audit.ClientOs).HasMaxLength(64);
            entity.Property(audit => audit.ClientDevice).HasMaxLength(64);
            entity.Property(audit => audit.ClientIp).HasMaxLength(64);
            entity.Property(audit => audit.MetadataJson);
            entity.Property(audit => audit.PayloadHash).HasMaxLength(64);
            entity.Property(audit => audit.OccurredAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(audit => audit.RecordedAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.HasIndex(audit => new { audit.SubjectUserId, audit.OccurredAtUtc });
            entity.HasIndex(audit => new { audit.OccurredAtUtc, audit.Id });
            entity.HasIndex(audit => new { audit.ActorUserId, audit.OccurredAtUtc });
            entity.HasIndex(audit => new { audit.EventType, audit.OccurredAtUtc });
            entity.HasIndex(audit => audit.CorrelationId);
            entity.HasIndex(audit => new { audit.ResourceType, audit.ResourceId });
            entity.HasIndex(audit => new { audit.Outcome, audit.OccurredAtUtc });
            entity.HasIndex(audit => new { audit.Source, audit.EventId }).IsUnique();
            entity.HasIndex(audit => audit.PublicId).IsUnique();
            entity.HasOne(audit => audit.Actor)
                .WithMany()
                .HasForeignKey(audit => audit.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(audit => audit.Subject)
                .WithMany()
                .HasForeignKey(audit => audit.SubjectUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CameraContext>(entity =>
        {
            entity.HasKey(context => context.ContextId);
            entity.Property(context => context.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(context => context.Name).HasMaxLength(128).IsRequired();
            entity.Property(context => context.Version).IsConcurrencyToken();
            entity.Property(context => context.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(context => context.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(context => context.Name);
            ConfigurePublicId(entity, PublicIdPrefixes.ContextPrefix);
        });

        builder.Entity<CameraCatalogEntry>(entity =>
        {
            entity.HasKey(camera => camera.CameraId);
            entity.Property(camera => camera.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(camera => camera.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(camera => camera.Name).HasMaxLength(128).IsRequired();
            entity.Property(camera => camera.Version).IsConcurrencyToken();
            entity.Property(camera => camera.ClipTriggerEnabled).IsRequired();
            entity.Property(camera => camera.ClipTriggerVersion).IsConcurrencyToken();
            entity.Property(camera => camera.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(camera => camera.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(camera => new { camera.ContextId, camera.Name });
            ConfigurePublicId(entity, PublicIdPrefixes.CameraPrefix);
            entity.HasOne(camera => camera.Context)
                .WithMany(context => context.Cameras)
                .HasForeignKey(camera => camera.ContextId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AccessProfile>(entity =>
        {
            entity.HasKey(profile => profile.Id);
            entity.Property(profile => profile.Name).HasMaxLength(128).IsRequired();
            entity.Property(profile => profile.Version).IsConcurrencyToken();
            entity.HasIndex(profile => profile.Name).IsUnique();
            ConfigurePublicId(entity, PublicIdPrefixes.AccessProfilePrefix);
        });
        builder.Entity<AccessProfileVersion>(entity =>
        {
            entity.HasKey(version => version.Id);
            entity.HasIndex(version => new { version.ProfileId, version.Version }).IsUnique();
            ConfigurePublicId(entity, PublicIdPrefixes.AccessProfileVersionPrefix);
            entity.HasOne(version => version.Profile).WithMany(profile => profile.Versions)
                .HasForeignKey(version => version.ProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(version => version.Permissions).WithOne(permission => permission.Version)
                .HasForeignKey(permission => permission.AccessProfileVersionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AccessProfilePermission>(entity =>
        {
            entity.HasKey(permission => new { permission.AccessProfileVersionId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(128).IsRequired();
        });
        builder.Entity<UserAccessProfileAssignment>(entity =>
        {
            entity.HasKey(assignment => assignment.Id);
            entity.HasIndex(assignment => new { assignment.UserId, assignment.AccessProfileVersionId })
                .IsUnique().HasFilter("\"RevokedAtUtc\" IS NULL");
            entity.HasOne(assignment => assignment.User).WithMany()
                .HasForeignKey(assignment => assignment.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(assignment => assignment.ProfileVersion).WithMany()
                .HasForeignKey(assignment => assignment.AccessProfileVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(assignment => assignment.AssignedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(assignment => assignment.RevokedAtUtc).HasConversion(NullableUtcTicksConverter);
            ConfigurePublicId(entity, PublicIdPrefixes.ProfileAssignmentPrefix);
        });
        builder.Entity<UserAccessProfileScope>(entity =>
        {
            entity.HasKey(scope => new
            {
                scope.AssignmentId,
                scope.ScopeKind,
                scope.ModuleId,
                scope.ContextId,
                scope.CameraId
            });
            entity.Property(scope => scope.ScopeKind).HasMaxLength(16).IsRequired();
            entity.Property(scope => scope.ModuleId).HasMaxLength(64).IsRequired();
            entity.Property(scope => scope.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(scope => scope.CameraId).HasMaxLength(64).IsRequired();
            entity.HasOne(scope => scope.Assignment).WithMany(assignment => assignment.Scopes)
                .HasForeignKey(scope => scope.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<IamMigrationState>(entity =>
        {
            entity.HasKey(state => state.Key);
            entity.Property(state => state.Key).HasMaxLength(128);
            entity.Property(state => state.CompletedAtUtc).HasConversion(UtcTicksConverter);
        });
        builder.Entity<AccessRequest>(entity =>
        {
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Purpose).HasMaxLength(32).IsRequired();
            entity.Property(request => request.Status).HasMaxLength(24).IsRequired();
            entity.Property(request => request.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(request => new { request.UserId, request.IdempotencyKey }).IsUnique();
            entity.HasIndex(request => request.UserId).IsUnique().HasFilter("\"Status\" = 'pending'");
            entity.HasIndex(request => new { request.Status, request.CreatedAtUtc });
            entity.HasOne(request => request.User).WithMany().HasForeignKey(request => request.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigurePublicId(entity, PublicIdPrefixes.AccessRequestPrefix);
        });
        builder.Entity<ClipAnalysisDispatchPolicy>(entity =>
        {
            entity.HasKey(policy => policy.CameraId);
            entity.Property(policy => policy.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(policy => policy.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(policy => policy.Policy).HasMaxLength(32).IsRequired();
            entity.Property(policy => policy.UpdatedByUserId).HasMaxLength(64).IsRequired();
            entity.Property(policy => policy.Version).IsConcurrencyToken();
            entity.Property(policy => policy.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(policy => policy.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasOne<CameraCatalogEntry>().WithMany().HasForeignKey(policy => policy.CameraId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClipAnalysisJob>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.Property(job => job.ClipId).HasMaxLength(64).IsRequired();
            entity.Property(job => job.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(job => job.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(job => job.AnalysisVersion).HasMaxLength(64).IsRequired();
            entity.Property(job => job.State).HasMaxLength(32).IsRequired();
            entity.Property(job => job.RequestKind).HasMaxLength(32).IsRequired();
            entity.Property(job => job.LeaseExpiresAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.Property(job => job.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(job => job.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(job => new { job.ClipId, job.AnalysisVersion }).IsUnique();
            entity.HasIndex(job => new { job.ClipId, job.IdempotencyKey }).IsUnique();
            entity.HasIndex(job => new { job.State, job.CreatedAtUtc });
            ConfigurePublicId(entity, PublicIdPrefixes.ClipAnalysisJobPrefix);
        });
        builder.Entity<KnownPerson>(entity =>
        {
            entity.HasKey(person => person.Id);
            entity.Property(person => person.DisplayName).HasMaxLength(128);
            entity.Property(person => person.Purpose).HasMaxLength(256);
            entity.Property(person => person.AuthorizationRecordedAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.Property(person => person.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(person => person.DeletedAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.Property(person => person.Version).IsConcurrencyToken();
            entity.HasIndex(person => new { person.DeletedAtUtc, person.DisplayName });
            ConfigurePublicId(entity, PublicIdPrefixes.KnownPersonPrefix);
        });
        builder.Entity<BiometricEnrollment>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Status).HasMaxLength(16).IsRequired();
            entity.Property(item => item.RevokedReason).HasMaxLength(32);
            entity.Property(item => item.KeyVersion).HasMaxLength(32).IsRequired();
            entity.Property(item => item.ModelId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.ModelVersion).HasMaxLength(64).IsRequired();
            entity.Property(item => item.SchemaVersion).HasMaxLength(32).IsRequired();
            entity.Property(item => item.AdapterId).HasMaxLength(64).IsRequired();
            entity.Property(item => item.EnrolledAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(item => item.ExpiresAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(item => item.RevokedAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.Property(item => item.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(item => item.UserId).IsUnique().HasFilter("\"Status\" = 'active'");
            entity.HasIndex(item => new { item.Status, item.ExpiresAtUtc });
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            ConfigurePublicId(entity, PublicIdPrefixes.BiometricEnrollmentPrefix);
        });
        builder.Entity<BiometricPersonLink>(entity =>
        {
            entity.HasKey(link => link.Id);
            entity.Property(link => link.EnrollmentPublicId).HasMaxLength(32).IsRequired();
            entity.Property(link => link.KnownPersonPublicId).HasMaxLength(32).IsRequired();
            entity.Property(link => link.LinkedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(link => link.UnlinkedAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.HasIndex(link => link.EnrollmentPublicId).IsUnique()
                .HasFilter("\"UnlinkedAtUtc\" IS NULL");
            entity.HasIndex(link => new { link.KnownPersonPublicId, link.UnlinkedAtUtc });
        });
        builder.Entity<FaceExtractorCandidate>(entity =>
        {
            entity.HasKey(candidate => candidate.Id);
            entity.Property(candidate => candidate.ModelId).HasMaxLength(128).IsRequired();
            entity.Property(candidate => candidate.ModelVersion).HasMaxLength(64).IsRequired();
            entity.Property(candidate => candidate.ArtifactSha256).HasMaxLength(64).IsRequired();
            entity.Property(candidate => candidate.SourceUri).HasMaxLength(512).IsRequired();
            entity.Property(candidate => candidate.CodeLicense).HasMaxLength(128).IsRequired();
            entity.Property(candidate => candidate.WeightsLicense).HasMaxLength(128).IsRequired();
            entity.Property(candidate => candidate.AdapterId).HasMaxLength(64).IsRequired();
            entity.Property(candidate => candidate.AdapterContractVersion).HasMaxLength(64).IsRequired();
            entity.Property(candidate => candidate.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(candidate => new { candidate.ModelId, candidate.ModelVersion, candidate.ArtifactSha256 })
                .IsUnique();
            ConfigurePublicId(entity, PublicIdPrefixes.FaceExtractorPrefix);
        });
        builder.Entity<FaceExtractorSelection>(entity =>
        {
            entity.HasKey(selection => selection.Key);
            entity.Property(selection => selection.Key).HasMaxLength(64).IsRequired();
            entity.Property(selection => selection.Version).IsConcurrencyToken();
            entity.Property(selection => selection.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasOne<FaceExtractorCandidate>().WithMany().HasForeignKey(selection => selection.CandidateId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClipRetentionPolicy>(entity =>
        {
            entity.HasKey(policy => policy.CameraId);
            entity.Property(policy => policy.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(policy => policy.Version).IsConcurrencyToken();
            entity.Property(policy => policy.UpdatedByUserId).HasMaxLength(64).IsRequired();
            entity.Property(policy => policy.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(policy => policy.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasOne<CameraCatalogEntry>().WithMany().HasForeignKey(policy => policy.CameraId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClipPreservation>(entity =>
        {
            entity.HasKey(preservation => preservation.ClipId);
            entity.Property(preservation => preservation.ClipId).HasMaxLength(64).IsRequired();
            entity.Property(preservation => preservation.PublicId).HasMaxLength(15).IsRequired();
            entity.Property(preservation => preservation.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(preservation => preservation.Version).IsConcurrencyToken();
            entity.Property(preservation => preservation.UpdatedByUserId).HasMaxLength(64).IsRequired();
            entity.Property(preservation => preservation.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(preservation => preservation.PublicId).IsUnique();
            entity.HasIndex(preservation => new { preservation.CameraId, preservation.IsPreserved });
            entity.HasOne<CameraCatalogEntry>().WithMany().HasForeignKey(preservation => preservation.CameraId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PortalModule>(entity =>
        {
            entity.HasKey(module => module.ModuleId);
            entity.Property(module => module.ModuleId).HasMaxLength(64).IsRequired();
            entity.Property(module => module.DisplayNameKey).HasMaxLength(128).IsRequired();
            entity.Property(module => module.ContractVersion).HasMaxLength(32).IsRequired();
            entity.Property(module => module.ApiBaseUrl).HasMaxLength(512).IsRequired();
            entity.Property(module => module.HealthPath).HasMaxLength(256);
            entity.Property(module => module.RequiredPermission).HasMaxLength(128);
            entity.Property(module => module.MenuGroup).HasMaxLength(64).IsRequired();
            entity.Property(module => module.MenuIcon).HasMaxLength(64).IsRequired();
            entity.Property(module => module.MenuLabelKey).HasMaxLength(128).IsRequired();
            entity.Property(module => module.Version).IsConcurrencyToken();
            entity.Property(module => module.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(module => module.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(module => new { module.IsEnabled, module.MenuGroup, module.MenuOrder });
        });

        builder.Entity<PortalModuleMenuItem>(entity =>
        {
            entity.HasKey(item => new { item.ModuleId, item.ItemId });
            entity.Property(item => item.ModuleId).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ItemId).HasMaxLength(64).IsRequired();
            entity.Property(item => item.LabelKey).HasMaxLength(128).IsRequired();
            entity.Property(item => item.RequiredPermission).HasMaxLength(128);
            entity.HasOne(item => item.Module).WithMany(module => module.Items)
                .HasForeignKey(item => item.ModuleId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortalUserPreference>(entity =>
        {
            entity.HasKey(preference => preference.UserId);
            entity.Property(preference => preference.ThemeId).HasMaxLength(32).IsRequired();
            entity.Property(preference => preference.LanguageTag).HasMaxLength(16).IsRequired();
            entity.Property(preference => preference.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasOne(preference => preference.User).WithMany()
                .HasForeignKey(preference => preference.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<IntegrationCredential>(entity =>
        {
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.IntegrationId).HasMaxLength(64).IsRequired();
            entity.Property(credential => credential.KeyVersion).HasMaxLength(32).IsRequired();
            entity.Property(credential => credential.Nonce).IsRequired();
            entity.Property(credential => credential.Ciphertext).IsRequired();
            entity.Property(credential => credential.Tag).IsRequired();
            entity.Property(credential => credential.Version).IsConcurrencyToken();
            entity.Property(credential => credential.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(credential => credential.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(credential => credential.IntegrationId).IsUnique();
            entity.HasOne(credential => credential.UpdatedBy).WithMany()
                .HasForeignKey(credential => credential.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private void EnsureAddedPublicIds()
    {
        foreach (var entry in ChangeTracker.Entries<IHasPublicId>().Where(entry => entry.State == EntityState.Added))
            entry.Entity.PublicId ??= publicIds.Generate(PublicIdPrefixes.For(entry.Entity));
    }

    private void RegenerateAddedPublicIds()
    {
        foreach (var entry in ChangeTracker.Entries<IHasPublicId>().Where(entry => entry.State == EntityState.Added))
        {
            entry.Entity.PublicId = publicIds.Generate(PublicIdPrefixes.For(entry.Entity));
            entry.Property(nameof(IHasPublicId.PublicId)).IsModified = false;
        }
    }

    private static bool IsPublicIdCollision(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite &&
        sqlite.Message.Contains("PublicId", StringComparison.OrdinalIgnoreCase);

    private static void ConfigurePublicId<TEntity>(ModelBuilder builder, string prefix)
        where TEntity : class, IHasPublicId => ConfigurePublicId(builder.Entity<TEntity>(), prefix);

    private static void ConfigurePublicId<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        string prefix) where TEntity : class, IHasPublicId
    {
        entity.Property(item => item.PublicId).HasMaxLength(prefix.Length + 12);
        entity.HasIndex(item => item.PublicId).IsUnique().HasFilter("\"PublicId\" IS NOT NULL");
    }
}
