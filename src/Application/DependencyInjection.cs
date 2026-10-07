using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Identity;

namespace NexaVerify.Application;

public static class DependencyInjection
{
    /// <summary>Registers application services and validators. Option binding (<see cref="AuthOptions"/>, <see cref="PasswordPolicyOptions"/>) is done by the host.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>());
        services.AddSingleton<PasswordPolicy>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPlatformUserService, PlatformUserService>();
        return services;
    }
}
