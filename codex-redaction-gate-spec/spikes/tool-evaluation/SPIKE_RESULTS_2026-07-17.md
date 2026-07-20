# Tool Evaluation Spike Results

Date: 2026-07-17

## Scope

This spike tested whether the proposed open-source candidates can serve as practical building blocks for Codex Redaction Gate.

The shared test corpus is stored locally under this directory:

- `corpus.json`
- `corpus.txt`
- `custom_policy_terms.csv`
- `fake_secrets.txt`

All sample values are synthetic. The committed `fake_secrets.txt` file is a safe template because GitHub push protection blocks committed strings that match secret shapes even when they are fake. For local scanner reproduction, generate the scanner-triggering fixture under the ignored `.generated/` directory:

```text
pwsh ./generate_fake_secrets.ps1
```

## Tested Candidates

| Candidate | Status | Result |
| --- | --- | --- |
| Baseline regex harness | Ran successfully | Good control case; catches obvious technical patterns, misses business terms, produces overlaps |
| Presidio | Install attempted, not completed in quick spike | Setup was too heavy/slow in this Windows environment; needs separate setup spike |
| Gitleaks | Installed and ran | Good as lightweight secret-scanner backend; needs `--redact`; line/column output must be converted to offsets |
| TruffleHog | Clone succeeded, build did not complete | Too heavy for MVP hook path in this environment; keep as optional/reference |
| llm-redactor regex detector | Ran successfully from source without full install | Very useful reference/backend; returns offsets and taxonomy, but has overlaps and false positives |

## Baseline Regex

Command:

```text
python run_basic_regex_baseline.py
```

Findings:

- Detected internal URL and bearer-like token.
- Detected private IPv4 addresses.
- Detected CIDR partially as both IP and CIDR, proving the need for span resolution.
- Detected email and Windows path.
- Detected connection string, JWT and private key block.
- Did not detect business terms such as customer, product, project and supplier names.
- Did not apply public URL allowlist policy.

Conclusion: baseline regex is useful for tests and emergency fallback, not enough for production.

## Presidio

Attempted:

```text
python -m venv .venv
.venv\Scripts\python.exe -m pip install --disable-pip-version-check presidio-analyzer presidio-anonymizer
```

Observed:

- Installation started and downloaded package metadata.
- The process stayed active for several minutes without completing.
- The install was stopped to keep the spike bounded.
- No useful Presidio run was completed in this environment.

Conclusion:

Presidio remains the best candidate for mature PII recognition, but it needs a dedicated setup spike. For Windows/local hook MVP, it may be too heavy unless packaged carefully or run as a local service.

Next Presidio spike should test:

- Python 3.12 compatibility;
- whether `AnalyzerEngine(..., nlp_engine=None)` can run pattern/custom recognizers without spaCy models;
- whether a prebuilt venv or packaged service is acceptable;
- exact offsets and raw-value-free logs.

## Gitleaks

Install:

```text
go install github.com/zricethezav/gitleaks/v8@latest
```

Installed binary:

```text
bin\gitleaks.exe
```

Notes:

- The newer GitHub repo path `github.com/gitleaks/gitleaks/v8` conflicted with the Go module path.
- The working module path was `github.com/zricethezav/gitleaks/v8`.
- Built binary reports `version is set by build process` because it was built from source without release ldflags.

File scan:

```text
pwsh ./generate_fake_secrets.ps1
gitleaks detect --no-git --source .generated/fake_secrets.txt --report-format json --report-path -
```

Observed:

- Detected the synthetic Slack bot token.
- Did not detect the synthetic AWS/GitHub samples in this corpus. This may be because the fake samples did not satisfy default rules/validation expectations.
- Exit code `1` means findings were found.
- Without `--redact`, JSON contains raw `Match` and `Secret` values.
- With `--redact`, `Match` and `Secret` are replaced with `REDACTED`.

Pipe scan:

```text
Get-Content -Raw .generated/fake_secrets.txt | gitleaks detect --pipe --report-format json --report-path - --redact
```

Observed:

- Pipe mode works and is suitable for prompt text.
- Output gives `StartLine`, `EndLine`, `StartColumn`, `EndColumn`, not absolute character offsets.
- Hook integration must convert line/column to offsets before span rendering.

Conclusion:

Gitleaks is the best MVP secret backend. Use it with:

- `--pipe`;
- `--report-format json`;
- `--report-path -`;
- `--redact`;
- timeout;
- local config;
- line/column to offset conversion.

