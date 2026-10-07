using System.Xml.Linq;
using NetArchTest.Rules;
using NexaVerify.Application.Common;
using NexaVerify.Domain.Common;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Architecture.Tests;

/// <summary>Enforces docs/01 §2: the dependency rule, thin controllers and the tenant-filter bypass ban.</summary>
public class DependencyRuleTests
{
    private const string Domain = "NexaVerify.Domain";
    private const string Contracts = "NexaVerify.Contracts";
    private const string Application = "NexaVerify.Application";
    private const string Infrastructure = "NexaVerify.Infrastructure";
    private const string Api = "NexaVerify.Api";
    private const string Web = "NexaVerify.Web";

    private static readonly System.Reflection.Assembly DomainAssembly = typeof(Entity).Assembly;
    private static readonly System.Reflection.Assembly ApplicationAssembly = typeof(Result).Assembly;
    private static readonly System.Reflection.Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;
    private static readonly System.Reflection.Assembly ApiAssembly = typeof(Program).Assembly;
    private static readonly System.Reflection.Assembly ContractsAssembly = typeof(NexaVerify.Contracts.Common.ErrorCodes).Assembly;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found.");
    }

    [Fact]
    public void Application_depends_on_no_outer_layer_or_framework()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot().HaveDependencyOnAny(Infrastructure, Api, Web, "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Domain_depends_on_nothing_of_ours()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot().HaveDependencyOnAny(Contracts, Application, Infrastructure, Api, Web, "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Contracts_depend_on_nothing_of_ours()
    {
        var result = Types.InAssembly(ContractsAssembly)
            .ShouldNot().HaveDependencyOnAny(Domain, Application, Infrastructure, Api, Web, "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_api()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .ShouldNot().HaveDependencyOnAny(Api, Web).GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Controllers_never_touch_ef_core_or_the_db_context()
    {
        var result = Types.InAssembly(ApiAssembly).That().Inherit(typeof(Microsoft.AspNetCore.Mvc.ControllerBase))
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", typeof(AppDbContext).FullName!, Infrastructure + ".Persistence")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Every_controller_declares_its_authorization_stance_explicitly()
    {
        var offenders = ApiAssembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .Where(t =>
                !t.IsDefined(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                && !t.IsDefined(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true)
                && !t.GetMethods().Any(m => m.IsDefined(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)))
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty("Controllers must carry [Authorize]/[AllowAnonymous]: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Tenant_query_filters_are_only_bypassed_in_the_platform_namespace()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var allowed = Path.Combine(src, "Infrastructure", "Platform") + Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.StartsWith(allowed, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToList();

        offenders.ShouldBeEmpty("IgnoreQueryFilters is only allowed under src/Infrastructure/Platform/: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Project_references_follow_the_allowed_graph()
    {
        var allowed = new Dictionary<string, string[]>
        {
            ["Domain"] = [],
            ["Contracts"] = [],
            ["Application"] = ["Domain", "Contracts"],
            ["Infrastructure"] = ["Application", "Domain", "Contracts"],
            ["Api"] = ["Application", "Infrastructure", "Contracts", "Domain"],
            ["Web"] = ["Contracts"],
        };

        var src = Path.Combine(RepoRoot(), "src");
        var violations = new List<string>();

        foreach (var csproj in Directory.EnumerateFiles(src, "NexaVerify.*.csproj", SearchOption.AllDirectories))
        {
            var project = Path.GetFileNameWithoutExtension(csproj).Replace("NexaVerify.", string.Empty);
            if (!allowed.TryGetValue(project, out var permitted))
            {
                violations.Add($"Unknown project {project}: add it to the dependency graph in this test and docs/01.");
                continue;
            }

            var references = XDocument.Load(csproj).Descendants("ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension(((string)r.Attribute("Include")!).Replace('\\', '/')).Replace("NexaVerify.", string.Empty));

            violations.AddRange(references.Where(r => !permitted.Contains(r)).Select(r => $"{project} -> {r}"));
        }

        violations.ShouldBeEmpty("Forbidden project references: " + string.Join("; ", violations));
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is { Count: > 0 } ? "Violating types: " + string.Join(", ", result.FailingTypeNames) : string.Empty;
}
