using System.ComponentModel.DataAnnotations;
using TicketRush.Application.Options;

namespace TicketRush.Application.Tests.Queue;

public sealed class QueueOptionsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var options = new QueueOptions();

        options.AdmissionRatePerSecond.ShouldBe(50);
        options.MaxActiveShoppers.ShouldBe(500);
        options.ShoppingWindowMinutes.ShouldBe(10);
        options.PreQueueMinutes.ShouldBe(30);
    }

    [Fact]
    public void Zero_MaxActiveShoppers_fails_validation()
    {
        var options = new QueueOptions { MaxActiveShoppers = 0 };

        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true).ShouldBeFalse();
    }
}
