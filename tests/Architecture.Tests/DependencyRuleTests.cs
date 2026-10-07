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

    private static IEnumerable<(string Relative, string Text)> SourceFiles()
    {
        var root = RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}Migrations{sep}"))
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)));
    }

    private static IReadOnlyList<string> FilesUsing(string[] tokens, string[] allowedPrefixes) =>
        SourceFiles()
            .Where(f => !allowedPrefixes.Any(p => f.Relative.StartsWith(p, StringComparison.Ordinal)))
            .Where(f => tokens.Any(t => f.Text.Contains(t, StringComparison.Ordinal)))
            .Select(f => f.Relative)
            .ToList();

    [Fact]
    public void Tenant_filter_bypass_and_raw_sql_are_confined_to_approved_infrastructure()
    {
        // These APIs skip the SaveChanges write guard and/or the EF tenant filter; RLS would be the only layer left.
        string[] tokens = ["IgnoreQueryFilters", "ExecuteUpdate", "ExecuteDelete", "FromSql", "ExecuteSql", "SqlQuery"];
        string[] allowed =
        [
            "src/Infrastructure/Platform/",
            "src/Infrastructure/Persistence/Rls/",
            "src/Infrastructure/Persistence/Guards/",
            "src/Infrastructure/Persistence/Maintenance/",
            "src/Infrastructure/Background/",
        ];

        var offenders = FilesUsing(tokens, allowed);

        offenders.ShouldBeEmpty("Use of filter-bypassing/raw SQL APIs is restricted to " + string.Join(", ", allowed) + ": " + string.Join(", ", offenders));
    }

    [Fact]
    public void Query_filters_are_never_bypassed_without_naming_the_filter()
    {
        var offenders = SourceFiles()
            .Where(f => f.Text.Contains("IgnoreQueryFilters()", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .ToList();

        offenders.ShouldBeEmpty("IgnoreQueryFilters() with no arguments disables every named filter (soft-delete too); name the filter: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Entering_tenant_or_platform_scope_is_restricted_to_approved_code()
    {
        // Anything that can call BeginPlatform/BeginTenant can read or write across tenants.
        string[] tokens = ["ITenantScope", "BeginPlatform(", "BeginTenant("];
        string[] allowed =
        [
            "src/Application/Abstractions/ITenantScope.cs",
            "src/Application/Identity/",
            "src/Infrastructure/Tenancy/",
            "src/Infrastructure/Platform/",
            "src/Infrastructure/Persistence/",
            "src/Infrastructure/Background/",
            "src/Infrastructure/Identity/",
            "src/Infrastructure/DependencyInjection.cs",
            "src/Migrator/",
        ];

        var offenders = FilesUsing(tokens, allowed);

        offenders.ShouldBeEmpty("Tenant/platform scope may only be entered from: " + string.Join(", ", allowed) + ". Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_request_dto_has_a_fluent_validator()
    {
        var requests = ContractsAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("Request", StringComparison.Ordinal))
            .ToList();
        var validated = ApplicationAssembly.GetTypes()
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(FluentValidation.IValidator<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToHashSet();

        var missing = requests.Where(r => !validated.Contains(r)).Select(r => r.Name).ToList();

        missing.ShouldBeEmpty("Request DTOs without a validator (FluentValidation is the single validation system): " + string.Join(", ", missing));
    }

    [Fact]
    public void Enum_names_fit_the_string_column_convention()
    {
        // Enums are stored as nvarchar(30) (AppDbContext conventions).
        var tooLong = new[] { ContractsAssembly, DomainAssembly, ApplicationAssembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsEnum)
            .SelectMany(t => Enum.GetNames(t).Select(n => (Type: t.Name, Name: n)))
            .Where(x => x.Name.Length > 30)
            .Select(x => $"{x.Type}.{x.Name}")
            .ToList();

        tooLong.ShouldBeEmpty("Enum member names longer than 30 characters: " + string.Join(", ", tooLong));
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
            ["Migrator"] = ["Application", "Infrastructure", "Contracts", "Domain"],
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
