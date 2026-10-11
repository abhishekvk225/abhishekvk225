namespace NexaVerify.Contracts.Verification;

/// <summary>Stable module keys (URL/DB keys). The first three are Available in release 1; the rest are catalogue-only (Upcoming) and have no route, permission or operation.</summary>
public static class VerificationModuleKeys
{
    public const string BankStatement = "bank-statement";
    public const string Pan = "pan";
    public const string Passport = "passport";

    public const string Aadhaar = "aadhaar";
    public const string DrivingLicence = "driving-licence";
    public const string VoterId = "voter-id";
    public const string DigiLocker = "digilocker";
    public const string GovernmentDocuments = "govt-documents";
    public const string KycFraud = "kyc-fraud";

    public static IReadOnlyList<string> Available { get; } = [BankStatement, Pan, Passport];

    public static IReadOnlyList<string> Upcoming { get; } = [Aadhaar, DrivingLicence, VoterId, DigiLocker, GovernmentDocuments, KycFraud];
}

/// <summary>Wire values of the module status in the catalogue.</summary>
public static class VerificationModuleStatuses
{
    public const string Available = "Available";
    public const string Upcoming = "Upcoming";
    public const string Disabled = "Disabled";
}

/// <summary>Wire values of <c>VerificationDownloadRequest.Kind</c> and report formats.</summary>
public static class VerificationDownloadKinds
{
    public const string Report = "Report";
    public const string TransactionsCsv = "TransactionsCsv";
    public const string OriginalDocument = "OriginalDocument";

    public const string Json = "json";
    public const string Html = "html";
}
