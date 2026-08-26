# Evidence Ticket Template

Use this template for every protected-behavior bug fix or safety change. A ticket
may claim `fixed` only after its required evidence state is green for the same
candidate build.

```text
## Ticket <id>: <title>

State owner: <single resident/module owner>
Fail-closed state: <observable safe outcome>
Allowed transitions: <explicit state transitions>
Red-capable reproduction: <raw-free command or scenario id>
Highest required seam: <deterministic/reference/live/installed seam>
Evidence target: <implemented|locally_verified|live_verified|released>
Build identity: <version, source commit, executable hash, installer identity>
Target identity: <compatibility fingerprint and Send binding>
Evidence record location: artifacts/evidence/<ticket-id>.json
Evidence discovery: <repository-relative path resolved by the release gate>
Validator artifact hash: <sha256 of the validator executable or tool>
Evidence state: proposed
Claim: unverified
```

The evidence record must contain only safe identifiers, typed states, opaque
fingerprints, build identity, and terminal outcomes. It must not contain prompt
text, sensitive values, mappings, paths, window titles, exception messages, or
other raw input.

The only valid state progression is:

```text
proposed -> reproduced_red -> implemented -> locally_verified -> live_verified -> released
```

`implemented` is a development state, not a user-facing `fixed` or `released`
claim. If a later check fails, retain the last state still supported by the
evidence and record a new raw-free failure reason.

For a `locally_verified` or stronger release-bound record, the release gate
must load the declared repository-relative record and compare its schema,
reproduction identifiers, source commit, build version, executable hash,
target binding, and validator artifact hash with the candidate being released.
The record is published after the candidate build; it is not created by the
synthetic validator smoke.
