# Security Policy

## Supported versions

The library is pre-1.0. Only the latest commit on the `main` branch, and the latest
published pre-release, receive security fixes. Older versions are not patched; upgrade
to the latest release instead.

| Version        | Supported          |
| -------------- | ------------------ |
| latest `main`  | :white_check_mark: |
| older versions | :x:                |

## Reporting a vulnerability

Please do not report security vulnerabilities in public issues, pull requests or
discussions.

Use GitHub private vulnerability reporting instead:
go to the repository's **Security** tab and choose **Report a vulnerability**
(or open <https://github.com/infineco/quant-finance-library/security/advisories/new>).
This keeps the report visible only to the maintainers until a fix is ready.

Please include:

* the affected project(s) and version or commit,
* a description of the issue and its impact,
* steps or a minimal code sample to reproduce it,
* any suggested mitigation, if you have one.

Scope note: this library computes prices and risk figures. Numerical inaccuracies
(for example a wrong formula or an unstable approximation) are bugs, not security
issues; report them as regular bug reports, using the bug report template.

## Response expectations

* Acknowledgement of your report: within 5 business days.
* Initial assessment (accepted, needs more information, or declined): within 10 business days.
* Fix or mitigation for accepted reports: a target is set with the reporter, depending on
  severity. Critical issues are prioritised over all other work.
* Coordinated disclosure: we ask reporters to keep the issue private until a fix is
  released. We credit reporters in the release notes unless they prefer to stay anonymous.

These are targets for a small maintainer team, not guarantees.
