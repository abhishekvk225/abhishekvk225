using MudBlazor;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Web.Components.Forms;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Components;

public static class LicenseDialogs
{
    /// <summary>
    /// "Issue a license" for a known client (<paramref name="clientId"/>) or, when null, one the user picks. A failure to load the
    /// plans is reported instead of opening a form that cannot be completed. Returns true when a license was issued.
    /// </summary>
    public static async Task<bool> IssueLicenseAsync(this IDialogService dialogs, ILicensingApiClient api, IAppSnackbar snackbar, Guid? clientId)
    {
        var plans = await api.ListPlansAsync();
        if (!plans.IsSuccess)
        {
            snackbar.Error(plans.Error!);
            return false;
        }

        IReadOnlyList<PlanDto> active = plans.Value.Where(p => p.IsActive).ToList();
        var saved = await dialogs.SaveAsync(
            snackbar,
            "Issue a license",
            new IssueLicenseForm { ClientId = clientId },
            m => builder =>
            {
                builder.OpenComponent<LicenseFields>(0);
                builder.AddAttribute(1, nameof(LicenseFields.Model), m);
                builder.AddAttribute(2, nameof(LicenseFields.Plans), active);
                builder.AddAttribute(3, nameof(LicenseFields.ShowClientPicker), clientId is null);
                builder.CloseComponent();
            },
            async m => m.ClientId is not { } id
                ? new ApiError("VALIDATION_FAILED", "Choose the client this license is for.", null, 400)
                : (await api.CreateAsync(id, m.ToRequest())).ToFormError(),
            "Issue license",
            _ => "The license was issued.");
        return saved is not null;
    }
}
