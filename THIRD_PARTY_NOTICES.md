# Third-party notices

The quant finance library itself is released under the MIT licence (see [LICENSE](LICENSE)).

The components listed below are third-party code or data used by or bundled with the library. Each is
distributed under its own licence, which is stated here as it appears in the sources or upstream. All of
them are compatible with redistribution under MIT, provided their notices are kept. The `research/`
directory (including the vecmathlib and SLEEF copies under `research/native/ThirdParties/`) is not part of
the published library; those entries are kept for transparency about what the repository contains.

This file lists third-party components used by or bundled with the quant finance library,
with their origin and licence as stated in the files themselves. Where the source does not
state a licence, this is marked explicitly.

## 1. vecmathlib (bundled source)

- **Location:** `research/native/ThirdParties/vecmathlib/`
- **Origin:** "From the wiki at <https://bitbucket.org/eschnett/vecmathlib/wiki/Home>" (README).
- **Copyright:** "Copyright (c) 2012, 2013 Erik Schnetter <eschnetter@gmail.com>" (LICENCE).
- **Licence:** MIT. The LICENCE file contains the standard MIT permission notice
  ("Permission is hereby granted, free of charge, to any person obtaining a copy of this software ...").
- **Used by:** the C++ projects through include paths in
  `research/native/InFine.QuantFinance.Haswell/InFine.QuantFinance.Haswell.vcxproj`,
  `research/native/InFine.QuantFinance.Haswell/InFine.QuantFinance.Haswell.BestPerf.vcxproj` and
  `research/native/InFine.QuantFinance.VSX/InFine.QuantFinance.VSX.vcxproj`
  (`AdditionalIncludeDirectories` pointing at `...\ThirdParties\vecmathlib`).
  The lab Makefiles reference the same library by path during the build.
- **Notes:** the copy is a snapshot of the upstream tree and contains some non-source leftovers
  (`*.bak`, `mathfuncs_exp - Copy.h`). These are not licence issues but should be cleaned up separately.

## 2. SLEEF 2.80 (bundled source, nested in vecmathlib)

- **Location:** `research/native/ThirdParties/vecmathlib/sleef-2.80/`
- **Origin:** README: "Author : Naoki Shibata" and "Main download page : http://shibatch.sourceforge.net/".
- **Licence:** README states: "The software is in public domain. You can use the software without any obligation."
  No separate LICENCE or COPYING file was found in this directory, and no copyright header was found in the
  sources under `sleef-2.80/`. Public-domain status is therefore taken from the README only;
  to verify against upstream before redistribution.
- **Used by:** no project build file under this repository references the `sleef-2.80/` directory.
  vecmathlib sources only cite it as the origin of some algorithms, for example
  `research/native/ThirdParties/vecmathlib/mathfuncs_log.h` ("Algorithm inspired by SLEEF 2.80") and
  `research/native/ThirdParties/vecmathlib/mathfuncs_asin.h` ("Algorithm taken from SLEEF 2.80").
  Those derived algorithms fall under the attribution question below.
- **Attribution question (to verify):** the vecmathlib sources that say "taken from" / "inspired by" SLEEF
  do not state a licence for the derived code beyond vecmathlib's MIT licence. Confirm with the author
  whether the public-domain SLEEF terms need to be credited.

## 3. Hybridizer.Runtime.CUDAImports (NuGet package)

- **Used by:** C# projects referencing `<Reference Include="Hybridizer.Runtime.CUDAImports">`,
  for example `research/legacy-benchmarks/InFine.QuantFinance.Benchmarks/InFine.QuantFinance.Benchmarks.csproj`, and the
  `research/labs/*/main.cs` programs.
- **Licence:** MIT. Verified: `src/Hybridizer.Runtime.CUDAImports.csproj` in the source repository
  hybridizer-io/hybridizer-runtime-cudaimports (commit 437873e) declares
  `<PackageLicenseExpression>MIT</PackageLicenseExpression>`, and the repository ships a `LICENSE` file. The package
  version is not pinned in this repository (no `packages.config` entry was found for it).

## 4. Joe-Kuo Sobol direction numbers (embedded data)

- **Location:** `InFine.QuantFinance.Random/QuasiRandom/JoeKuoDirectionNumbers.cs` (generated table).
- **Origin:** S. Joe and F. Y. Kuo, file `new-joe-kuo-6.21201` from <https://web.maths.unsw.edu.au/~fkuo/sobol/>
  (direction numbers of "Constructing Sobol sequences with better two-dimensional projections",
  SIAM J. Sci. Comput. 30 (2008) 2635-2654). Rows for dimensions 2..1111 are embedded (the `d` column dropped);
  the SHA-256 of the downloaded file is recorded in the generated source.
- **Copyright:** "Copyright (c) 2008, Frances Y. Kuo and Stephen Joe".
- **Licence:** BSD-style (3-clause), stated at <https://web.maths.unsw.edu.au/~fkuo/sobol/licence> as covering
  "sobol.cc and the accompanying sets of direction numbers". The full licence text, including the copyright
  notice, the three conditions and the disclaimer, is reproduced in the header of the generated source file,
  as the licence requires for source redistribution. Binary redistributions must reproduce the same notice in
  their documentation.
