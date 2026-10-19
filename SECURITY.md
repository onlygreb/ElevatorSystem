# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Report them privately through
GitHub's private vulnerability reporting: open the **Security** tab of this repository and choose
**Report a vulnerability**.

Include, where possible:

- the affected component (API, engine, web UI, messaging, build or release pipeline)
- steps to reproduce, or a proof of concept
- the impact you observed or expect

You will receive an acknowledgement within 3 business days and updates at least weekly until the
issue is resolved. Credit is given in the release notes unless you prefer otherwise.

## Supported versions

Security fixes are applied to `main` and released in the next version. Older releases are not patched.

## Personal data

If a vulnerability may have exposed personal data, it is handled as a security incident under the
LGPD (Lei nº 13.709/2018): affected data subjects and the ANPD are notified as required by ANPD
Resolution nº 15/2024. The system is designed to hold as little personal data as possible; see
[ADR-0009](docs/adr/0009-privacy-by-design-for-lgpd.md).

## How this repository protects itself

- Builds fail on any known-vulnerable NuGet package, direct or transitive.
- Dependencies are restored only from nuget.org, with committed lock files.
- GitHub Actions are pinned to commit SHAs and run with least-privilege tokens.
- CodeQL scans the C# code and the workflows; Dependabot proposes updates weekly.
- Released container images are non-root, minimal (chiseled) and carry signed build provenance.
