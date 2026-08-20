# Contributing

Thanks for your interest in the project. Issues and pull requests are welcome.

## Licensing of contributions

By submitting a pull request, an issue containing code, or any other contribution to this repository, you agree that your contribution is licensed under the same [MIT Licence](LICENSE) that covers the project, and you confirm that you have the right to license it on those terms.

This is inbound = outbound: contributions come in under the same licence the project goes out under. No separate contributor licence agreement is required.

If your contribution includes code you did not write, say so in the pull request and identify its source and licence. Code under a licence incompatible with MIT cannot be accepted.

## Before opening a pull request

- Keep the existing conventions: British spelling in prose and identifiers, `_camelCase` private fields, XML documentation on public members, and the licence header at the top of each source file.
- Add or update tests in `Tests/Runtime/` for any behavioural change, and run them via **Window > General > Test Runner** on the **PlayMode** tab.
- Update `README.md` and `docs/architecture.md` where the change affects documented behaviour.

## Handling student data

This library processes data about children. Please do not introduce logging, telemetry, analytics, network calls, or persistence that transmits or stores learner data by default. Anything of that kind belongs in the integrator's own code, where it can be assessed against the school's data protection obligations.

Test fixtures and examples should use obviously synthetic identifiers (`student-1`, `student-4021`), never anything resembling a real person.

## Reporting security issues

Please do not open a public issue for a security or privacy vulnerability. See [SECURITY.md](SECURITY.md).
