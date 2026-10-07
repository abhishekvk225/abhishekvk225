namespace NexaVerify.Web.Components;

/// <summary>A photo ready to be sent to the API. Held only in memory by the caller; never persisted by the UI.</summary>
public sealed record CapturedImage(byte[] Data, string ContentType, string FileName);
