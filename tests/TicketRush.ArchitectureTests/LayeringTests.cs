using System.Reflection;
using NetArchTest.Rules;

namespace TicketRush.ArchitectureTests;

/// <summary>Dependency rule from spec 00: Domain ← Application ← Infrastructure/Api/Worker.</summary>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(Domain.Primitives.Entity).Assembly;
    private static readonly Assembly Application = typeof(Application.DependencyInjection).Assembly;

    [Fact]
    public void Domain_references_only_the_base_class_library()
    {
        var references = Domain.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.ShouldAllBe(name => name == "System.Runtime" || name.StartsWith("System.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TicketRush.Application")]
    [InlineData("TicketRush.Infrastructure")]
    [InlineData("TicketRush.Api")]
    [InlineData("TicketRush.Worker")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("StackExchange.Redis")]
    public void Domain_does_not_depend_on(string forbidden)
    {
        AssertNoDependency(Domain, forbidden);
    }

    [Theory]
    [InlineData("TicketRush.Infrastructure")]
    [InlineData("TicketRush.Api")]
    [InlineData("TicketRush.Worker")]
    [InlineData("Npgsql")]
    [InlineData("StackExchange.Redis")]
    [InlineData("Microsoft.AspNetCore")]
    public void Application_does_not_depend_on(string forbidden)
    {
        AssertNoDependency(Application, forbidden);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_presentation_hosts()
    {
        var infrastructure = typeof(Infrastructure.DependencyInjection).Assembly;

        AssertNoDependency(infrastructure, "TicketRush.Api");
        AssertNoDependency(infrastructure, "TicketRush.Worker");
    }

    [Fact]
    public void Domain_entities_have_no_public_setters()
    {
        var offenders = Domain.GetTypes()
            .Where(t => t.IsClass && t.Namespace?.StartsWith("TicketRush.Domain", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(p => p.SetMethod is { IsPublic: true } && !IsInitOnly(p))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        offenders.ShouldBeEmpty();
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static void AssertNoDependency(Assembly assembly, string forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(forbidden).GetResult();

        (result.FailingTypeNames ?? []).ShouldBeEmpty($"{assembly.GetName().Name} must not depend on {forbidden}");
    }
}
