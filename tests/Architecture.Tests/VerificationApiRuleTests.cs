using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Filters;

namespace NexaVerify.Architecture.Tests;

/// <summary>M14 API guarantees (docs/07 §10): thin controllers, every action authorised, abuse-prone actions throttled, no tenant filter bypass.</summary>
public class VerificationApiRuleTests
{
    private static readonly Assembly Api = typeof(Program).Assembly;

    private static IEnumerable<Type> Controllers() =>
        Api.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t.Namespace == "NexaVerify.Api.Controllers.Verification");

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    [Fact]
    public void There_are_verification_controllers_and_each_action_requires_a_permission()
    {
        var controllers = Controllers().ToList();
        controllers.Count.ShouldBe(3);

        foreach (var action in controllers.SelectMany(Actions))
        {
            action.GetCustomAttributes<HasPermissionAttribute>().ShouldNotBeEmpty($"{action.DeclaringType!.Name}.{action.Name} has no [HasPermission]");
            action.GetCustomAttributes<AllowAnonymousAttribute>().ShouldBeEmpty($"{action.DeclaringType!.Name}.{action.Name} must not be anonymous");
        }
    }

    [Fact]
    public void Controllers_depend_on_application_services_only_never_on_ef_or_infrastructure()
    {
        foreach (var controller in Controllers())
        {
            var parameters = controller.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
            foreach (var type in parameters)
            {
                var ns = type.Namespace ?? string.Empty;
                (ns.StartsWith("NexaVerify.Application", StringComparison.Ordinal) || ns == "Microsoft.Extensions.Options").ShouldBeTrue($"{controller.Name} depends on {type.FullName}");
            }

            controller.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.FieldType.Namespace ?? string.Empty)
                .ShouldAllBe(ns => !ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) && !ns.StartsWith("NexaVerify.Infrastructure", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Submissions_retries_and_downloads_are_throttled_and_uploads_have_a_configured_size_limit()
    {
        var all = Controllers().SelectMany(Actions).ToList();
        var submit = all.Where(a => a.GetCustomAttributes<HttpPostAttribute>().Any(p => p.Template is "bank-statements" or "pan" or "passports")).ToList();
        submit.Count.ShouldBe(3);
        submit.ShouldAllBe(a => a.GetCustomAttribute<ThrottleAttribute>() != null && a.GetCustomAttribute<VerificationUploadLimitAttribute>() != null);

        foreach (var name in new[] { "RetryExternal", "CreateDownload", "Download" })
        {
            all.Single(a => a.Name == name).GetCustomAttribute<ThrottleAttribute>().ShouldNotBeNull(name);
        }

        all.Single(a => a.Name == "StatsCsv").GetCustomAttribute<ThrottleAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void No_verification_code_bypasses_the_tenant_query_filter()
    {
        var root = RepoRoot();
        var offenders = new[] { "src/Application/Verification", "src/Infrastructure/Verification", "src/Api/Controllers/Verification" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => File.ReadAllText(f).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Verification_services_never_log_exception_messages_or_objects_that_can_carry_document_text()
    {
        // Logging an exception object prints its message, which a parser can fill with document text. Only the type name is allowed.
        var root = RepoRoot();
        var offenders = new[] { "src/Application/Verification/Processing", "src/Application/Verification/Query", "src/Application/Verification/Submission", "src/Infrastructure/Verification/Storage", "src/Infrastructure/Background" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "Verification*.cs", SearchOption.AllDirectories))
            .Select(f => (File: f, Lines: File.ReadAllLines(f)))
            .SelectMany(f => f.Lines.Select((line, i) => (File: Path.GetRelativePath(root, f.File), Line: i + 1, Text: line)))
            .Where(x => (x.Text.Contains("_logger.Log", StringComparison.Ordinal) || x.Text.Contains("logger.Log", StringComparison.Ordinal))
                        && (x.Text.Contains("(ex,", StringComparison.Ordinal) || x.Text.Contains("(ex)", StringComparison.Ordinal) || x.Text.Contains("ex.Message", StringComparison.Ordinal)))
            .Select(x => $"{x.File}:{x.Line}")
            .ToList();

        offenders.ShouldBeEmpty();
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
