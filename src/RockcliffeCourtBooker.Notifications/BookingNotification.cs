namespace RockcliffeCourtBooker.Notifications;

public sealed record BookingNotification(
    Guid AttemptId,
    bool Succeeded,
    string Title,
    string Message);

public interface IBookingNotificationService : IDisposable
{
    void Register(Action<Guid>? activated = null);

    void Show(BookingNotification notification);
}
