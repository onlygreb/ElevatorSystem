# 0006. Blazor WebAssembly UI hosted by the API

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

The system needs a live passenger view (shaft, car, buttons) and an admin panel, available in
English and Brazilian Portuguese. Every rule (validation, authorization, rate limits) must be
enforced in exactly one place, and browser credentials must be protected from theft through XSS.

## Decision

- Build the UI with **Blazor WebAssembly** (`ElevatorSystem.Web`), served by the API host
  (`ElevatorSystem.Api`) from the **same origin**.
- Set the render mode to `InteractiveWebAssembly` with **prerendering off**. Components run only in
  the browser and reach data only through the REST API and the SignalR hub. The architecture tests
  forbid `Web` from referencing anything but `Contracts`.
- Authenticate with **ASP.NET Core Identity cookies** (`HttpOnly`, `Secure`, `SameSite=Strict`)
  plus antiforgery tokens. No access or refresh tokens ever reach the browser (the
  backend-for-frontend pattern, without a separate gateway).
- Use **MudBlazor** (MIT) for the admin chrome, custom CSS for the shaft visualisation, and `.resx`
  localisation for en-US and pt-BR.

## Alternatives considered

- **Blazor Server (interactive server rendering):** components would run on the server with
  direct access to services, which invites bypassing the API's rules. It also holds a server
  circuit per user.
- **React or another JavaScript SPA:** a mature ecosystem, but a second language and toolchain,
  and the contracts would have to be duplicated in TypeScript.
- **A separate UI origin with bearer tokens:** needs CORS and token storage in the browser, which
  is a larger attack surface than same-origin `HttpOnly` cookies.

## Consequences

- One enforcement point: the UI is just another API client, and admin screens hiding buttons is
  only a convenience.
- C# contracts are shared between server and client, with no drift.
- The first load downloads the .NET WebAssembly runtime (a few MB, then cached). That's acceptable
  for an operations tool.
- The CSP must allow `'wasm-unsafe-eval'`, which is the minimum WebAssembly requires. Inline
  scripts stay forbidden.
