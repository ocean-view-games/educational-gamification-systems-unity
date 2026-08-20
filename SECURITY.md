# Security Policy

## Reporting a vulnerability

Please report security and privacy vulnerabilities privately rather than opening a public issue.

Use GitHub's [private vulnerability reporting](https://github.com/Ocean-View-Games/educational-gamification-systems-unity/security/advisories/new) for this repository.

Please include:

- a description of the issue and why it matters,
- the version, commit, or tag affected,
- steps to reproduce, and
- any suggested remediation.

We aim to acknowledge reports within five working days and to keep you updated while we investigate. Please give us a reasonable opportunity to release a fix before disclosing publicly.

## Scope

This repository is a Unity library. It performs no network I/O, writes no files at runtime, and stores no credentials, so the realistic vulnerability surface is narrow. Reports we are particularly interested in:

- **Learner data exposure** — any path by which student identifiers, attempt records, or generated reports could leak into logs, crash reports, serialised scene data, or a build artefact.
- **Injection into exported reports** — untrusted objective or activity identifiers producing malformed or unsafe JSON in `GenerateReportJson`.
- **Denial of service** — unbounded growth or pathological behaviour reachable from ordinary gameplay input.

## Out of scope

- Vulnerabilities in Unity itself, or in a consuming application's own networking, storage, or authentication code. The library deliberately leaves report transport to the integrator.
- The absence of encryption or persistence for learner data. This is a documented design decision, not a defect — see the scope notes in [README.md](README.md) and the data protection section of [docs/architecture.md](docs/architecture.md).

## Supported versions

Fixes are applied to the default branch, and only the most recent release is supported.
