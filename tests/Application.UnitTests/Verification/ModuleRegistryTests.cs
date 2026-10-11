using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.UnitTests.Verification;

public class ModuleRegistryTests
{
    private sealed class Settings : IVerificationModuleSettingReader
    {
        public Dictionary<string, bool> Rows { get; } = [];

        public Task<bool?> IsEnabledAsync(string moduleKey, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(Rows.TryGetValue(moduleKey, out var v) ? v : null);
    }

    private static VerificationModuleRegistry Registry(Settings? settings = null, IEnumerable<VerificationModuleDescriptor>? descriptors = null) =>
        new((descriptors ?? ModuleCatalogue.All).Select(d => new CatalogueModule(d)), settings ?? new Settings());

    [Fact]
    public void Catalogue_has_three_available_and_six_upcoming_modules_in_display_order()
    {
        var all = Registry().All;

        all.Count(d => d.Status == ModuleStatus.Available).ShouldBe(3);
        all.Count(d => d.Status == ModuleStatus.Upcoming).ShouldBe(6);
        all.Select(d => d.DisplayOrder).ShouldBe(all.Select(d => d.DisplayOrder).Order());
        all.Select(d => d.Key).Where(k => VerificationModuleKeys.Upcoming.Contains(k)).Order()
            .ShouldBe(VerificationModuleKeys.Upcoming.Order());
        all.Take(3).Select(d => d.Key).ShouldBe(VerificationModuleKeys.Available);
    }

    [Fact]
    public void Upcoming_modules_carry_no_type_permission_operation_or_provider()
    {
        foreach (var d in ModuleCatalogue.Upcoming)
        {
            d.Status.ShouldBe(ModuleStatus.Upcoming);
            d.Type.ShouldBeNull();
            d.SubmitPermissions.ShouldBeEmpty();
            d.CostOperations.ShouldBeEmpty();
            d.ProviderIds.ShouldBeEmpty();
            d.PlannedNote.ShouldNotBeNull();
        }
    }

    [Fact]
    public void Available_modules_use_real_permissions_and_operations_of_their_own_type()
    {
        var known = Permissions.All.Select(p => p.Key).ToHashSet();
        foreach (var d in ModuleCatalogue.Available)
        {
            d.SubmitPermissions.ShouldAllBe(p => known.Contains(p));
            d.CostOperations.ShouldAllBe(op => VerificationOperations.IsVerificationOperation(op));
            foreach (var stage in Enum.GetValues<VerificationStage>())
            {
                if (VerificationOperations.For(d.Type!.Value, stage) is { } op)
                {
                    d.CostOperations.ShouldContain(op);
                }
            }
        }

        ModuleCatalogue.Available.SelectMany(d => d.CostOperations).Distinct().Count().ShouldBe(5);
    }

    [Fact]
    public void Find_by_key_and_type()
    {
        var registry = Registry();

        registry.Find("PAN")!.Type.ShouldBe(VerificationType.Pan);
        registry.Find(VerificationType.Passport)!.Key.ShouldBe("passport");
        registry.Find("aadhaar")!.Status.ShouldBe(ModuleStatus.Upcoming);
        registry.Find("nope").ShouldBeNull();
    }

    [Fact]
    public void Duplicate_keys_fail_start_up()
    {
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [.. ModuleCatalogue.All, ModuleCatalogue.Pan with { Type = null, Status = ModuleStatus.Upcoming, SubmitPermissions = [], CostOperations = [], ProviderIds = [] }]));
    }

    [Fact]
    public void An_upcoming_descriptor_that_carries_an_operation_or_permission_is_rejected()
    {
        var bad = ModuleCatalogue.Upcoming[0];
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [bad with { CostOperations = [MeteredOperation.PanExtraction] }]));
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [bad with { SubmitPermissions = [Permissions.Verification.PanSubmit] }]));
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [bad with { Type = VerificationType.Pan }]));
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [bad with { ProviderIds = ["x"] }]));
    }

    [Fact]
    public void An_available_descriptor_needs_type_permission_and_operation()
    {
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [ModuleCatalogue.Pan with { Type = null }]));
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [ModuleCatalogue.Pan with { SubmitPermissions = [] }]));
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [ModuleCatalogue.Pan with { CostOperations = [] }]));
    }

    [Fact]
    public void Two_modules_for_one_type_are_rejected()
    {
        Should.Throw<InvalidOperationException>(() => Registry(descriptors: [ModuleCatalogue.Pan, ModuleCatalogue.Pan with { Key = "pan-2" }]));
    }

    [Fact]
    public async Task Effective_status_follows_the_platform_switch_and_never_promotes_upcoming()
    {
        var settings = new Settings();
        var registry = Registry(settings);

        (await registry.GetEffectiveStatusAsync("pan", default)).ShouldBe(ModuleStatus.Available); // no row = never switched off
        settings.Rows["pan"] = false;
        (await registry.GetEffectiveStatusAsync("pan", default)).ShouldBe(ModuleStatus.Disabled);
        settings.Rows["pan"] = true;
        (await registry.GetEffectiveStatusAsync("pan", default)).ShouldBe(ModuleStatus.Available);

        settings.Rows["aadhaar"] = true; // even a stray row cannot enable an Upcoming module
        (await registry.GetEffectiveStatusAsync("aadhaar", default)).ShouldBe(ModuleStatus.Upcoming);
        (await registry.GetEffectiveStatusAsync("unknown", default)).ShouldBe(ModuleStatus.Disabled);
    }

    [Fact]
    public async Task Gate_admits_enabled_modules_and_answers_404_for_disabled_ones()
    {
        var settings = new Settings();
        var gate = new ModuleGate(Registry(settings));

        var ok = await gate.EnsureAvailableAsync(VerificationType.BankStatement, default);
        ok.IsSuccess.ShouldBeTrue();
        ok.Value.Key.ShouldBe("bank-statement");

        settings.Rows["bank-statement"] = false;
        var off = await gate.EnsureAvailableAsync(VerificationType.BankStatement, default);
        off.IsFailure.ShouldBeTrue();
        off.Error!.Code.ShouldBe(ErrorCodes.VerificationModuleDisabled);
        off.Error.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task Gate_refuses_a_type_without_a_registered_module()
    {
        var gate = new ModuleGate(Registry(descriptors: ModuleCatalogue.Upcoming));

        (await gate.EnsureAvailableAsync(VerificationType.Pan, default)).IsFailure.ShouldBeTrue();
    }
}
