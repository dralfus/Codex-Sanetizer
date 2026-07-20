# Threat Model

## Assets

- Internal URLs and domains.
- Company and product names.
- Customer and project names.
- Personal data such as names and emails.
- Internal IPs, CIDRs and hostnames.
- Logs and configuration snippets.
- Passwords, tokens, keys and cookies.
- Mapping table between real values and pseudonyms.
- HMAC/encryption secret.

## Protected Against

- Accidental paste of sensitive data into Codex prompt.
- Accidental inclusion of secrets in logs or config snippets.
- Repeated disclosure of company structure through stable identifiers.
- Sending real identifiers when sanitized identifiers would be sufficient.
- Reintroducing real values into cloud prompts after a sanitized response is restored locally, if the gateway warns or blocks.

## Not Protected Against

- Data already sent before the gate was installed.
- A user intentionally bypassing the gate.
- External tools or connectors that send data outside the gateway path.
- Screen sharing or screenshots that expose sensitive data.
- Malware or local admin compromise.
- Perfect de-identification of large text where context itself reveals the organization.
- Legal or policy questions about whether prior submissions can be removed from provider systems.

## Key Risks

### Hash Reversal

Short values such as company domains, product names and customer names are vulnerable to dictionary attacks if hashed with raw SHA256.

Decision: use HMAC with a local secret, include entity type in the input and never expose the secret.

### Mapping Vault Exposure

The mapping vault is sensitive because it contains the relationship between pseudonyms and real values.

Decision: encrypt at rest, store outside repositories, avoid backups by default and protect exports with explicit confirmation.

### Over-Restoration

Restoring real values into a response can create a new sensitive artifact that may later be copied back into Codex.

Decision: visibly mark restored output and warn before sending restored content to cloud contexts.

### False Negatives

The system may miss a sensitive term not covered by pattern detectors or dictionaries.

Decision: support dictionaries, fail closed on high-risk patterns and provide a quick way to add missed terms.

### False Positives

Aggressive detection may interrupt normal work.

Decision: allow public allowlists, project overlays and confidence thresholds, but keep secrets and global blocklist terms strict.

### Policy File Exposure

Policy files and dictionaries may contain sensitive organization-specific names even when they do not contain secrets.

Decision: keep them local by default, exclude them from automatic exports, and treat shared or managed policy distribution as a later signed/encrypted workflow.

### Unsupported Prompt Rewriting

If Codex hooks cannot rewrite prompts, a hook-only implementation cannot meet the desired UX.

Decision: define hook guard as baseline and gateway mode as target.

## Security Posture

The system is a local DLP-style guardrail for accidental disclosure. It should be treated as defense in depth, not as a guarantee that no sensitive context can ever reach a cloud service.
