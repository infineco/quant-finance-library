## Summary

<!-- What does this change do, and why? Link the issue it closes or relates to, e.g. "Closes #123" or "Related: #123". -->

## Tests run

<!-- Commands and results. For example:
- `dotnet build QuantFinance.sln -c Release`: 0 warnings, 0 errors
- `dotnet test QuantFinance.sln -c Release`: 412 passed, 0 failed, 9 skipped (GPU / Hybridizer)
-->

- [ ]

## Checklist

- [ ] The change builds and the default tests pass (`dotnet test QuantFinance.sln -c Release`).
- [ ] New numerical code has tests against an independent reference (closed form, published value or finite difference). Not against a copy of the implementation.
- [ ] Numerical references and tolerances are stated in the tests, with their justification (absolute or relative, Monte Carlo standard error). Stochastic tests use deterministic seeds.
- [ ] Pure math lives in Hybridizer-free helpers and is tested with plain `[Fact]`, where possible.
- [ ] Public API changes are documented (XML comments, `docs/` if relevant) and called out below.
- [ ] Kernel-compatible code (`[Kernel]`, `[EntryPoint]`, or called from them) avoids allocation, LINQ, exceptions, boxing and `string`.
- [ ] Commit messages follow the convention in CONTRIBUTING.md.

## Numerical impact

<!-- If results change for existing inputs, say so and give before/after values and the tolerance. Otherwise write "None". -->

## GPU impact

<!-- Which execution paths does this touch: plain .NET, Hybridizer AVX/OMP, Hybridizer CUDA? -->
- [ ] Not applicable (host-only change)
- [ ] Changes kernel code: GPU tests were run on (GPU model, CUDA version, Hybridizer version), or "not run locally, needs GPU CI"

## Not tested

<!-- Verification you could not run, for example Hybridizer / GPU tests on an arm64 host. Be explicit. -->
