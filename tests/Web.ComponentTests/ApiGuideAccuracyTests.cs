using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.ComponentTests;

/// <summary>
/// The API guide must only promise what the API really does. Every response header, request header and webhook header the guide
/// names is checked against the source of the component that emits or reads it, so a made-up header fails the build.
/// </summary>
public class ApiGuideAccuracyTests
{
    // Curated: header -> where it really lives (relative to the repository root).
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // Response headers the API sends.
        ["Retry-After"] = ["src/Api"],
        ["X-Correlation-Id"] = ["src/Contracts", "src/Api"],
        // Request headers the API reads.
        ["X-Api-Key"] = ["src/Contracts", "src/Api"],
        ["Idempotency-Key"] = ["src/Api"],
        // Headers the webhook sender adds.
        ["X-Signature"] = ["src/Infrastructure"],
        ["X-Event-Id"] = ["src/Infrastructure"],
        ["X-Event-Type"] = ["src/Infrastructure"],
    };

    private static readonly Regex HeaderLike = new(@"\b(X-[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*|Idempoten[A-Za-z-]+|[A-Z][a-z]+-After|[A-Z][a-z]+-Replayed)\b", RegexOptions.None, TimeSpan.FromSeconds(2));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexaVerify.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string GuideText()
    {
        var root = RepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src/Web/Blazor/Pages/Client/ApiDocs.razor"));
        var content = File.ReadAllText(Path.Combine(root, "src/Web/Blazor/Services/ApiDocsContent.cs"));
        return razor + "\n" + content;
    }

    [Fact]
    public void Every_header_the_guide_names_is_on_the_curated_list()
    {
        var named = HeaderLike.Matches(GuideText()).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        named.ShouldNotBeEmpty();
        named.Where(h => !Allowed.ContainsKey(h)).ShouldBeEmpty("the guide promises a header the API does not send (for example X-RateLimit-* or Idempotent-Replayed)");
    }

    [Fact]
    public void Every_curated_header_really_appears_in_the_source_that_owns_it()
    {
        var root = RepoRoot();
        foreach (var (header, places) in Allowed)
        {
            var found = places.Any(place => Directory.EnumerateFiles(Path.Combine(root, place), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Any(f => File.ReadAllText(f).Contains(header.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase)
                          || File.ReadAllText(f).Contains(header, StringComparison.OrdinalIgnoreCase)));
            found.ShouldBeTrue($"{header} is on the allow-list but no code under {string.Join(", ", places)} uses it");
        }
    }

    [Fact]
    public void The_guide_makes_no_promise_about_replay_markers_windows_or_retry_after_on_engine_outages()
    {
        var text = GuideText();

        text.ShouldNotContain("X-RateLimit", Case.Insensitive);
        text.ShouldNotContain("Replayed", Case.Insensitive);
        text.ShouldNotContain("24 hours", Case.Insensitive);
        ApiDocsContent.ErrorCodeList.Single(c => c.Code == "FACE_PROVIDER_UNAVAILABLE").Meaning.ShouldNotContain("Retry-After");
    }

    [Fact]
    public void The_csharp_sample_rejects_future_and_malformed_input_and_compares_in_constant_time()
    {
        var code = ApiDocsContent.WebhookVerification.Single(s => s.Language == "C#").Code;

        code.ShouldContain("NumberStyles.None", customMessage: "a non-numeric timestamp is rejected");
        code.ShouldContain("TimeSpan.FromMinutes(-1)", customMessage: "a future timestamp is rejected");
        code.ShouldContain("Convert.TryFromHexString");
        code.ShouldNotContain("Convert.FromHexString(", customMessage: "malformed hex must not throw");
        code.ShouldContain("CryptographicOperations.FixedTimeEquals");
    }

    [Fact]
    public async Task The_javascript_sample_behaves_as_documented()
    {
        if (!NodeAvailable())
        {
            return; // the static checks above still run; node is only needed for this behavioural check
        }

        var dir = Directory.CreateTempSubdirectory("nv-sample-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "sample.mjs"), ApiDocsContent.WebhookVerification.Single(s => s.Language == "JavaScript").Code);
            File.WriteAllText(Path.Combine(dir, "driver.mjs"), """
                import { isValid } from "./sample.mjs";
                import crypto from "node:crypto";
                const sign = (s, t, b) => crypto.createHmac("sha256", s).update(`${t}.${b}`).digest("hex");
                const now = Math.floor(Date.now() / 1000);
                const body = '{"a":1}';
                const ok = (t, sig = sign("secret", t, body), secret = "secret") => isValid(secret, `t=${t},v1=${sig}`, body);
                console.log(JSON.stringify({
                  valid: ok(now),
                  old: ok(now - 3600),
                  future: ok(now + 3600),
                  nonNumeric: ok("abc", "00".repeat(32)),
                  malformedHex: ok(now, "zz".repeat(32)),
                  oddHex: ok(now, "abc"),
                  shortHex: ok(now, "00"),
                  wrongSecret: ok(now, sign("other", now, body)),
                  garbage: isValid("secret", "nonsense", body),
                  tamperedBody: isValid("secret", `t=${now},v1=${sign("secret", now, body)}`, body + " "),
                }));
                """);

            using var process = Process.Start(new ProcessStartInfo("node", "driver.mjs") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.ShouldBe(0, await process.StandardError.ReadToEndAsync());

            var r = JsonDocument.Parse(output).RootElement;
            r.GetProperty("valid").GetBoolean().ShouldBeTrue();
            foreach (var name in new[] { "old", "future", "nonNumeric", "malformedHex", "oddHex", "shortHex", "wrongSecret", "garbage", "tamperedBody" })
            {
                r.GetProperty(name).GetBoolean().ShouldBeFalse(name);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool NodeAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
