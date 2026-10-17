# Roadmap

This roadmap is derived from the open issues of the project. It is a plan, not a commitment: scope and
order can change as the work progresses. Issue numbers refer to the GitHub issues of this repository.

## Near term

**Open-source release 0.1**: the first public package and documentation set.

- Packaging: NuGet packages, source link, versioning and the release process (#78).
- Documentation: English documentation, API reference and GitHub Pages (#77, #82), GPU guide and Hybridizer
  requirements (#83), README (#84).
- Samples: runnable examples and Excel workbooks (#81, #76).
- Public repository: publication and protected branches (#86).

**GPU continuous integration**

- Nightly GPU CI and a published CI image (#80).
- Continuous integration matrix for operating systems, formatting, coverage, CodeQL and Dependabot (#79).

## Next

- Longstaff-Schwartz American pricing on the GPU without host-created delegates, by replacing them with
  functors (#65).
- Multi-curve interest rate modelling.
- Second-order Greeks.
- Single-precision GPU kernels.

## Upstream Hybridizer fixes

Issues we track with the Hybridizer vendor, which affect what the library can do on the GPU today:

- FieldTools support for array fields.
- Race condition in `alignedstorage`.
- Runtime data loss with chained launches and concurrent reads (#66).
- Correct CUDA translation of `double.IsNegativeInfinity` (#69).

## Not on the roadmap

Rates, credit and FX coverage beyond what is listed above are not planned yet. Requests for them are welcome
as feature requests, but they will be prioritised against the list above.
