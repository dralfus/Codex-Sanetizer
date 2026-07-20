using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

internal sealed record CliRuntime(
    Func<IReadOnlyList<DictionaryTerm>, Sanitizer> SanitizerFactory,
    Func<ManagedPolicyLoadResult> PolicyLoadFactory,
    Func<ManagedPolicyLoadResult, Sanitizer> PolicyTestSanitizerFactory,
    Func<DefaultStorageLayout> LayoutFactory,
    Func<DefaultStorageLayout, LocalRestoreWorkflow> RestoreWorkflowFactory);