Do not treat Gitleaks as the only detector. It does not catch internal URLs, business terms, emails, paths, or all token-like values by default.

## TruffleHog

Attempted:

```text
go install github.com/trufflesecurity/trufflehog/v3@latest
```

Result:

- Failed because the module contains `replace` directives, which cannot be used through `go install` in this mode.

Then attempted:

```text
git clone --depth 1 https://github.com/trufflesecurity/trufflehog.git trufflehog-src
go build -o ..\bin\trufflehog.exe .
```

Observed:

- Shallow clone succeeded.
- Initial build failed because Go tried to write module/build caches under the user profile, which is outside the workspace sandbox.
- Retried with local `GOMODCACHE` and `GOCACHE`.
- Build downloaded many dependencies, including cloud SDKs and provider integrations.
- Build emitted a compile failure for `github.com/google/go-containerregistry/pkg/v1`.
- The build was stopped after it continued running beyond the quick spike window.
- No TruffleHog binary was produced.

Conclusion:

TruffleHog is powerful, but too heavy for the MVP hook path in this Windows/sandbox setup. Keep it as:

- reference detector catalog;
- optional offline backend later;
- not the default prompt-time scanner.

If used later, active verification must be disabled in the prompt sanitizer path because the sanitizer must work offline and must not call external providers while scanning prompts.

## llm-redactor

Clone:

```text
git clone --depth 1 https://github.com/jayluxferro/llm-redactor.git llm-redactor-src
```

Reviewed commit:

```text
acd9019c8b5a376abfc8250f66ae0d30ef89f2af
```

Full install was not attempted because `pyproject.toml` depends on Presidio, spaCy, FastAPI, MCP, tiktoken and other runtime packages.

Instead, the regex detector was run directly from source:

```text
python run_llm_redactor_regex.py
```

Observed:

- Detected email with offsets.
- Detected IPv4 addresses with offsets.
- Detected bearer token and OpenAI-shaped key.
- Detected JWT.
- Detected private key header.
- Detected connection string.
- Detected internal hostname fragments.
- Did not detect business terms from our `custom_policy_terms.csv`.
- Did not detect public URLs, which means allowlist policy still belongs in our policy engine.
- Produced overlapping spans.
- Produced a false positive `phone_us` inside a synthetic token.

Useful code/design:

- `detect/regex.py`: broad regex catalog and taxonomy.
- `detect/types.py`: useful category model.
- `redact/placeholder.py`: typed placeholders plus reverse map.
- `redact/restore.py`: simple exact placeholder restoration.
- HTTP proxy and MCP API design are close to our gateway ideas.

Mismatch with our target:

- Reverse map is in-memory/session-oriented, not a persistent encrypted HMAC vault.
- Placeholders are counter-based, not deterministic cross-project pseudonyms.
- It still needs a resolver around overlaps and false positives.
- It does not solve Codex `UserPromptSubmit` interception.

Conclusion:

`llm-redactor` is the closest conceptual project and the best design reference. It is not a drop-in foundation yet. The regex detector and taxonomy can be borrowed or adapted after license/code review.

## Overall Recommendation

Recommended MVP stack:

```text
Codex UserPromptSubmit guard
  -> local sanitizer orchestrator
      -> llm-redactor-inspired regex scanner
      -> Gitleaks pipe scanner with redacted JSON
      -> custom dictionary scanner
      -> custom regex policy scanner
      -> span resolver
      -> HMAC mapping vault
      -> renderer/verifier
  -> local confirmation / clipboard handoff UI
```

Defer:

- Presidio integration until packaging/setup is solved.
- TruffleHog until an optional offline binary or release package is selected.
- Full llm-redactor proxy adoption until code quality, tests, storage model and release maturity are reviewed.

## Engineering Decisions From Spike

1. We still need our own span resolver.
   Baseline regex and llm-redactor both produce overlaps.

2. We still need policy-as-data.
   None of the tested detectors know local customers, products, projects or internal domain semantics without custom rules.

3. We still need our own mapping vault.
   Existing projects do not provide the encrypted stable HMAC mapping required by this architecture.

4. Gitleaks can be useful only with safe output flags.
   Default JSON can contain raw secrets; hook path must use `--redact`.

5. Heavy scanners should not block prompt submission indefinitely.
   Every scanner backend needs a timeout and fail-closed policy.
