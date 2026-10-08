namespace NexaVerify.Web.Services;

/// <summary>
/// Unread-notification count shared by the bell in the layout and the notifications page, so marking one as read updates the badge
/// straight away. Scoped to the circuit; holds a number only.
/// </summary>
public sealed class NotificationState
{
    public long UnreadCount { get; private set; }

    public event Action? Changed;

    public void Set(long unread)
    {
        var value = Math.Max(0, unread);
        if (value == UnreadCount)
        {
            return;
        }

        UnreadCount = value;
        Changed?.Invoke();
    }
}