- **Used by:** `InFine.QuantFinance.Random.QuasiRandom.Sobol`.

## 5. "Let's Be Rational" implied volatility (ported source)

- **Location:** `InFine.QuantFinance.ClosedForm/ImpliedVolatilityKernels.cs` (C# port).
- **Origin:** Peter Jäckel's reference implementation, <http://www.jaeckel.org/LetsBeRational.7z>
  (version of 2024-02-15: `lets_be_rational.cpp`, `rationalcubic.cpp`), accompanying "Let's Be Rational",
  Wilmott, January 2015, pp. 40-53. The rational function coefficients and the asymptotic and small-volatility
  expansions were extracted programmatically from the source; Cody's erf/erfc/erfcx coefficients were already
  in `NormalDistribution.cs`.
- **Copyright:** "Copyright © 2013-2024 Peter Jäckel."
- **Licence:** permissive: "Permission to use, copy, modify, and distribute this software is freely granted,
  provided that this notice is preserved", with a warranty disclaimer. The notice and disclaimer are reproduced
  verbatim at the top of the ported file.
- **Used by:** `InFine.QuantFinance.ClosedForm.ImpliedVolatilityKernels` and `ImpliedVolatility`.

## 6. MINPACK Levenberg-Marquardt (ported source)

- **Location:** `InFine.QuantFinance.Numerics/LevenbergMarquardt.cs` (C# port of `lmder`, `lmpar`, `qrfac`, `qrsolv`).
- **Origin:** MINPACK, J. J. Moré, B. S. Garbow, K. E. Hillstrom, Argonne National Laboratory, <https://www.netlib.org/minpack/>.
- **Licence:** MINPACK copyright notice (BSD-style with acknowledgment clause), from <https://www.netlib.org/minpack/disclaimer>. The notice is also reproduced at the top of the ported file.
- **Acknowledgment:** This product includes software developed by the University of Chicago, as Operator of Argonne National Laboratory.

```text
Minpack Copyright Notice (1999) University of Chicago.  All rights reserved

Redistribution and use in source and binary forms, with or
without modification, are permitted provided that the
following conditions are met:

1. Redistributions of source code must retain the above
copyright notice, this list of conditions and the following
disclaimer.

2. Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following
disclaimer in the documentation and/or other materials
provided with the distribution.

3. The end-user documentation included with the
redistribution, if any, must include the following
acknowledgment:

   "This product includes software developed by the
   University of Chicago, as Operator of Argonne National
   Laboratory.

Alternately, this acknowledgment may appear in the software
itself, if and wherever such third-party acknowledgments
normally appear.

4. WARRANTY DISCLAIMER. THE SOFTWARE IS SUPPLIED "AS IS"
WITHOUT WARRANTY OF ANY KIND. THE COPYRIGHT HOLDER, THE
UNITED STATES, THE UNITED STATES DEPARTMENT OF ENERGY, AND
THEIR EMPLOYEES: (1) DISCLAIM ANY WARRANTIES, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO ANY IMPLIED WARRANTIES
OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, TITLE
OR NON-INFRINGEMENT, (2) DO NOT ASSUME ANY LEGAL LIABILITY
OR RESPONSIBILITY FOR THE ACCURACY, COMPLETENESS, OR
USEFULNESS OF THE SOFTWARE, (3) DO NOT REPRESENT THAT USE OF
THE SOFTWARE WOULD NOT INFRINGE PRIVATELY OWNED RIGHTS, (4)
DO NOT WARRANT THAT THE SOFTWARE WILL FUNCTION
UNINTERRUPTED, THAT IT IS ERROR-FREE OR THAT ANY ERRORS WILL
BE CORRECTED.

5. LIMITATION OF LIABILITY. IN NO EVENT WILL THE COPYRIGHT
HOLDER, THE UNITED STATES, THE UNITED STATES DEPARTMENT OF
ENERGY, OR THEIR EMPLOYEES: BE LIABLE FOR ANY INDIRECT,
INCIDENTAL, CONSEQUENTIAL, SPECIAL OR PUNITIVE DAMAGES OF
ANY KIND OR NATURE, INCLUDING BUT NOT LIMITED TO LOSS OF
PROFITS OR LOSS OF DATA, FOR ANY REASON WHATSOEVER, WHETHER
SUCH LIABILITY IS ASSERTED ON THE BASIS OF CONTRACT, TORT
(INCLUDING NEGLIGENCE OR STRICT LIABILITY), OR OTHERWISE,
EVEN IF ANY OF SAID PARTIES HAS BEEN WARNED OF THE
POSSIBILITY OF SUCH LOSS OR DAMAGES.
```

## 7. Other components

- No other third-party sources were found under `research/native/ThirdParties/`.
- Test and benchmark dependencies named in the project (xUnit, BenchmarkDotNet, ExcelDna) are not yet
  part of the repository's SDK-style projects and are not listed here. Add them when introduced.
