# 01 — Prove a supported live ingress boundary for protected project files

**Ticket ID:** 283

**What to build:** Establish a real pre-cloud boundary for supported project-file reads, attachments, and file-derived tool output. If the selected desktop surface has no supported boundary, keep the capability explicitly unsupported.

**Blocked by:** 282 — Separate broker evidence from live project-file protection status; provide a verified supported desktop integration surface or an approved local gateway.

**Status:** blocked

**State owner:** The supported pre-cloud ingress boundary owns admission and protection state; local UI and persisted records only project it.

**Fail-closed state:** An unavailable or unclassifiable boundary reports `unsupported`; it never claims `project_files_protected` and never silently allows an unmanaged channel through the sanitizer.

**Allowed transitions:** `unverified -> supported -> protected`, or `unverified -> unsupported` when no supported boundary exists.

**Deterministic proof:** A disposable protected workspace exercises one file-context operation and asserts a raw-free sanitized model-visible payload, plus the explicit unsupported outcome when the boundary is unavailable.

**Red-capable reproduction:** Run the file-context operation while the desktop surface has no supported pre-cloud ingress. The old local-broker-only evidence must not produce a protected claim.

**Highest required seam:** A verified supported pre-cloud desktop boundary or an approved gateway that owns the operation before cloud visibility.

**Evidence target:** `live_verified` ingress evidence bound to the supported surface; otherwise an explicit unsupported result.

- [ ] A disposable protected workspace demonstrates one real pre-cloud file-context operation and records raw-free sanitized evidence.
- [x] When the boundary is unavailable, local attachment and unmanaged-connector channels fail closed as `unsupported`.
- [ ] The user receives one operation-level batch summary with a navigable per-file list, not one confirmation dialog per file.
- [x] `project_files_protected` remains false until real ingress proof and its regression test exist.
