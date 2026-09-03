# Existing Solutions Review

Date: 2026-07-15

## Goal

Find existing open-source projects that can reduce the amount of new code needed for Codex Redaction Gate.

The target capability is specific:

- intercept prompt before cloud submission;
- detect secrets, PII, internal infrastructure identifiers and business names;
- replace sensitive values with stable placeholders;
- keep mappings local;
- optionally restore placeholders locally;
- work with Codex guard mode and later gateway mode.

## Short Answer

There is no mature drop-in open-source project that exactly implements "Codex pre-submit redaction gate with local stable vault and Codex hook integration".

The best path is composition:

1. Use or study `llm-redactor` for LLM-specific scrub/restore/proxy ideas.
2. Use a source-built Gitleaks binary as the MVP secret scanner backend.
3. Use built-in technical scanners plus CSV dictionaries and TOML policy for internal identifiers and organization-specific terms.
4. Keep Presidio and TruffleHog as later candidates, not MVP dependencies.
5. Build our own Codex hook/gateway adapter, policy layering, stable HMAC mapping vault and `Confirm sanitized prompt` UX.

## Candidate Matrix

| Project | What it gives | Fit | Main concern |
| --- | --- | --- | --- |
| `jayluxferro/llm-redactor` | LLM request shim, scrub/restore tools, placeholders, MCP/OpenAI-compatible flow, Burp proxy option | Closest conceptual match | Very young: 0 stars, no releases at review time |
| `data-privacy-stack/presidio` | Mature PII detection, anonymization, custom recognizers, text/image/structured data support | Best detector/anonymizer foundation | Not LLM/Codex-specific; does not solve prompt interception or stable vault by itself |
| `gitleaks/gitleaks` | Mature secret scanning, config rules, stdin mode, redacted output | Good secret detector for MVP/prototype | Built for files/repos/stdin, not span-based prompt replacement API |
| `trufflesecurity/trufflehog` | Large credential detector set, active verification, many source scanners, stdin support | Strong secret detection reference | Verification can call external APIs; not ideal for offline prompt sanitizer unless disabled |
| `protectai/llm-guard` | LLM prompt/output scanners: anonymize, regex, secrets, prompt injection, sensitive output | Useful design reference | Archived on 2026-07-09; not a good long-term dependency |
| `LeapBeyond/scrubadub` | Simple configurable PII scrubbing for free text | Lightweight fallback | Narrower and less enterprise-ready than Presidio |
| `BerriAI/litellm` | Mature AI gateway/proxy, OpenAI-compatible API, guardrails support | Useful if we need API proxy mode | Does not intercept Codex desktop prompt composer; heavier than needed |
| `meta-llama/PurpleLlama/LlamaFirewall` | Prompt injection/security scanners, regex/custom scanners, CodeShield | Useful adjacent security layer | Focused on prompt injection/agent security, not privacy redaction/vault |
| Casper paper | Browser extension architecture for prompt sanitization | Good product-pattern reference | Research paper; GitHub repo not found in quick review |

## Most Relevant Projects

### 1. llm-redactor

Repository: `https://github.com/jayluxferro/llm-redactor`

Why it matters:

- It is explicitly about privacy-preserving outbound LLM requests.
- It sits between an agent and an LLM endpoint.
- It supports scrub, restore, detect and stats style tools.
- It has an MCP-style workflow and OpenAI-compatible request flow.
- It includes a Burp proxy extension for outbound body redaction.

Useful ideas to borrow:

- `redact.scrub(text) -> redacted_text + session_id`
- `redact.restore(text, session_id) -> restored_text`
- per-request/session placeholder binding
- explicit dry-run detection mode
- proxy mode as an integration option

Concerns:

- At review time the repository had 0 stars and no releases.
- It is research/prototype-shaped, not a proven enterprise dependency.
- The placeholder/vault behavior must be reviewed before trusting it with sensitive mappings.

Recommendation:

Study first. Possibly fork small parts later. Do not make it the only foundation until code quality, tests, storage model and maintenance posture are reviewed locally.

### 2. Presidio

Repository: `https://github.com/data-privacy-stack/presidio`

Why it matters:

- It is a mature open-source framework for detecting, redacting, masking and anonymizing sensitive data.
- It supports NLP, pattern matching and customizable pipelines.
- It has custom recognizers and multiple deployment options.
- It is much more mature than most LLM-specific redaction repos.

Useful ideas/components:

- analyzer engine for PII detection;
- anonymizer engine for replacements;
- custom recognizers for company-specific terms;
- confidence scores and context-aware recognizers;
- Docker/Python integration options.

Concerns:

- It is PII-first. It does not know our internal domains, project names, repository paths or Codex workflow by default.
- Built-in anonymization is not the same as our stable HMAC pseudonym vault.
- It warns that automated detection cannot guarantee finding all sensitive data.

Recommendation:

Use as the main PII detector/anonymizer dependency if Python is acceptable. Add custom recognizers and wrap output in our own span resolver, policy engine and mapping vault.

### 3. Gitleaks

Repository: `https://github.com/gitleaks/gitleaks`

Why it matters:

- Mature and popular secret detector.
- Can scan stdin, directories and git repositories.
- Supports configurable rules and redacted output.
- Good fit for detecting tokens/passwords/API keys in pasted logs/config snippets.

Useful ideas/components:

- rule format and entropy heuristics;
- stdin scanning for quick MVP;
- TOML config model;
- redaction controls.

Concerns:

