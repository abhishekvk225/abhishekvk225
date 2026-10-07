using MudBlazor;

namespace NexaVerify.Web.Services;

/// <summary>Single entry point for user feedback. Errors carry the correlation id so support can trace them (docs/06 §5).</summary>
public interface IAppSnackbar
{
    void Success(string message);

    void Info(string message);

    void Warning(string message);

    void Error(string message, string? correlationId = null);

    void Error(ApiError error);
}

public sealed class AppSnackbar(ISnackbar snackbar) : IAppSnackbar
{
    public void Success(string message) => snackbar.Add(message, Severity.Success);

    public void Info(string message) => snackbar.Add(message, Severity.Info);

    public void Warning(string message) => snackbar.Add(message, Severity.Warning);

    public void Error(string message, string? correlationId = null) =>
        snackbar.Add(
            string.IsNullOrWhiteSpace(correlationId) ? message : $"{message} (ref {correlationId})",
            Severity.Error,
            options =>
            {
                options.RequireInteraction = true;
                options.ShowCloseIcon = true;
            });

    public void Error(ApiError error) => Error(error.Message, error.CorrelationId);
}
