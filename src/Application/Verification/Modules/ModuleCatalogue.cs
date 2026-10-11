using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Modules;

/// <summary>The code-defined module catalogue (docs/07 §3). Adding a module = a new entry here, flipped from Upcoming to Available, plus its three pieces.</summary>
public static class ModuleCatalogue
{
    public const string UpcomingNote = "Planned";

    public static VerificationModuleDescriptor BankStatement { get; } = new(
        VerificationModuleKeys.BankStatement, "Bank Statement Verification",
        "Extracts and checks account details, balances and transactions from a bank statement.",
        ModuleStatus.Available, 10, VerificationType.BankStatement,
        [Permissions.Verification.BankStatementSubmit], [MeteredOperation.BankStatementExtraction], [], null);

    public static VerificationModuleDescriptor Pan { get; } = new(
        VerificationModuleKeys.Pan, "PAN Verification",
        "Checks the format of a PAN, reads it from a card image or PDF and compares it with the details you supply.",
        ModuleStatus.Available, 20, VerificationType.Pan,
        [Permissions.Verification.PanSubmit], [MeteredOperation.PanExtraction, MeteredOperation.PanProviderCheck], ["pan"], null);

    public static VerificationModuleDescriptor Passport { get; } = new(
        VerificationModuleKeys.Passport, "Passport Verification",
        "Reads a passport, validates its machine-readable zone and compares it with the details you supply.",
        ModuleStatus.Available, 30, VerificationType.Passport,
        [Permissions.Verification.PassportSubmit], [MeteredOperation.PassportExtraction, MeteredOperation.PassportProviderCheck], ["passport"], null);

    public static IReadOnlyList<VerificationModuleDescriptor> Available { get; } = [BankStatement, Pan, Passport];

    /// <summary>Catalogue only: no type, no permission, no operation, no provider, no route.</summary>
    public static IReadOnlyList<VerificationModuleDescriptor> Upcoming { get; } =
    [
        Planned(VerificationModuleKeys.Aadhaar, "Aadhaar Verification", "Verification of Aadhaar identity documents.", 100),
        Planned(VerificationModuleKeys.DrivingLicence, "Driving Licence Verification", "Verification of driving licences.", 110),
        Planned(VerificationModuleKeys.VoterId, "Voter ID Verification", "Verification of voter identity cards.", 120),
        Planned(VerificationModuleKeys.DigiLocker, "DigiLocker Integration", "Fetching documents from DigiLocker with the holder's consent.", 130),
        Planned(VerificationModuleKeys.GovernmentDocuments, "Additional Government Document Verification", "Verification of further government-issued documents.", 140),
        Planned(VerificationModuleKeys.KycFraud, "Additional KYC and fraud detection", "Further KYC checks and fraud-detection capabilities.", 150),
    ];

    public static IReadOnlyList<VerificationModuleDescriptor> All { get; } = [.. Available, .. Upcoming];

    private static VerificationModuleDescriptor Planned(string key, string name, string description, int order) =>
        new(key, name, description, ModuleStatus.Upcoming, order, null, [], [], [], UpcomingNote);
}

/// <summary>Adapts a catalogue descriptor to <see cref="IVerificationModule"/> so the registry is fed uniformly.</summary>
public sealed class CatalogueModule : IVerificationModule
{
    public CatalogueModule(VerificationModuleDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public VerificationModuleDescriptor Descriptor { get; }
}
