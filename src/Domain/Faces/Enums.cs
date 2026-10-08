namespace NexaVerify.Domain.Faces;

public enum FaceProfileStatus
{
    Active = 0,
    Disabled,
}

public enum TemplateStatus
{
    Active = 0,
    Superseded,
}

public enum RecognitionSource
{
    Api = 0,
    Portal,
}

public enum RecognitionStatus
{
    Completed = 0,
    Failed,
}

/// <summary>Stable outcome vocabulary of the public API (docs/03 §5).</summary>
public enum RecognitionOutcome
{
    Enrolled = 0,
    Matched,
    NoMatch,
    NoFaceDetected,
    MultipleFaces,
    LowQuality,
    ProviderError,
    Rejected,
}
