using NexaVerify.Web.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace NexaVerify.Web.Components;

/// <summary>Outcome of a confirmed <see cref="ConfirmDialog"/>.</summary>
public sealed record ConfirmResult(string? Reason);

public static class DialogExtensions
{
    private static DialogOptions Modal(MaxWidth width = MaxWidth.Small) => new()
    {
        MaxWidth = width,
        FullWidth = true,
        CloseOnEscapeKey = true,
        BackdropClick = false,
    };

    /// <summary>Shows a confirmation. Returns null when cancelled. Pass <paramref name="requiredPhrase"/> for type-to-confirm.</summary>
    public static async Task<ConfirmResult?> ConfirmAsync(
        this IDialogService dialogs,
        string title,
        string message,
        string confirmText = "Confirm",
        bool destructive = false,
        string? requiredPhrase = null,
        bool requireReason = false)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { x => x.Title, title },
            { x => x.Message, message },
            { x => x.ConfirmText, confirmText },
            { x => x.Destructive, destructive },
            { x => x.RequiredPhrase, requiredPhrase },
            { x => x.RequireReason, requireReason },
        };

        var dialog = await dialogs.ShowAsync<ConfirmDialog>(title, parameters, Modal());
        var result = await dialog.Result;
        return result is { Canceled: false, Data: ConfirmResult confirmed } ? confirmed : null;
    }

    /// <summary>
    /// Shows a one-time secret (API key, webhook secret). The dialog cannot be dismissed without the "I stored it" confirmation
    /// and discards the secret afterwards.
    /// </summary>
    public static async Task RevealSecretAsync(this IDialogService dialogs, string title, string secret, string? description = null)
    {
        var parameters = new DialogParameters<SecretRevealDialog>
        {
            { x => x.Title, title },
            { x => x.Secret, secret },
            { x => x.Description, description },
        };

        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = false, BackdropClick = false, CloseButton = false };
        var dialog = await dialogs.ShowAsync<SecretRevealDialog>(title, parameters, options);
        await dialog.Result;
    }

    /// <summary>
    /// "Submit a form, then show the one-time secret the API returned" (API keys, webhook secrets). The secret lives only in a local
    /// variable between the API response and the reveal dialog, and is cleared as soon as the dialog closes. Returns true when saved.
    /// </summary>
    public static async Task<bool> ShowSecretFormAsync<TModel, TResult>(
        this IDialogService dialogs,
        string title,
        TModel model,
        RenderFragment<TModel> fields,
        Func<TModel, Task<ApiResult<TResult>>> submit,
        Func<TResult, string?> secretOf,
        string revealTitle,
        string revealDescription,
        string submitText)
        where TModel : class
    {
        string? secret = null;
        var saved = await dialogs.ShowFormAsync(
            title, model, fields,
            async m =>
            {
                var result = await submit(m);
                if (result.IsSuccess)
                {
                    secret = secretOf(result.Value);
                }

                return result.ToFormError();
            },
            submitText);
        try
        {
            if (saved is null)
            {
                return false;
            }

            if (secret is not null)
            {
                await dialogs.RevealSecretAsync(revealTitle, secret, revealDescription);
            }

            return true;
        }
        finally
        {
            secret = null;
        }
    }

    /// <summary>Shows a model form. Returns the (valid, submitted) model, or null when cancelled.</summary>
    public static async Task<T?> ShowFormAsync<T>(
        this IDialogService dialogs,
        string title,
        T model,
        RenderFragment<T> fields,
        Func<T, Task<ApiError?>> onSubmit,
        string submitText = "Save")
        where T : class
    {
        var parameters = new DialogParameters<FormDialog<T>>
        {
            { x => x.Title, title },
            { x => x.Model, model },
            { x => x.FormContent, fields },
            { x => x.OnSubmit, onSubmit },
            { x => x.SubmitText, submitText },
        };

        var dialog = await dialogs.ShowAsync<FormDialog<T>>(title, parameters, Modal());
        var result = await dialog.Result;
        return result is { Canceled: false, Data: T saved } ? saved : null;
    }

    /// <summary>
    /// The standard "edit in a dialog" flow: show the form, save through <paramref name="save"/> (field errors land on the form),
    /// and on success say so in a snackbar. Returns the saved model, or null when cancelled.
    /// </summary>
    public static async Task<T?> SaveAsync<T>(
        this IDialogService dialogs,
        IAppSnackbar snackbar,
        string title,
        T model,
        RenderFragment<T> fields,
        Func<T, Task<ApiError?>> save,
        string submitText,
        Func<T, string> successMessage)
        where T : class
    {
        var saved = await dialogs.ShowFormAsync(title, model, fields, save, submitText);
        if (saved is not null)
        {
            snackbar.Success(successMessage(saved));
        }

        return saved;
    }

    /// <summary>
    /// The standard "are you sure" flow: confirm (optionally with a reason or typed phrase), apply, and report the outcome in a
    /// snackbar. Returns null when cancelled, otherwise the API result (success or failure).
    /// </summary>
    public static async Task<ApiResult<TResult>?> ConfirmAndApplyAsync<TResult>(
        this IDialogService dialogs,
        IAppSnackbar snackbar,
        string title,
        string message,
        string confirmText,
        Func<string?, Task<ApiResult<TResult>>> apply,
        string successMessage,
        bool destructive = false,
        bool requireReason = false,
        string? requiredPhrase = null)
    {
        var confirmed = await dialogs.ConfirmAsync(title, message, confirmText, destructive, requiredPhrase, requireReason);
        if (confirmed is null)
        {
            return null;
        }

        var result = await apply(confirmed.Reason);
        result.Report(snackbar, successMessage);
        return result;
    }
}
