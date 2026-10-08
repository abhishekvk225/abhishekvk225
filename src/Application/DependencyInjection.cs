using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Api;
using NexaVerify.Application.Dashboards;
using NexaVerify.Application.Faces;
using NexaVerify.Application.Identity;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Tenancy;

namespace NexaVerify.Application;

public static class DependencyInjection
{
    /// <summary>Registers application services and validators. Option binding (<see cref="AuthOptions"/>, <see cref="PasswordPolicyOptions"/>) is done by the host.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>());
        services.AddSingleton<PasswordPolicy>();

        services.AddScoped<IMfaPolicy, MfaPolicy>();
        services.AddScoped<ISessionIssuer, SessionIssuer>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPlatformUserService, PlatformUserService>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<IClientService, ClientService>();
        services.AddScoped<IClientPortalService, ClientPortalService>();
        services.AddScoped<IClientSettingsService, ClientSettingsService>();
        services.AddScoped<ICostRuleResolver, CostRuleResolver>();
        services.AddScoped<ILicenseMeteringService, LicenseMeteringService>();
        services.AddScoped<LedgerWriter>();
        services.AddScoped<LicenseService>();
        services.AddScoped<ILicenseService>(sp => sp.GetRequiredService<LicenseService>());
        services.AddScoped<ILicenseAdjustmentService>(sp => sp.GetRequiredService<LicenseService>());
        services.AddScoped<IApiKeyService, Api.ApiKeyService>();
        services.AddScoped<IApiLogService, Api.ApiLogService>();
        services.AddScoped<IWebhookService, Api.WebhookService>();
        services.AddScoped<IFaceRecognitionService, Faces.FaceRecognitionService>();
        services.AddScoped<IFaceProfileService, Faces.FaceProfileService>();
        services.AddScoped<IRecognitionHistoryService, Faces.RecognitionHistoryService>();
        services.AddScoped<IFaceRetentionProcessor, Faces.FaceRetentionProcessor>();
        services.AddScoped<IPlanService, PlanService>();
        services.AddScoped<ICostRuleService, CostRuleService>();
        services.AddScoped<IClientLicenseService, ClientLicenseService>();
        services.AddScoped<ILicenseExpiryProcessor, LicenseExpiryProcessor>();
        services.AddScoped<IClientDashboardService, ClientDashboardService>();
        services.AddScoped<IAdminDashboardService, AdminDashboardService>();
        services.AddScoped<IUsageReportService, UsageReportService>();
        services.AddSingleton<LedgerVerificationGate>();
        services.AddScoped<ILedgerVerificationService, LedgerVerificationService>();
        services.AddScoped<ILicenseAlertService, LicenseAlertService>();
        services.AddScoped<INotificationService, NotificationService>();
        return services;
    }
}
