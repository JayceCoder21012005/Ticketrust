namespace TicketRush.Domain.Users;

public static class Roles
{
    public const string Customer = "Customer";
    public const string Organizer = "Organizer";
    public const string Admin = "Admin";
    public const string GateStaff = "GateStaff";

    public static IReadOnlyList<string> All { get; } = [Customer, Organizer, Admin, GateStaff];
}
