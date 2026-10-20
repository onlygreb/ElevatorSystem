# 0013. Keep the UI culture preference in browser storage

- **Status:** Accepted
- **Date:** 2026-10-19
- **Amends:** [0009](0009-privacy-by-design-for-lgpd.md), Consequences, the list of strictly necessary cookies

## Context

ADR-0009 lists the culture preference among the strictly necessary cookies, next to the
authentication and antiforgery cookies. The `blazor-ui` skill stores the language picker's choice
in `localStorage`. Two sources disagree about what the browser holds.

The UI is Blazor WebAssembly with prerendering off (ADR-0006): the culture is applied in the
browser, and the server never renders a page in the user's language.

## Decision

The culture preference (`en-US` or `pt-BR`) is kept in the browser's `localStorage` and never
sent to the server. The strictly necessary cookies are the authentication and antiforgery
cookies only. The rest of ADR-0009 is unchanged.

## Alternatives considered

- **A culture cookie (`.AspNetCore.Culture`):** sent with every request for no benefit, since
  the server renders nothing localised. It would matter only if prerendering or server-side
  localisation were introduced, which would need its own ADR anyway.

## Consequences

- The privacy notice lists the language preference under browser storage, not cookies, and it
  still needs no consent: it is a preference the user sets, holds nothing personal and never
  leaves the browser.
- If the server ever needs the culture (localised error messages, e-mails), it reads
  `Accept-Language` or a new decision changes this one.
