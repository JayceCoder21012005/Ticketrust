using TicketRush.Application.Features.Tickets.ListMyTickets;

namespace TicketRush.Application.Tests.Tickets;

public sealed class ListMyTicketsQueryTests
{
    private static readonly ListMyTicketsValidator Validator = new();

    private static ListMyTicketsQuery Query(string? scope) => new(Guid.NewGuid(), scope);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("upcoming")]
    [InlineData("past")]
    [InlineData("PAST")]
    [InlineData("Upcoming")]
    public void Known_scopes_and_no_scope_are_valid(string? scope)
    {
        Validator.Validate(Query(scope)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("future")]
    [InlineData("all")]
    [InlineData(" past")]
    [InlineData("past,upcoming")]
    public void Any_other_scope_is_invalid_with_a_domain_code(string scope)
    {
        var errors = Validator.Validate(Query(scope)).Errors;

        errors.ShouldHaveSingleItem().ErrorCode.ShouldBe("ticket.invalid_scope");
        errors[0].PropertyName.ShouldBe("Scope");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("upcoming", false)]
    [InlineData("past", true)]
    [InlineData("Past", true)]
    public void Upcoming_is_the_default_and_past_is_case_insensitive(string? scope, bool wantsPast)
    {
        Query(scope).WantsPast.ShouldBe(wantsPast);
    }
}
