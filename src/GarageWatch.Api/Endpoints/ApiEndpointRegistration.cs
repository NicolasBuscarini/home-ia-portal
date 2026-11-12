namespace GarageWatch.Api.Endpoints;

public static class ApiEndpointRegistration
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder routes)
    {
        BaseEndpoint[] endpoints =
        [
            new Health.LiveEndpoint(), new Health.ReadyEndpoint(), new Health.MetricsEndpoint(),
            new Authentication.LoginEndpoint(), new Authentication.RegisterEndpoint(),
            new Authentication.LogoutEndpoint(), new Authentication.SessionEndpoint(),
            new Authentication.MeEndpoint(), new Clients.HeartbeatEndpoint(),
            new AccessRequests.CreateAccessRequestEndpoint(), new AccessRequests.GetMyAccessRequestEndpoint(),
            new Cameras.ListCamerasEndpoint(),
            new Clips.GetClipAnalysisDispatchPolicyEndpoint(), new Clips.UpdateClipAnalysisDispatchPolicyEndpoint(),
            new Clips.CreateClipAnalysisRequestEndpoint(),
            new Clips.DeleteClipEndpoint(), new Clips.DeleteClipsEndpoint(),
            new Clips.DeleteAllClipsEndpoint(),
            new Admin.GetClipRetentionEndpoint(), new Admin.UpdateClipRetentionEndpoint(),
            new Admin.UpdateClipPreservationEndpoint(),
            new Clips.ListClipsEndpoint(), new Clips.GetClipEndpoint(),
            new Clips.GetClipAnalysisResultEndpoint(), new Clips.GetClipMediaEndpoint(),
            new Clips.DownloadClipsEndpoint(),
            new Clips.GetClipThumbnailEndpoint(),
            new Admin.ListContextsEndpoint(), new Admin.CreateContextEndpoint(),
            new Admin.UpdateContextEndpoint(), new Admin.DeactivateContextEndpoint(),
            new Admin.ListCamerasEndpoint(), new Admin.CreateCameraEndpoint(),
            new Admin.UpdateCameraEndpoint(), new Admin.DeactivateCameraEndpoint(),
            new Admin.GetCameraConnectionEndpoint(), new Admin.UpdateCameraConnectionEndpoint(),
            new Admin.GetClipTriggerConfigurationEndpoint(), new Admin.UpdateClipTriggerConfigurationEndpoint(),
            new Admin.GetMonitoringCaptureEndpoint(), new Admin.UpdateMonitoringCaptureEndpoint(),
            new Admin.GetPlatformSettingsEndpoint(), new Admin.UpdatePlatformSettingsEndpoint(),
            new Admin.CreatePortalBackupEndpoint(), new Admin.ListPortalBackupsEndpoint(),
            new Admin.GetPortalBackupSummaryEndpoint(),
            new Biometrics.GetIdentityObservationFramesEndpoint(),
            new Biometrics.GetIdentityObservationFrameEndpoint(),
            new Admin.ListUsersEndpoint(), new Admin.GetUserEndpoint(), new Admin.DeactivateUserEndpoint(),
            new Admin.ListPermissionCatalogEndpoint(), new Admin.ListAccessProfilesEndpoint(),
            new Admin.CreateAccessProfileEndpoint(), new Admin.PublishAccessProfileVersionEndpoint(),
            new Admin.ReplaceUserAccessProfilesEndpoint(),
            new Admin.ListAccessRequestsEndpoint(), new Admin.ReviewAccessRequestEndpoint(),
            new Admin.ListKnownPeopleEndpoint(), new Admin.CreateKnownPersonEndpoint(), new Admin.RemoveKnownPersonEndpoint(),
            new Admin.LinkBiometricPersonEndpoint(), new Admin.UnlinkBiometricPersonEndpoint(),
            new Admin.GetFaceExtractorRegistryEndpoint(), new Admin.CreateFaceExtractorCandidateEndpoint(),
            new Admin.SelectFaceExtractorEndpoint(),
            new Admin.ListPortalModulesEndpoint(), new Admin.CreatePortalModuleEndpoint(),
            new Admin.UpdatePortalModuleEndpoint(),
            new Admin.GetIntegrationCredentialEndpoint(), new Admin.SetIntegrationCredentialEndpoint(),
            new Admin.RemoveIntegrationCredentialEndpoint(),
            new Portal.GetPortalNavigationEndpoint(), new Portal.GetPortalPreferencesEndpoint(),
            new Portal.UpdatePortalPreferencesEndpoint(), new Portal.CreatePortalAuthorizationDecisionEndpoint(),
            new Portal.GetActivityWatchSummaryEndpoint(),
            new Portal.GetMonitoringModuleHealthEndpoint(),
            new Portal.GetPortalHealthEndpoint(),
            new Events.CreatePlatformEventEndpoint(),
            new Admin.ListPlatformEventsEndpoint(), new Admin.GetPlatformEventEndpoint(),
            new Biometrics.ListBiometricEnrollmentsEndpoint(), new Biometrics.EnrollBiometricEndpoint(),
            new Biometrics.RevokeBiometricEnrollmentEndpoint(),
            new Biometrics.VerifyBiometricEnrollmentEndpoint(),
            new Biometrics.IngestIdentityObservationEndpoint(),
            new Biometrics.ListIdentityObservationsEndpoint(),
            new Biometrics.GetIdentityObservationEndpoint(),
            new Biometrics.ReviewIdentityObservationEndpoint(),
            new Biometrics.GetIdentityCalibrationEndpoint(),
            new Biometrics.CreateIdentityMatchPolicyDraftEndpoint(),
            new Biometrics.ListIdentityMatchPolicyVersionsEndpoint(),
            new Biometrics.PromoteIdentityMatchPolicyVersionEndpoint(),
            new Live.GetPlaylistEndpoint(), new Live.GetSegmentEndpoint(),
            new System.GetSystemProfileEndpoint()
        ];

        foreach (var endpoint in endpoints)
            endpoint.Map(routes);

        return routes;
    }
}
