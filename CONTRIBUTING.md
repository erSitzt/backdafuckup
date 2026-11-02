# Contributing to backdafuckup

Thanks for your interest in contributing!

## Contributor License Agreement (CLA)

backdafuckup is licensed under AGPL-3.0. To keep the option of offering the code under additional licences
(dual licensing) and to be able to defend the project legally, every contributor must sign a
Contributor License Agreement before a pull request can be merged.

> **Draft:** the CLA text and the signing process (e.g. a CLA bot on pull requests) are not final yet and
> are pending legal review. Until then, external contributions cannot be merged.

## Ground rules

- **Integration policy:** only publicly documented APIs. Open-source dependencies are fine if they are
  compatible with AGPL-3.0. Never add code, binaries or tools that are only obtainable through a purchased
  product (for example Veeam components or VMware VDDK), partner-gated SDKs, or undocumented protocols.
- Never commit anything from `research/` (proprietary reference material, excluded via `.gitignore`).
- Add every new dependency to `Directory.Packages.props` **and** `THIRD-PARTY-NOTICES.md`.
- Code: .NET 10, nullable enabled, warnings are errors, async I/O with a trailing `CancellationToken`.
- Tests: xUnit. Categories `Unit`, `Integration` (needs Docker) and `Lab` (needs a real XCP-ng pool, opt-in).
