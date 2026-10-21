# 0034. Docker Compose starts with no setup

- **Status:** Accepted
- **Date:** 2026-10-20
- **Amends:** [0029](0029-docker-compose-and-dockerfile.md)

## Context

ADR-0029 required a `deploy/.env` with four passwords and an audit key, and an HTTPS certificate exported by hand
(`dotnet dev-certs` or two `openssl` commands), before `docker compose up` would even start. Someone who clones the
repository to try the system, a reviewer say, often has neither the .NET SDK nor OpenSSL, and has to invent secrets
for a stack that only ever runs on their own machine.

## Decision

1. **Local defaults for every setting.** Compose substitutes a default for each variable: fixed, public passwords
   for RabbitMQ and PostgreSQL, the certificate's password and the test admin's (ADR-0030). `deploy/.env` becomes
   optional: `.env.example` lists every value with its default and says when to change it.
2. **This machine only.** Because the defaults are public, the API's ports are bound to 127.0.0.1 unless
   `API_ADDRESS` says otherwise, like the dashboard and RabbitMQ's management UI already were. PostgreSQL and AMQP
   are not published at all.
3. **A real admin only when asked.** The first admin is created once `ADMIN_PASSWORD` is set; a configured account
   without a password is skipped at start-up (logged, event 3017) instead of stopping the API. The test admin is on
   by default, so the admin view is usable at once.
4. **A self-signed certificate when there is none.** With `Tls:CreateSelfSignedCertificate` on, the API makes a
   certificate for `localhost`, 127.0.0.1 and ::1, valid for a year, at Kestrel's configured path before Kestrel
   starts, and logs a warning (event 3016). It never replaces an existing file. Compose keeps it in the
   `api-certificate` volume (the image prepares `/https` for the non-root user); `CERTIFICATE_DIR` points at a
   folder with a certificate of your own instead. The staff cookies stay `__Host-` and `Secure` (ADR-0022): HTTPS
   is kept, not weakened.
5. **No audit key by default.** Without `AUDIT_PSEUDONYM_KEY` the API already uses a random key per start and warns
   that pseudonyms link only until a restart; that is acceptable for trying the system out.

## Alternatives considered

- **Plain HTTP for local runs:** the antiforgery and `__Host-` cookies need HTTPS; relaxing them would make the local
  stack behave unlike a deployed one, exactly where staff authentication is meant to be shown.
- **A certificate made by a helper container with OpenSSL:** one more image to pull and pin, for what .NET does in a
  few lines.
- **Generated random secrets on first start:** they would have to be written somewhere both the broker, the database
  and the services read before starting; fixed local defaults bound to this machine are simpler and as safe there.

## Consequences

- `docker compose -f deploy/compose.yaml up --build` works on a fresh clone; the browser warns once about the
  self-signed certificate.
- The defaults are not secrets. Anyone exposing the stack beyond their machine must set their own values; the README,
  `.env.example` and the threat model say so.
