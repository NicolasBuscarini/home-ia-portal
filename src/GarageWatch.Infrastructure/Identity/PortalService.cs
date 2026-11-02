using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Portal;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed class PortalService(
    IamDbContext database,
    IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents,
    UserManager<ApplicationUser> users,
    AccessProfileService profiles,
    IPermissionEvaluator evaluator,
    PortalOptions options,
    TimeProvider timeProvider)
    : IPortalModuleCatalog, IPortalNavigation, IPortalPreferences, IPortalAuthorizationDecisions
{
    public async Task<PortalModulePage> ListAsync(int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize);
        var query = database.PortalModules.AsNoTracking().OrderBy(module => module.ModuleId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        return new(items.Select(Map).ToArray(), page, pageSize, total);
    }

    public Task<PortalModuleRecord> CreateAsync(Guid actorUserId, PortalModuleUpsert request,
        CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        var normalized = Normalize(request);
        if (await database.PortalModules.AnyAsync(module => module.ModuleId == normalized.ModuleId, token))
            throw new PortalConflictException("Já existe um módulo com esse identificador.");
        var now = timeProvider.GetUtcNow();
        var entity = new PortalModule
        {
            ModuleId = normalized.ModuleId,
            DisplayNameKey = normalized.DisplayNameKey,
            ContractVersion = normalized.ContractVersion,
            ApiBaseUrl = normalized.ApiBaseUrl,
            HealthPath = normalized.HealthPath,
            RequiredPermission = normalized.RequiredPermission,
            MenuGroup = normalized.MenuGroup,
            MenuOrder = normalized.MenuOrder,
            MenuIcon = normalized.MenuIcon,
            MenuLabelKey = normalized.MenuLabelKey,
            IsEnabled = normalized.IsEnabled,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.PortalModules.Add(entity);
        auditEvents.Append(actorUserId, null, "portal_module.created", entity.ModuleId, now);
        return Map(entity);
    }, cancellationToken);

    public Task<PortalModuleRecord> UpdateAsync(Guid actorUserId, string moduleId, long expectedVersion,
        PortalModuleUpsert request, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await EnsureAdministratorAsync(actorUserId);
            if (expectedVersion < 1) throw new PortalValidationException("Versão inválida.");
            var normalized = Normalize(request, moduleId);
            var entity = await database.PortalModules.SingleOrDefaultAsync(module => module.ModuleId == moduleId, token)
                ?? throw new PortalNotFoundException("Módulo não encontrado.");
            if (entity.Version != expectedVersion)
                throw new PortalConflictException("O módulo mudou; atualize a consulta e tente novamente.");

            var now = timeProvider.GetUtcNow();
            entity.DisplayNameKey = normalized.DisplayNameKey;
            entity.ContractVersion = normalized.ContractVersion;
            entity.ApiBaseUrl = normalized.ApiBaseUrl;
            entity.HealthPath = normalized.HealthPath;
            entity.RequiredPermission = normalized.RequiredPermission;
            entity.MenuGroup = normalized.MenuGroup;
            entity.MenuOrder = normalized.MenuOrder;
            entity.MenuIcon = normalized.MenuIcon;
            entity.MenuLabelKey = normalized.MenuLabelKey;
            entity.IsEnabled = normalized.IsEnabled;
            entity.Version++;
            entity.UpdatedAtUtc = now;
            auditEvents.Append(actorUserId, null,
                normalized.IsEnabled ? "portal_module.updated" : "portal_module.disabled", entity.ModuleId, now);
            return Map(entity);
        }, cancellationToken);

    public async Task<PortalNavigation> GetNavigationAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var administrator = await IsAdministratorAsync(userId);
        var modules = await database.PortalModules.AsNoTracking()
            .Where(module => module.IsEnabled)
            .OrderBy(module => module.MenuGroup).ThenBy(module => module.MenuOrder).ThenBy(module => module.ModuleId)
            .ToArrayAsync(cancellationToken);
        var visible = new List<PortalNavigationModule>();
        foreach (var module in modules)
        {
            if (!administrator && module.RequiredPermission is not null &&
                !await profiles.IsAllowedAnyCameraAsync(userId, module.RequiredPermission, cancellationToken))
                continue;
            visible.Add(new PortalNavigationModule(module.ModuleId, module.DisplayNameKey, module.ContractVersion,
                module.MenuGroup, module.MenuOrder, module.MenuIcon, module.MenuLabelKey,
                $"/modules/{module.ModuleId}"));
        }
        return new PortalNavigation(options.ResidenceId, timeProvider.GetUtcNow(), visible);
    }

    public async Task<PortalPreferences> GetPreferencesAsync(Guid userId,
        CancellationToken cancellationToken = default)
    {
        var preference = await database.PortalUserPreferences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (preference is null || !PortalPreferenceCatalog.IsSupportedTheme(preference.ThemeId) ||
            !PortalPreferenceCatalog.IsSupportedLanguage(preference.LanguageTag))
            return DefaultPreferences();
        return new PortalPreferences(preference.ThemeId, preference.LanguageTag, false, preference.UpdatedAtUtc,
            PortalPreferenceCatalog.Themes, PortalPreferenceCatalog.Languages);
    }

    public Task<PortalPreferences> UpdatePreferencesAsync(Guid userId, string themeId, string languageTag,
        CancellationToken cancellationToken = default)
    {
        var theme = ValidateTheme(themeId);
        var language = ValidateLanguage(languageTag);
        return unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var user = await users.FindByIdAsync(userId.ToString());
            if (user is null || !user.IsActive) throw new UnauthorizedAccessException("Conta inativa ou inexistente.");
            var now = timeProvider.GetUtcNow();
            var preference = await database.PortalUserPreferences
                .SingleOrDefaultAsync(item => item.UserId == userId, token);
            if (preference is null)
            {
                preference = new PortalUserPreference
                {
                    UserId = userId,
                    ThemeId = theme,
                    LanguageTag = language,
                    UpdatedAtUtc = now
                };
                database.PortalUserPreferences.Add(preference);
            }
            else
            {
                preference.ThemeId = theme;
                preference.LanguageTag = language;
                preference.UpdatedAtUtc = now;
            }
            auditEvents.Append(userId, userId, "portal.preferences.updated", null, now);
            return new PortalPreferences(theme, language, false, now, PortalPreferenceCatalog.Themes,
                PortalPreferenceCatalog.Languages);
        }, cancellationToken);
    }

    public async Task<PortalAuthorizationDecision> DecideAsync(Guid userId, PortalAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audience = request.Audience?.Trim();
        if (string.IsNullOrWhiteSpace(audience) ||
            !await database.PortalModules.AsNoTracking().AnyAsync(module => module.ModuleId == audience && module.IsEnabled,
                cancellationToken))
            throw new PortalValidationException("Módulo de audiência não encontrado ou desabilitado.");
        if (!string.Equals(request.ResidenceId, options.ResidenceId, StringComparison.Ordinal))
            throw new PortalValidationException("Residência não corresponde à instalação.");
        var requestedPermissions = ValidatePermissionKeys(request.PermissionKeys);
        var requestedScopes = ValidateScopes(request.Scopes);
        var resolvedScopes = await ResolveScopesAsync(requestedScopes, cancellationToken);
        var subject = await profiles.GetSubjectAsync(userId, cancellationToken);
        if (!subject.IsActive) throw new UnauthorizedAccessException("Conta inativa ou inexistente.");
        var decisions = new List<PortalPermissionDecision>(requestedPermissions.Length);
        foreach (var key in requestedPermissions)
        {
            var allowedScopes = new List<PortalAuthorizationScope>();
            foreach (var scope in requestedScopes)
            {
                if (!resolvedScopes.TryGetValue((scope.ContextId, scope.CameraId), out var internalScope)) continue;
                if (evaluator.IsAllowed(subject,
                        new PermissionRequest(key, internalScope.ContextId, internalScope.CameraId)))
                    allowedScopes.Add(scope);
            }
            var definition = PermissionCatalog.TryGet(key, out var found) ? found : null;
            var adminOnlyWithoutScope = definition is { IsDelegable: false } &&
                subject.Roles.Contains("Administrator", StringComparer.Ordinal) && requestedScopes.Length == 0;
            var allowed = allowedScopes.Count > 0 || adminOnlyWithoutScope;
            decisions.Add(new PortalPermissionDecision(key, allowed, allowedScopes));
        }
        return new PortalAuthorizationDecision("1.0", options.ResidenceId, audience,
            decisions.All(decision => decision.Allowed), decisions, timeProvider.GetUtcNow());
    }

    private async Task<Dictionary<(string ContextId, string CameraId), PermissionScope>> ResolveScopesAsync(
        PortalAuthorizationScope[] scopes, CancellationToken cancellationToken)
    {
        if (scopes.Length == 0) return new Dictionary<(string ContextId, string CameraId), PermissionScope>();
        var contextIds = scopes.Select(scope => scope.ContextId).Distinct(StringComparer.Ordinal).ToArray();
        var cameraIds = scopes.Select(scope => scope.CameraId).Distinct(StringComparer.Ordinal).ToArray();
        var cameras = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.IsEnabled && camera.Context.IsEnabled &&
                contextIds.Contains(camera.Context.PublicId!) && cameraIds.Contains(camera.PublicId!))
            .Select(camera => new
            {
                ContextPublicId = camera.Context.PublicId!,
                CameraPublicId = camera.PublicId!,
                camera.ContextId,
                InternalCameraId = camera.CameraId
            }).ToArrayAsync(cancellationToken);
        return cameras.ToDictionary(
            camera => (camera.ContextPublicId, camera.CameraPublicId),
            camera => new PermissionScope(camera.ContextId, camera.InternalCameraId));
    }

    private static PortalPreferences DefaultPreferences() => new(PortalPreferenceCatalog.DefaultThemeId,
        PortalPreferenceCatalog.DefaultLanguageTag, true, null, PortalPreferenceCatalog.Themes,
        PortalPreferenceCatalog.Languages);

    private static string ValidateTheme(string? value)
    {
        var theme = value?.Trim();
        if (!PortalPreferenceCatalog.IsSupportedTheme(theme))
            throw new PortalValidationException("Tema não suportado.");
        return theme!;
    }

    private static string ValidateLanguage(string? value)
    {
        var language = value?.Trim();
        if (!PortalPreferenceCatalog.IsSupportedLanguage(language))
            throw new PortalValidationException("Idioma não suportado.");
        return language!;
    }

    private static string[] ValidatePermissionKeys(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count is < 1 or > 100)
            throw new PortalValidationException("Lista de permissões inválida.");
        var distinct = keys.Select(key => key?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length != keys.Count || distinct.Any(key => !PermissionCatalog.TryGet(key, out _)))
            throw new PortalValidationException("Permissão duplicada ou desconhecida.");
        return distinct;
    }

    private static PortalAuthorizationScope[] ValidateScopes(IReadOnlyList<PortalAuthorizationScope>? scopes)
    {
        if (scopes is null) return [];
        if (scopes.Count > 100) throw new PortalValidationException("Escopos demais na decisão.");
        var normalized = scopes.Select(scope => new PortalAuthorizationScope(scope.ContextId?.Trim() ?? string.Empty,
            scope.CameraId?.Trim() ?? string.Empty)).ToArray();
        if (normalized.Any(scope => !IsSafeIdentifier(scope.ContextId) || !IsSafeIdentifier(scope.CameraId)) ||
            normalized.Distinct().Count() != normalized.Length)
            throw new PortalValidationException("Escopo inválido ou duplicado.");
        return normalized;
    }

    private static PortalModuleUpsert Normalize(PortalModuleUpsert request, string? moduleIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var moduleId = (moduleIdOverride ?? request.ModuleId)?.Trim();
        if (moduleIdOverride is not null && !string.Equals(request.ModuleId?.Trim(), moduleId,
                StringComparison.Ordinal))
            throw new PortalValidationException("O identificador do módulo não corresponde ao recurso.");
        ValidateModuleId(moduleId);
        var displayNameKey = ValidateText(request.DisplayNameKey, "displayNameKey", 128);
        var contractVersion = ValidateText(request.ContractVersion, "contractVersion", 32);
        if (!Version.TryParse(contractVersion, out _))
            throw new PortalValidationException("contractVersion inválida.");
        var apiBaseUrl = ValidateApiBaseUrl(request.ApiBaseUrl);
        var healthPath = string.IsNullOrWhiteSpace(request.HealthPath) ? null : ValidatePath(request.HealthPath);
        var requiredPermission = string.IsNullOrWhiteSpace(request.RequiredPermission)
            ? null
            : request.RequiredPermission.Trim();
        if (requiredPermission is not null && !PermissionCatalog.TryGet(requiredPermission, out _))
            throw new PortalValidationException("Permissão exigida desconhecida.");
        var menuGroup = ValidateText(request.MenuGroup, "menuGroup", 64);
        if (menuGroup.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) &&
                                      character is not '-'))
            throw new PortalValidationException("menuGroup inválido.");
        if (request.MenuOrder is < 0 or > 10_000)
            throw new PortalValidationException("menuOrder inválido.");
        var menuIcon = ValidateText(request.MenuIcon, "menuIcon", 64);
        var menuLabelKey = ValidateText(request.MenuLabelKey, "menuLabelKey", 128);
        return new PortalModuleUpsert(moduleId!, displayNameKey, contractVersion, apiBaseUrl, healthPath,
            requiredPermission, menuGroup, request.MenuOrder, menuIcon, menuLabelKey, request.IsEnabled);
    }

    private static void ValidateModuleId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value[0] is not (>= 'a' and <= 'z') ||
            value.Any(character => !(character is >= 'a' and <= 'z') && !char.IsAsciiDigit(character) &&
                                   character is not '-'))
            throw new PortalValidationException("moduleId inválido.");
    }

    private static string ValidateText(string? value, string name, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
            throw new PortalValidationException($"{name} inválido.");
        return normalized;
    }

    private static string ValidatePath(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length > 256 || !normalized.StartsWith('/') || normalized.StartsWith("//") ||
            normalized.IndexOfAny(['?', '#']) >= 0)
            throw new PortalValidationException("healthPath inválido.");
        return normalized;
    }

    private static string ValidateApiBaseUrl(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 512)
            throw new PortalValidationException("apiBaseUrl inválida.");
        if (normalized.StartsWith('/') && !normalized.StartsWith("//") &&
            normalized.IndexOfAny(['?', '#']) < 0)
            return normalized;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new PortalValidationException("apiBaseUrl inválida.");
        return normalized;
    }

    private static bool IsSafeIdentifier(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            throw new PortalValidationException("Paginação inválida.");
    }

    private async Task EnsureAdministratorAsync(Guid actorUserId)
    {
        if (!await IsAdministratorAsync(actorUserId))
            throw new UnauthorizedAccessException("A operação requer administrador ativo.");
    }

    private async Task<bool> IsAdministratorAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is { IsActive: true } && await users.IsInRoleAsync(user, "Administrator");
    }

    private static PortalModuleRecord Map(PortalModule module) => new(module.ModuleId, module.DisplayNameKey,
        module.ContractVersion, module.ApiBaseUrl, module.HealthPath, module.RequiredPermission, module.MenuGroup,
        module.MenuOrder, module.MenuIcon, module.MenuLabelKey, module.IsEnabled, module.Version,
        module.CreatedAtUtc, module.UpdatedAtUtc);
}
