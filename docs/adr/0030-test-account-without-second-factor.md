# 0030. A configured test account may sign in without a second factor

- **Status:** Accepted
- **Date:** 2026-10-20
- **Amends:** [0022](0022-staff-authentication-and-authorization.md)

## Context

ADR-0022 makes a second factor mandatory for every staff capability. People who only want to try the system (a
reviewer, a tester, a demonstration) then have to install an authenticator app before they can see the admin view.
The system is not deployed anywhere with real staff, and the friction buys nothing in that setting, while the rule
must stay intact for real accounts.

## Decision

1. **`Staff:TestAccount`** names one test account (e-mail and password). It exists only when both are set; empty
   values turn it off. Docker Compose and the Aspire host set `tester@elevator.local` with a public password,
   documented in the README, and either can be cleared to turn the account off.
2. **Marked at creation.** Start-up creates the account, like the other seeded accounts, and adds the
   `elevator:test-account` claim to it. An account that already existed is never marked, so pointing the setting at
   a real admin's e-mail exempts nobody.
3. **Exempt only while configured.** A password sign-in of a marked account whose e-mail is still the configured one
   gets the session method `amr=test-account`. The staff policies accept `mfa` or `test-account`; every other account
   still needs `mfa`. Removing the setting ends the exemption at the next sign-in.
4. **Never disguised.** The session says `TestAccount: true` and `TwoFactorVerified: false`; the admin view shows a
   banner saying the session has no second factor; start-up logs a warning (event 3015) for as long as the account
   is enabled. Sign-ins are audited like any other.
5. The account keeps every other control: lockout after five wrong passwords, the sign-in rate limit, antiforgery,
   `__Host-` cookies, and it may still register an authenticator, after which it signs in with codes like anyone.

## Alternatives considered

- **A published authenticator key for a demo account** — still needs an app, which is the friction to remove.
- **Disabling the second factor everywhere in a "demo mode"** — weakens every account at once and is easy to leave on.
- **Development environment only** — the Docker Compose stack runs in Production, which is where testers use it.

## Consequences

- Anyone who knows the public password signs in as an admin wherever the test account is enabled: it must be turned
  off (an empty password) anywhere the system is exposed beyond a local machine.
- `AuthorizationMatrixTests` gains a column for the test account, and `TestAccountTests` pin the rules above.
