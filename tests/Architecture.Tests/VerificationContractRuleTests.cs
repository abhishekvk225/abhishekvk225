using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NetArchTest.Rules;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Architecture.Tests;

/// <summary>
/// M14 contract-skeleton guarantees (docs/07 §3, §13): Upcoming modules are catalogue data only; the pure layers stay pure;
/// no vendor or I/O library leaks into Application. (QA's broader suite lives in VerificationArchitectureTests.)
/// </summary>
public class VerificationContractRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(VerificationRequest).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(IVerificationModule).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(VerificationModuleKeys).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(NexaVerify.Infrastructure.Persistence.AppDbContext).Assembly;
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    /// <summary>Hyphen-less, lower-case tokens so "driving-licence", "DrivingLicence" and "driving_licence" all hit.</summary>
    private static readonly string[] UpcomingTokens = VerificationModuleKeys.Upcoming.Select(Squash).ToArray();

    private static string Squash(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool MentionsUpcoming(string value) =>
        UpcomingTokens.Any(t => Squash(value).Contains(t, StringComparison.Ordinal));

    [Fact]
    public void Upcoming_modules_have_no_route()
    {
        var offenders = ApiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(true).Select(a => a.Template ?? string.Empty)
                .Concat(t.GetCustomAttributes<RouteAttribute>(true).Select(a => a.Template ?? string.Empty))
                .Concat(t.GetMethods().SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>(true).Select(a => a.Template ?? string.Empty)))
                .Select(template => (Controller: t.Name, Template: template)))
            .Where(x => MentionsUpcoming(x.Template) || MentionsUpcoming(x.Controller))
            .Select(x => $"{x.Controller} {x.Template}")
            .ToList();

        offenders.ShouldBeEmpty("Upcoming modules must not have routes: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Upcoming_modules_have_no_processor_service_or_provider_type()
    {
        var offenders = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ApiAssembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace?.Contains(".Verification", StringComparison.Ordinal) == true || t.GetInterfaces().Any(i => i == typeof(IModuleProcessor)))
            .Where(t => MentionsUpcoming(t.Name))
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty("Upcoming modules must not have implementation types: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Upcoming_modules_have_no_permission_key_and_no_metered_operation()
    {
        var keys = Permissions.All.Select(p => p.Key).Where(MentionsUpcoming).ToList();
        var operations = Enum.GetNames<MeteredOperation>().Where(MentionsUpcoming).ToList();
        var types = Enum.GetNames<VerificationType>().Where(MentionsUpcoming).ToList();

        keys.ShouldBeEmpty("Permission keys for Upcoming modules: " + string.Join(", ", keys));
        operations.ShouldBeEmpty("MeteredOperation members for Upcoming modules: " + string.Join(", ", operations));
        types.ShouldBeEmpty("VerificationType members for Upcoming modules: " + string.Join(", ", types));
    }

    [Fact]
    public void Every_catalogue_descriptor_satisfies_the_upcoming_and_available_invariants()
    {
        ModuleCatalogue.All.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(ModuleCatalogue.All.Count);
        ModuleCatalogue.Upcoming.Select(d => d.Key).Order().ShouldBe(VerificationModuleKeys.Upcoming.Order());
        ModuleCatalogue.Available.Select(d => d.Key).ShouldBe(VerificationModuleKeys.Available);

        foreach (var d in ModuleCatalogue.Upcoming)
        {
            (d.Type, d.SubmitPermissions.Count, d.CostOperations.Count, d.ProviderIds.Count).ShouldBe((null, 0, 0, 0), d.Key);
        }

        foreach (var d in ModuleCatalogue.Available)
        {
            d.Type.ShouldNotBeNull(d.Key);
            d.SubmitPermissions.ShouldNotBeEmpty(d.Key);
            d.CostOperations.ShouldNotBeEmpty(d.Key);
        }
    }

    [Fact]
    public void Every_verification_metered_operation_belongs_to_an_available_module()
    {
        var owned = ModuleCatalogue.Available.SelectMany(d => d.CostOperations).ToHashSet();
        var all = Enum.GetValues<MeteredOperation>().Where(VerificationOperations.IsVerificationOperation).ToList();

        all.Where(o => !owned.Contains(o)).ShouldBeEmpty();
        owned.Where(o => !VerificationOperations.IsVerificationOperation(o)).ShouldBeEmpty();
    }

    [Fact]
    public void Verification_permissions_are_in_the_catalogue_with_the_right_scope()
    {
        var byKey = Permissions.All.ToDictionary(p => p.Key);
        string[] client =
        [
            Permissions.Verification.BankStatementSubmit, Permissions.Verification.PanSubmit, Permissions.Verification.PassportSubmit,
            Permissions.Verification.Read, Permissions.Verification.ReportDownload, Permissions.Verification.DocumentDownload, Permissions.Verification.Erase,
        ];
        string[] platform = [Permissions.VerificationAdmin.Read, Permissions.VerificationAdmin.Configure];

        client.ShouldAllBe(k => byKey.ContainsKey(k) && byKey[k].Scope == PermissionScopeKind.Client);
        platform.ShouldAllBe(k => byKey.ContainsKey(k) && byKey[k].Scope == PermissionScopeKind.Platform);
        Permissions.All.Select(p => p.Key).Distinct().Count().ShouldBe(Permissions.All.Count);

        // Role defaults (ADR-30): Client Admin gets the client keys, Super Admin the platform keys, Client User none.
        client.Except(SystemRoles.PermissionsFor(SystemRoles.ClientAdmin)).ShouldBeEmpty();
        platform.Except(SystemRoles.PermissionsFor(SystemRoles.SuperAdmin)).ShouldBeEmpty();
        SystemRoles.PermissionsFor(SystemRoles.SuperAdmin).Intersect(client).ShouldBeEmpty();
        SystemRoles.PermissionsFor(SystemRoles.ClientUser).Where(k => k.StartsWith("verification.", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void Contracts_make_no_authenticity_claims_through_member_names()
    {
        string[] forbidden = ["isauthentic", "isgenuine", "isvalid", "isverified", "authentic", "genuine", "isfake", "isforged"];

        var offenders = ContractsAssembly.GetTypes()
            .Where(t => t.Namespace == "NexaVerify.Contracts.Verification")
            .SelectMany(t => t.GetProperties().Select(p => (Type: t.Name, Member: p.Name))
                .Concat(t.GetConstructors().SelectMany(c => c.GetParameters().Select(p => (Type: t.Name, Member: p.Name ?? string.Empty)))))
            .Where(x => forbidden.Contains(x.Member.ToLowerInvariant()))
            .Select(x => $"{x.Type}.{x.Member}")
            .ToList();

        offenders.ShouldBeEmpty("DTO members must not claim authenticity (Rule V-1): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Verification_domain_and_application_stay_pure()
    {
        string[] forbiddenForBoth =
        [
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "System.Net.Http", "System.Diagnostics.Process", "System.Data",
            "Microsoft.Data.SqlClient", "SkiaSharp", "UglyToad", "PDFtoImage", "Tesseract", "Docnet", "PdfSharp",
        ];

        var domain = Types.InAssembly(DomainAssembly).That().ResideInNamespace("NexaVerify.Domain.Verification")
            .ShouldNot().HaveDependencyOnAny(forbiddenForBoth.Concat(["NexaVerify.Application", "NexaVerify.Contracts", "NexaVerify.Infrastructure"]).ToArray()).GetResult();
        var application = Types.InAssembly(ApplicationAssembly).That().ResideInNamespace("NexaVerify.Application.Verification")
            .ShouldNot().HaveDependencyOnAny(forbiddenForBoth.Concat(["NexaVerify.Infrastructure", "NexaVerify.Api"]).ToArray()).GetResult();

        domain.IsSuccessful.ShouldBeTrue("Domain.Verification impurity: " + string.Join(", ", domain.FailingTypeNames ?? []));
        application.IsSuccessful.ShouldBeTrue("Application.Verification impurity: " + string.Join(", ", application.FailingTypeNames ?? []));
    }

    [Fact]
    public void No_provider_vendor_or_io_library_code_in_application()
    {
        // Vendor adapters live in Infrastructure.Verification; Application only knows the abstractions (docs/07 §4.5).
        string[] tokens = ["HttpClient", "System.Diagnostics.Process", "ProcessStartInfo", "SkiaSharp", "UglyToad", "PDFtoImage", "using Tesseract", "using System.Net.Http"];
        var root = RepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "Application", "Verification"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetRelativePath(root, f), Text: File.ReadAllText(f)))
            .Where(f => tokens.Any(t => f.Text.Contains(t, StringComparison.Ordinal)))
            .Select(f => f.File)
            .ToList();

        offenders.ShouldBeEmpty("Vendor/IO code in Application.Verification: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_fake_or_simulated_type_in_production_verification_code()
    {
        var offenders = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ApiAssembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace?.Contains(".Verification", StringComparison.Ordinal) == true)
            .Where(t => t.Name.StartsWith("Fake", StringComparison.Ordinal) || t.Name.StartsWith("Simulated", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty("Fabricated results must not exist in production code (ADR-27): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Domain_verification_entities_follow_the_tenant_conventions()
    {
        var tenantOwned = typeof(NexaVerify.Domain.Common.ITenantOwned);
        var strict = typeof(NexaVerify.Domain.Common.IStrictTenantOwned);
        var types = DomainAssembly.GetTypes().Where(t => t.Namespace == "NexaVerify.Domain.Verification").ToDictionary(t => t.Name);

        foreach (var name in new[] { "VerificationDocument", "VerificationDocumentBlob", "VerificationResult", "VerificationCheck" })
        {
            strict.IsAssignableFrom(types[name]).ShouldBeTrue(name);
        }

        foreach (var name in new[] { "VerificationRequest", "VerificationEvent", "VerificationDownloadGrant" })
        {
            tenantOwned.IsAssignableFrom(types[name]).ShouldBeTrue(name);
            strict.IsAssignableFrom(types[name]).ShouldBeFalse(name);
        }

        tenantOwned.IsAssignableFrom(types["VerificationModuleSetting"]).ShouldBeFalse("module settings are global");
        typeof(NexaVerify.Domain.Common.IAppendOnly).IsAssignableFrom(types["VerificationEvent"]).ShouldBeTrue();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found.");
    }
}
