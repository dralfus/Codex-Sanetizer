using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class ProtectedComposerSessionTests
{
    [Test]
    public void ReferenceSession_ReadWriteVerifyAndReplay_UsesOneTargetScopedContract()
    {
        var surface = CreateSurface("reference");
        var fixture = new DeterministicComposerFixture(surface, "original");
        var session = new ReferenceProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        var read = session.Read();
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(read.Succeeded, Is.True);
        Assert.That(read.Text, Is.EqualTo("original"));
        Assert.That(write.Succeeded, Is.True);
        Assert.That(write.Diagnostics["verification_exact"], Is.EqualTo("true"));
        Assert.That(replay.Succeeded, Is.True);
        Assert.That(fixture.CurrentText, Is.EqualTo("sanitized"));
        Assert.That(fixture.WriteCount, Is.EqualTo(1));
        Assert.That(fixture.SubmitCount, Is.EqualTo(1));
    }

    [Test]
    public void Session_TargetChangesBeforeWrite_FailsClosedBeforeWriter()
    {
        var initial = CreateSurface("initial");
        var changed = CreateSurface("changed");
        var fixture = new DeterministicComposerFixture(initial, "original", changed);
        var session = new ReferenceProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Succeeded, Is.False);
        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.StaleComposer));
        Assert.That(fixture.WriteCount, Is.Zero);
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(fixture.SubmitCount, Is.Zero);
    }

    [Test]
    public void Session_ForegroundRefusalDuringRevalidation_FailsClosedBeforeWriter()
    {
        var surface = CreateSurface("foreground-refusal");
        var fixture = new DeterministicComposerFixture(surface, "original")
        {
            FailDiscoveryAfterRead = true
        };
        var session = new ReferenceProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var target = session.Revalidate();
        var write = session.WriteAndVerify("sanitized");

        Assert.That(target.Succeeded, Is.False);
        Assert.That(target.Status, Is.EqualTo(OsInteractionStatusIds.FocusLost));
        Assert.That(write.Succeeded, Is.False);
        Assert.That(fixture.WriteCount, Is.Zero);
    }

    [Test]
    public void Session_WriteVerificationMismatch_FailsClosedAndCannotReplay()
    {
        var surface = CreateSurface("mismatch");
        var fixture = new DeterministicComposerFixture(surface, "original")
        {
            ReturnWrongTextAfterWrite = true
        };
        var session = new ReferenceProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Succeeded, Is.False);
        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.VerificationFailed));
        Assert.That(write.Diagnostics["verification_exact"], Is.EqualTo("false"));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(fixture.SubmitCount, Is.Zero);
    }

    [Test]
    public void Session_ReplayFailure_IsTerminalAndDoesNotRepeatSideEffect()
    {
        var surface = CreateSurface("replay-failure");
        var fixture = new DeterministicComposerFixture(surface, "original")
        {
            ReplaySucceeded = false
        };
        var session = new ReferenceProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var firstReplay = session.Replay();
        var secondReplay = session.Replay();

        Assert.That(firstReplay.Succeeded, Is.False);
        Assert.That(firstReplay.Status, Is.EqualTo(OsInteractionStatusIds.ReplayIndeterminate));
        Assert.That(secondReplay.Succeeded, Is.False);
        Assert.That(secondReplay.Status, Is.EqualTo("invalid_transition"));
        Assert.That(fixture.SubmitCount, Is.EqualTo(1));
    }

    [Test]
    public void Session_AccessException_IsTypedFailureAndCannotContinue()
    {
        var surface = CreateSurface("exception");
        var fixture = new DeterministicComposerFixture(surface, "original")
        {
            ThrowOnWrite = true
        };
        var session = new ReferenceProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Succeeded, Is.False);
        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.WriteFailed));
        Assert.That(write.Diagnostics["failed_closed"], Is.EqualTo("true"));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(fixture.SubmitCount, Is.Zero);
    }

    [Test]
    public void WindowsSessionFactory_UsesVerifiedComposerAdapterWithSameContract()
    {
        var surface = CreateWindowsSurface();
        var access = new VerifiedComposerFixture("original");
        var adapter = new WindowsVerifiedComposerSurfaceAdapter(access);
        var discovery = new FixedSurfaceDiscovery(surface);
        var session = new WindowsProtectedComposerSessionFactory(discovery, adapter).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        Assert.That(session.WriteAndVerify("sanitized").Succeeded, Is.True);
        Assert.That(session.Replay().Succeeded, Is.True);
        Assert.That(access.CurrentText, Is.EqualTo("sanitized"));
        Assert.That(access.SubmitCount, Is.EqualTo(1));
    }

    private static TextSurfaceDescriptor CreateSurface(string id)
    {
        return new TextSurfaceDescriptor(
            $"session:{id}",
            "reference-ai",
            "Reference AI",
            true,
            true,
            true,
            true,
            new SurfaceMetadata(SurfaceKind: "reference"));
    }

    private static TextSurfaceDescriptor CreateWindowsSurface()
    {
        return new TextSurfaceDescriptor(
            "session:windows",
            "codex-desktop",
            "OpenAI Desktop",
            true,
            true,
            true,
            true,
            new SurfaceMetadata(
                SurfaceKind: "windows",
                ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                ArbitraryMetadata: new Dictionary<string, string>
                {
                    ["focused_element_hash"] = "verified-element"
                }));
    }

    private sealed class DeterministicComposerFixture :
        IActiveTextSurfaceDiscovery,
        ITextSurfaceReader,
        ITextSurfaceWriter,
        ISubmitAction
    {
        private readonly TextSurfaceDescriptor _initialSurface;
        private readonly TextSurfaceDescriptor? _nextSurface;

        public DeterministicComposerFixture(
            TextSurfaceDescriptor initialSurface,
            string initialText,
            TextSurfaceDescriptor? nextSurface = null)
        {
            _initialSurface = initialSurface;
            _nextSurface = nextSurface;
            CurrentText = initialText;
        }

        public string CurrentText { get; private set; }

        public int WriteCount { get; private set; }

        public int SubmitCount { get; private set; }

        public bool ReturnWrongTextAfterWrite { get; init; }

        public bool ReplaySucceeded { get; init; } = true;

        public bool ThrowOnWrite { get; init; }

        public bool FailDiscoveryAfterRead { get; init; }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface()
        {
            if (FailDiscoveryAfterRead && DiscoveryCount > 0)
            {
                DiscoveryCount++;
                return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
            }

            var surface = _nextSurface is not null && WriteCount == 0 && SubmitCount == 0 && DiscoveryCount > 0
                ? _nextSurface
                : _initialSurface;
            DiscoveryCount++;
            return TextSurfaceDiscoveryResult.Success(surface);
        }

        public int DiscoveryCount { get; private set; }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
        {
            var text = ReturnWrongTextAfterWrite && WriteCount > 0 ? "unexpected" : CurrentText;
            return new TextCaptureResult(true, "captured", text, new Dictionary<string, string>());
        }

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("test write failure");
            }

            CurrentText = text;
            WriteCount++;
            return new TextReplacementResult(true, OsInteractionStatusIds.Applied, new Dictionary<string, string>());
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            SubmitCount++;
            return new SubmitActionResult(
                ReplaySucceeded,
                ReplaySucceeded ? OsInteractionStatusIds.Submitted : OsInteractionStatusIds.ReplayIndeterminate,
                new Dictionary<string, string>());
        }
    }

    private sealed class FixedSurfaceDiscovery : IActiveTextSurfaceDiscovery
    {
        private readonly TextSurfaceDescriptor _surface;

        public FixedSurfaceDiscovery(TextSurfaceDescriptor surface)
        {
            _surface = surface;
        }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface() =>
            TextSurfaceDiscoveryResult.Success(_surface);
    }

    private sealed class VerifiedComposerFixture : IVerifiedComposerTextAccess
    {
        public VerifiedComposerFixture(string initialText)
        {
            CurrentText = initialText;
        }

        public string CurrentText { get; private set; }

        public int SubmitCount { get; private set; }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface) =>
            new(true, "captured", CurrentText, new Dictionary<string, string>());

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            CurrentText = text;
            return new TextReplacementResult(true, OsInteractionStatusIds.Applied, new Dictionary<string, string>());
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            SubmitCount++;
            return new SubmitActionResult(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string>());
        }
    }
}
