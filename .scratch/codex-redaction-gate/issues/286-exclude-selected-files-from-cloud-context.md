# 02 — Exclude selected files, including `.env`, from cloud file context

**Ticket ID:** 286

**What to build:** Let the user select exact local files for an `exclude from cloud` policy and withhold their contents and file-derived representations before model visibility, while showing the local policy outcome.

**Blocked by:** 01 — Prove a supported live ingress boundary for protected project files; 285 — Show local protection capabilities and active state in the tray UI.

**Status:** blocked

**State owner:** The file-exclusion policy and supported ingress boundary own the enforcement decision; UI and diagnostics project it.

**Fail-closed state:** A missing, unhealthy, or unclassifiable ingress boundary reports `unsupported` or `degraded` and never claims an exclusion is enforced.

**Allowed transitions:** `selected -> classified -> withheld`, or `selected -> unsupported/degraded`; an excluded file never transitions to model-visible content.

**Deterministic proof:** Synthetic `.env` and arbitrary-file fixtures cover reads, attachments, diffs, patches, and file-derived output without live cloud access.

**Red-capable reproduction:** Keep a selected file locally writable while bypassing the ingress boundary. The test must fail if any raw content, path, filename, or attachment representation reaches the model-visible record.

**Highest required seam:** The supported pre-cloud ingress boundary that handles every file-context path.

**Evidence target:** `locally_verified` exclusion matrix followed by `live_verified` only after the boundary is proven.

- [ ] The local UI can add, review, and remove exact exclusions, including `.env`; persisted and cloud-bound diagnostics remain protected or raw-free.
- [ ] Every supported path that could expose an excluded file is blocked before model visibility with `file_excluded_from_cloud`.
- [ ] The operation-level status view identifies the withheld file policy while keeping the rest of the batch usable.
- [ ] Missing or unhealthy ingress fails closed as `unsupported` or `degraded`.
- [ ] Synthetic fixtures prove no raw contents, paths, or filenames reach model-visible payloads, audit output, or cloud-bound diagnostics.
