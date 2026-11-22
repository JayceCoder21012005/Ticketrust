using System.Globalization;
using System.Text;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Admin;

public static class AdminErrors
{
    public const int ReasonMaxLength = 500;

    public static readonly Error UserNotFound = Error.NotFound("admin.user_not_found", "There is no such user.");
    public static readonly Error EventNotFound = Error.NotFound("admin.event_not_found", "There is no such event.");
    public static readonly Error OrderNotFound = Error.NotFound("admin.order_not_found", "There is no such order.");
    public static readonly Error ApplicationNotFound = Error.NotFound("admin.application_not_found", "There is no such application.");
    public static readonly Error FlagNotFound = Error.NotFound("admin.flag_not_found", "There is no such flag.");
    public static readonly Error DeadLetterNotFound = Error.NotFound("admin.dead_letter_not_found", "There is no such dead letter.");
    public static readonly Error AuditNotFound = Error.NotFound("admin.audit_not_found", "There is no such audit entry.");

    public static readonly Error OwnAdminRole = Error.Conflict("admin.own_admin_role", "You cannot remove your own Admin role or lock yourself.");
    public static readonly Error NotRefundable = Error.Conflict("admin.not_refundable", "Only a paid primary order whose tickets were not used can be refunded.");
    public static readonly Error WrongState = Error.Conflict("admin.wrong_state", "This cannot be done in the current state.");
    public static readonly Error AlreadyApplied = Error.Conflict("organizer_application.pending", "You already have an application waiting for review.");
    public static readonly Error AlreadyOrganizer = Error.Conflict("organizer_application.already_organizer", "You are an organizer already.");

    public static Error ReasonRequired => Error.Validation(
        "admin.reason_required", "Every admin change needs a reason.", new Dictionary<string, string[]> { ["reason"] = [$"1 to {ReasonMaxLength} characters."] });

    public static Error ConfirmMismatch => Error.Validation(
        "admin.confirm_mismatch", "Type the order id to confirm the refund.", new Dictionary<string, string[]> { ["confirm"] = ["Must be the order id."] });

    public static Error InvalidRole => Error.Validation(
        "admin.invalid_role", "Unknown role.", new Dictionary<string, string[]> { ["role"] = ["Customer, Organizer, Admin or GateStaff."] });

    public static Error InvalidCursor => Error.Validation("admin.invalid_cursor", "Invalid cursor.", new Dictionary<string, string[]> { ["cursor"] = ["Invalid."] });

    public static Error InvalidApplication => Error.Validation(
        "organizer_application.invalid", "Company name and phone are required.", new Dictionary<string, string[]> { ["companyName"] = ["Required."] });

    /// <returns>The trimmed reason, or null when it is missing or too long.</returns>
    public static string? CleanReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > ReasonMaxLength ? null : reason.Trim();
}

/// <summary>Keyset cursors of the admin lists: (instant, id) of the last row, base64.</summary>
public static class AdminCursor
{
    public const int MaxLimit = 100;

    public static int Limit(int? limit) => Math.Clamp(limit ?? 50, 1, MaxLimit);

    public static string Encode(DateTimeOffset at, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{id:N}"));

    public static bool TryDecode(string? cursor, out (DateTimeOffset At, Guid Id)? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Trim())).Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParse(parts[1], out var id))
            {
                value = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}
