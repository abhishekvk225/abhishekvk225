using NexaVerify.Contracts.Common;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Components;

public static class UiResults
{
    /// <summary>
    /// Ends every mutation the same way (docs/06 rule 4): success snackbar, or an error snackbar carrying the correlation id.
    /// Returns true on success.
    /// </summary>
    public static bool Report<T>(this ApiResult<T> result, IAppSnackbar snackbar, string successMessage)
    {
        if (result.IsSuccess)
        {
            snackbar.Success(successMessage);
            return true;
        }

        snackbar.Error(result.Error!);
        return false;
    }

    /// <summary>For dialogs: null on success, the error otherwise (what <see cref="FormDialog{T}"/> expects from its submit callback).</summary>
    public static ApiError? ToFormError<T>(this ApiResult<T> result) => result.IsSuccess ? null : result.Error;

    /// <summary>Wraps an unpaged API list as a single page so it can feed a <see cref="DataTable{T}"/>.</summary>
    public static ApiResult<PagedResult<T>> AsPage<T>(this ApiResult<IReadOnlyList<T>> result) =>
        result.IsSuccess
            ? ApiResult<PagedResult<T>>.Ok(new PagedResult<T>(result.Value, 1, Math.Max(1, result.Value.Count), result.Value.Count))
            : ApiResult<PagedResult<T>>.Fail(result.Error!);
}