- It is a detector, not a sanitizer.
- It is file/repo oriented and reports findings rather than returning exact replacement plans for prompt rendering.
- It may be simpler to embed selected rules than to shell out forever.

Recommendation:

Use in early prototypes as an external secret scanner, then decide whether to embed a subset of rules or keep it as optional scanner backend.

### 4. TruffleHog

Repository: `https://github.com/trufflesecurity/trufflehog`

Why it matters:

- Very broad credential detector set.
- Active verification reduces false positives.
- Supports many data sources and stdin.

Useful ideas/components:

- detector catalog;
- verified/unverified/unknown result model;
- stdin scanning mode.

Concerns:

- Active verification may call external APIs, which conflicts with offline/local-only redaction unless explicitly disabled.
- Heavier operationally than Gitleaks for prompt-time scanning.

Recommendation:

Use as a reference or optional offline-only backend. Do not enable network verification in the prompt sanitizer path.

### 5. LLM Guard

Repository: `https://github.com/protectai/llm-guard`

Why it matters:

- It was a focused LLM security toolkit.
- It supported prompt scanners such as anonymize, regex and secrets, and output scanners such as deanonymize and sensitive.

Concerns:

- The repository was archived on 2026-07-09 and marked read-only.
- Archived status makes it a risky dependency for a new project.

Recommendation:

Use as design reference only. Do not build a new system around it.

### 6. LiteLLM

Repository: `https://github.com/BerriAI/litellm`

Why it matters:

- Mature OpenAI-compatible AI Gateway.
- Supports many LLM providers and production gateway features.
- Has guardrails concepts and proxy mode.

Concerns:

- It helps when applications call an OpenAI-compatible API endpoint through a proxy.
- It does not solve Codex desktop prompt interception unless Codex can be routed through such a proxy.
- It is a large platform compared to our local prompt gate.

Recommendation:

Consider only for future gateway/proxy mode if we want centralized provider routing. Not necessary for the Codex hook MVP.

### 7. LlamaFirewall / PurpleLlama

Repository: `https://github.com/meta-llama/PurpleLlama/tree/main/LlamaFirewall`

Why it matters:

- Open-source security guardrail framework.
- Supports prompt injection scanners, alignment checks, CodeShield and custom regex scanners.

Concerns:

- It is about AI security and prompt injection more than privacy redaction.
- It does not provide stable pseudonymization and restoration vault.

Recommendation:

Use later as an adjacent scanner layer for prompt injection or dangerous-code detection. It is not the core sanitizer.

## Build-Vs-Buy Decision

Do not build every detector from scratch.

But do build these parts ourselves:

- Codex hook adapter;
- local confirmation/handoff UI;
- policy layering for global/org/project/session;
- stable HMAC pseudonym mapping vault;
- span resolver and renderer contract;
- restoration UX and restored-output warning;
- fail-closed adapter state machine.

Use existing projects for these parts:

- PII recognition: Presidio.
- Secret recognition: Gitleaks first, TruffleHog optional.
- LLM scrub/restore/proxy design: llm-redactor as reference.
- Prompt-injection guardrail: LlamaFirewall optional later.

## Recommended Architecture Update

The sanitizer should become a pluggable orchestrator:

```text
Codex hook/gateway
  -> sanitizer orchestrator
      -> Presidio PII scanner
      -> Gitleaks/TruffleHog secret scanner
      -> custom dictionary scanner
      -> custom regex scanner
      -> policy engine
      -> mapping vault
      -> span renderer
  -> confirmation/handoff UI
```

This keeps the hard Codex-specific and security-boundary parts under our control while avoiding rewriting mature PII and secret detection engines.

## Immediate Next Experiment

Before choosing a dependency, run a local spike:

1. Feed the same sample prompts to Presidio, Gitleaks and llm-redactor.
2. Measure detected spans, false positives and missed internal terms.
3. Check whether each tool can return exact offsets needed for span-based rendering.
4. Check whether it can run fully offline.
5. Check whether logs/stdout can be made raw-value-free.

Minimum sample set:

- internal URL;
- private IP and CIDR;
- email;
- Windows path with username;
- API token;
- JWT;
- connection string;
- customer/project/product name from dictionary;
- public docs URL that should be allowed.

Spike outcome: see `spikes/tool-evaluation/SPIKE_RESULTS_2026-07-17.md`.

The practical MVP recommendation after testing is to start with a local redaction orchestrator using Gitleaks in redacted pipe mode for secrets, built-in technical scanners for non-secret infrastructure identifiers, custom dictionaries/regexes for organization-specific terms, and our own span resolver, HMAC vault and Codex adapter. llm-redactor remains a design reference for scrub/restore flow and regex taxonomy, not a required MVP dependency. Presidio remains a strong PII candidate but needs a separate packaging spike; TruffleHog should stay optional/reference for now.

## Sources Reviewed

- `https://github.com/jayluxferro/llm-redactor`
- `https://github.com/data-privacy-stack/presidio`
- `https://github.com/gitleaks/gitleaks`
- `https://github.com/trufflesecurity/trufflehog`
- `https://github.com/protectai/llm-guard`
- `https://github.com/LeapBeyond/scrubadub`
- `https://github.com/BerriAI/litellm`
- `https://github.com/meta-llama/PurpleLlama/tree/main/LlamaFirewall`
- Casper paper: `https://arxiv.org/abs/2408.07004`
- LLM-Redactor paper: `https://arxiv.org/abs/2604.12064`
