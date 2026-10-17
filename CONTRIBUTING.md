# Contributing

Thank you for your interest in InFine.QuantFinance. This guide explains how to build and test the library, the
conventions we follow, and how to get a change merged.

## Before you start

- For a small fix, open a pull request directly.
- For a new model, instrument, or a change to the public API, open a feature request first so we can agree on the
  scope and the expected results.
- For security issues, do not open a public issue. Follow [SECURITY.md](SECURITY.md).
- Please follow the [Code of Conduct](CODE_OF_CONDUCT.md) in all project spaces.

## Branches and workflow

- `main` holds the stable line. Fork the repository and open your pull request against `main`.
- Keep each pull request focused on one change. Several small commits are fine; the pull request is squashed or
  merged as a whole, so write the pull request description for the reviewer.

## Build and test

You need the .NET SDK 8 (`global.json` pins 8.0.100 and rolls forward to the latest feature band).

```sh
dotnet build QuantFinance.sln -c Release
dotnet test QuantFinance.sln -c Release
```

The default test run needs no GPU, no Hybridizer licence and no MKL. Tests that need them are skipped with a
reason, not failed (see "Testing rules"). The GPU and Hybridizer paths are described in
[docs/verification-linux-cuda.md](docs/verification-linux-cuda.md) and
[docs/verification-windows-gpu.md](docs/verification-windows-gpu.md).

Architecture overview: [docs/architecture.md](docs/architecture.md).

## Commit messages

We write commit messages to explain why a change was made, not only what changed (the diff already shows
what). The first line states the reason in plain words. Then add a body with the context that a future
maintainer would need.

```
Keep the legacy fixed-cash-flows pricer out of CUDA generation

The Hybridizer code generator fails on its delegate-based schedule, so the library
could not be hybridized on Linux. The pricer stays available on the CPU.
```

Git trailers are optional for external contributors. Maintainers use the [Lore protocol](https://github.com/Lore-Protocol/Lore-Protocol-Spec)
trailers to record decisions. If you want to add them, the most useful are:

```
Constraint:  external constraint that shaped the decision
Rejected:    alternative considered | why it was rejected
Confidence:  low | medium | high
Scope-risk:  narrow | moderate | broad
Directive:   warning for future modifiers
Tested:      what was verified
Not-tested:  known verification gaps
Related:     #<issue>
```

Be honest in `Not-tested`: a list of verification gaps is more useful to a reviewer than an implied full coverage.

## Testing rules

- Numerical code is tested against independent references: closed forms, published values, or finite
  differences. Do not test an implementation against a copy of itself.
- State the tolerance in the test and justify it (absolute vs relative, Monte Carlo standard error).
- Use deterministic seeds for stochastic tests.
- Put pure math in Hybridizer-free static helpers (plain `double` / `double[]` signatures) and test them with
  plain `[Fact]`, so they can be verified on any host. Kernel wrappers then call the helpers.
- Use the attributes from `InFine.QuantFinance.TestSupport` when a test needs native resources, so it is
  skipped with a reason rather than failing:
  - `[HybridizerFact]`: the code under test loads `Hybridizer.Runtime.CUDAImports` (does not load on arm64).
  - `[GpuFact]`: needs a CUDA device (and optionally a Hybridizer-generated binary).
  - `[NativeFact(NativeRequirement...)]`: any combination of CUDA device, MKL and the Hybridizer runtime.

## Kernel-compatible code

Code that may run on a GPU or AVX unit (marked `[Kernel]` / `[EntryPoint]`, or called from such code) must not
use:

- allocation,
- LINQ,
- exceptions,
- virtual dispatch through boxing (use generics specialised with `[HybridRegisterTemplate]`, see
  [docs/architecture.md](docs/architecture.md)),
- `string`.

Use structs, arrays and `Math.*` instead. Host-side API code can be idiomatic modern C#.

## Dependencies and style

- No new third-party dependencies without an explicit decision in the issue or pull request. Package versions are
  centralised in `Directory.Packages.props`.
- Match the surrounding code style. Code, comments, documentation and commit messages are in English.
- Keep diffs small and reviewable; prefer reusing existing helpers over new abstractions.

## Pull request checklist

The pull request template lists the checks. In short: the build and tests pass, numerical changes come with
references and tolerances, kernel-compatible code follows the rules above, and the description says what was
tested and what was not.

## Licence

By contributing, you agree that your contribution is licensed under the MIT licence of this project
([LICENSE](LICENSE)).
