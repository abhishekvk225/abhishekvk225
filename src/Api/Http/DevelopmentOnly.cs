using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace NexaVerify.Api.Http;

/// <summary>Marks a controller that exists only in the Development and Testing environments.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DevelopmentOnlyAttribute : Attribute
{
}

/// <summary>Removes every <see cref="DevelopmentOnlyAttribute"/> controller from the application model unless the environment allows them, so the routes are not mapped at all.</summary>
public sealed class DevelopmentOnlyConvention : IApplicationModelConvention
{
    private readonly bool _allowed;

    public DevelopmentOnlyConvention(IHostEnvironment environment)
    {
        _allowed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
    }

    public void Apply(ApplicationModel application)
    {
        if (_allowed)
        {
            return;
        }

        foreach (var controller in application.Controllers.Where(c => c.ControllerType.IsDefined(typeof(DevelopmentOnlyAttribute), inherit: true)).ToList())
        {
            application.Controllers.Remove(controller);
        }
    }
}
