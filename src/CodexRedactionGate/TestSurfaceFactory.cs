using System;

namespace CodexRedactionGate;

/// <summary>
/// Factory for creating test surface descriptors with common patterns.
/// Eliminates duplicated code for creating TextSurfaceDescriptor with standard metadata.
/// </summary>
public static class TestSurfaceFactory
{
    /// <summary>
    /// Creates a native submit surface for testing.
    /// </summary>
    /// <param name="profileId">The profile ID for the surface.</param>
    /// <returns>A TextSurfaceDescriptor with standard test metadata.</returns>
    public static TextSurfaceDescriptor CreateNativeSubmitSurface(string profileId)
    {
        var metadata = new SurfaceMetadata(
            SurfaceKind: "test",
            ComposerStatus: OsInteractionStatusIds.SupportedComposer);

        return new TextSurfaceDescriptor(
            SurfaceId: $"native-submit-test:{profileId}",
            ProfileId: profileId,
            DisplayName: profileId,
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: metadata);
    }

    /// <summary>
    /// Creates a smoke test surface for native submit testing.
    /// </summary>
    /// <param name="profileId">The profile ID for the surface.</param>
    /// <returns>A TextSurfaceDescriptor with smoke test metadata.</returns>
    public static TextSurfaceDescriptor CreateSmokeNativeSubmitSurface(string profileId)
    {
        var metadata = new SurfaceMetadata(
            SurfaceKind: "disposable_local_target",
            CloudSubmission: "false");

        return new TextSurfaceDescriptor(
            SurfaceId: $"product-smoke-native-profile:{profileId}",
            ProfileId: profileId,
            DisplayName: profileId,
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: metadata);
    }

    /// <summary>
    /// Creates a test surface with custom metadata.
    /// </summary>
    /// <param name="profileId">The profile ID for the surface.</param>
    /// <param name="surfaceKind">The surface kind (optional).</param>
    /// <param name="cloudSubmission">Whether cloud submission is enabled (optional).</param>
    /// <param name="composerStatus">The composer status (optional, defaults to SupportedComposer).</param>
    /// <returns>A TextSurfaceDescriptor with the specified metadata.</returns>
    public static TextSurfaceDescriptor CreateTestSurface(
        string profileId,
        string? surfaceKind = null,
        string? cloudSubmission = null,
        string? composerStatus = null)
    {
        var metadata = new SurfaceMetadata(
            SurfaceKind: surfaceKind,
            CloudSubmission: cloudSubmission,
            ComposerStatus: composerStatus ?? OsInteractionStatusIds.SupportedComposer);

        return new TextSurfaceDescriptor(
            SurfaceId: $"test-surface:{profileId}",
            ProfileId: profileId,
            DisplayName: profileId,
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: metadata);
    }

    /// <summary>
    /// Updates a surface with new metadata while preserving other properties.
    /// </summary>
    /// <param name="surface">The original surface.</param>
    /// <param name="surfaceKind">The new surface kind (optional).</param>
    /// <param name="cloudSubmission">Whether cloud submission is enabled (optional).</param>
    /// <param name="composerStatus">The composer status (optional).</param>
    /// <returns>A new TextSurfaceDescriptor with updated metadata.</returns>
    public static TextSurfaceDescriptor UpdateSurface(
        TextSurfaceDescriptor surface,
        string? surfaceKind = null,
        string? cloudSubmission = null,
        string? composerStatus = null)
    {
        var metadata = new SurfaceMetadata(
            SurfaceKind: surfaceKind ?? surface.Metadata.SurfaceKind,
            CloudSubmission: cloudSubmission ?? surface.Metadata.CloudSubmission,
            ComposerStatus: composerStatus ?? surface.Metadata.ComposerStatus);

        return surface with { Metadata = metadata };
    }
}
