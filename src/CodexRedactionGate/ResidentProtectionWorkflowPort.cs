using System;

namespace CodexRedactionGate;

internal sealed record ResidentWorkflowActionRequest(
    string ActionKind,
    string InitialStage,
    bool UserInputRequired,
    string NextAction);

internal readonly record struct ResidentWorkflowAttempt(
    string Kind,
    long AttemptId)
{
    internal static ResidentWorkflowAttempt Operational(long attemptId) =>
        new("operational", attemptId);

    internal static ResidentWorkflowAttempt SetupVerification(long attemptId) =>
        new("setup_verification", attemptId);
}

internal enum ResidentWorkflowPublicationKind
{
    OperationalStage,
    OperationalCompleted,
    OperationalCancelled,
    SetupVerificationProgress,
    PromptProtectionRetryStarted,
    PromptProtectionRetrySucceeded,
    PromptProtectionRetryFailed,
    LocalProtectionStatus,
    LocalProtectionReady,
    ResidentReadinessProof
}

internal sealed record ResidentWorkflowPublication
{
    private ResidentWorkflowPublication(
        ResidentWorkflowPublicationKind kind,
        string? stage = null,
        bool userInputRequired = false,
        string nextAction = "none",
        string? outcomeCode = null,
        PromptProtectionSetupProgress? setupProgress = null,
        string? localProtectionStatus = null,
        long attemptId = 0)
    {
        Kind = kind;
        Stage = stage;
        UserInputRequired = userInputRequired;
        NextAction = nextAction;
        OutcomeCode = outcomeCode;
        SetupProgress = setupProgress;
        LocalProtectionStatus = localProtectionStatus;
        AttemptId = attemptId;
    }

    internal ResidentWorkflowPublicationKind Kind { get; }

    internal string? Stage { get; }

    internal bool UserInputRequired { get; }

    internal string NextAction { get; }

    internal string? OutcomeCode { get; }

    internal PromptProtectionSetupProgress? SetupProgress { get; }

    internal string? LocalProtectionStatus { get; }

    internal long AttemptId { get; }

    internal static ResidentWorkflowPublication ForStage(
        string stage,
        bool userInputRequired,
        string nextAction) => new(
            ResidentWorkflowPublicationKind.OperationalStage,
            stage: RequireText(stage, nameof(stage)),
            userInputRequired: userInputRequired,
            nextAction: RequireText(nextAction, nameof(nextAction)));

    internal static ResidentWorkflowPublication Completed(
        string outcomeCode,
        string nextAction) => new(
            ResidentWorkflowPublicationKind.OperationalCompleted,
            outcomeCode: RequireText(outcomeCode, nameof(outcomeCode)),
            nextAction: RequireText(nextAction, nameof(nextAction)));

    internal static ResidentWorkflowPublication Cancelled() => new(
        ResidentWorkflowPublicationKind.OperationalCancelled,
        outcomeCode: "cancelled",
        nextAction: "none");

    internal static ResidentWorkflowPublication Setup(
        PromptProtectionSetupProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return new(
            ResidentWorkflowPublicationKind.SetupVerificationProgress,
            setupProgress: progress,
            attemptId: progress.AttemptId);
    }

    internal static ResidentWorkflowPublication RetryStarted(long attemptId) =>
        ForAttempt(ResidentWorkflowPublicationKind.PromptProtectionRetryStarted, attemptId);

    internal static ResidentWorkflowPublication RetrySucceeded(long attemptId) =>
        ForAttempt(ResidentWorkflowPublicationKind.PromptProtectionRetrySucceeded, attemptId);

    internal static ResidentWorkflowPublication RetryFailed(long attemptId) =>
        ForAttempt(ResidentWorkflowPublicationKind.PromptProtectionRetryFailed, attemptId);

    internal static ResidentWorkflowPublication LocalProtection(
        string status,
        long attemptId = 0) => new(
            ResidentWorkflowPublicationKind.LocalProtectionStatus,
            localProtectionStatus: RequireText(status, nameof(status)),
            attemptId: RequireOptionalAttempt(attemptId));

    internal static ResidentWorkflowPublication LocalReady(long attemptId) =>
        ForAttempt(ResidentWorkflowPublicationKind.LocalProtectionReady, attemptId);

    internal static ResidentWorkflowPublication ReadinessProof() => new(
        ResidentWorkflowPublicationKind.ResidentReadinessProof);

    private static ResidentWorkflowPublication ForAttempt(
        ResidentWorkflowPublicationKind kind,
        long attemptId) => new(kind, attemptId: RequireAttempt(attemptId));

    private static long RequireAttempt(long attemptId)
    {
        if (attemptId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptId), "A resident workflow attempt is required.");
        }

        return attemptId;
    }

    private static long RequireOptionalAttempt(long attemptId)
    {
        if (attemptId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptId));
        }

        return attemptId;
    }

    private static string RequireText(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty workflow value is required.", parameterName)
            : value;
    }
}

/// <summary>
/// Resident workflow seam. It exposes only the state and lifecycle operations
/// needed by the coordinator; tray-only controls remain on IResidentProtectionRuntime.
/// </summary>
internal interface IResidentProtectionWorkflowPort
{
    TrayProtectionState State { get; }

    OperationalActionState OperationalAction { get; }

    bool Start();

    void Stop();

    void EnableResidentReadinessAdmission();

    OperationalActionStartResult StartAction(ResidentWorkflowActionRequest request);

    IDisposable? TryAcquireAttempt(ResidentWorkflowAttempt attempt);

    bool Publish(
        ResidentWorkflowPublication publication,
        long expectedAttemptId = 0);

    bool IsCurrent(ResidentWorkflowAttempt attempt);

    void RefreshOperationalState();

    bool Reload(NativeSubmitRuntimeSet candidateRuntimeSet);

    bool Reload(NativeSubmitRuntimeSet candidateRuntimeSet, long expectedAttemptId);

    bool Reload(ResidentProtectionRuntime candidateRuntime);

    bool Reload(ResidentProtectionRuntime candidateRuntime, long expectedAttemptId);
}
