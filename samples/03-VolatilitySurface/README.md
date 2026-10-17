# 03 - Volatility surface

Fits the SVI parametrisation to synthetic smiles and builds a surface from the fits.

1. For four expiries (0.25, 0.5, 1 and 2 years) a smile of 17 strikes (67% to 149% of the forward) is generated from a known
   SVI plus +-0.15 volatility points of deterministic noise; the forward comes from a spot, a discount curve and a dividend
   curve (`SpotForwardCurve`).
2. `SviCalibrator.Calibrate` fits each smile and reports the parameters, the RMS error on total variance and the largest
   volatility error.
3. `ArbitrageChecks.CheckButterfly` verifies on a grid that each fitted slice has a non-negative risk-neutral density
   (Gatheral's function g). A counter-example shows that an SVI with admissible wing slopes can still violate it.
4. `SviVolatilitySurface.CheckCalendar` verifies that total variance does not decrease from one expiry to the next.
5. The surface interpolates in total variance between expiries (columns marked `*` below were not fitted) and prices an option
   through Black-76 on the forward.

## Run

```sh
dotnet run -c Release --project samples/03-VolatilitySurface
```

## Expected output

```text
== SVI fit per expiry (noise +-0.15 vol points, 17 strikes from 67% to 149% of the forward) ==

    T         a        b      rho        m    sigma    rmse(w)  max|dvol|  iters  butterfly
 0.25   0.00420   0.0295  -0.6129   0.0336   0.2187    7.99E-5    1.52E-3      9  free (min g = 0.310)
 0.50  -0.00149   0.0641  -0.5455   0.0000   0.3383    1.19E-4    9.19E-4      6  free (min g = 0.258)
 1.00   0.01209   0.0896  -0.5825   0.0264   0.2454    2.88E-4    1.24E-3      7  free (min g = 0.290)
 2.00   0.01383   0.1655  -0.5209   0.0305   0.2830    6.29E-4    1.53E-3     11  free (min g = 0.262)

Counter-example SVI(a=-0.02, b=0.9, rho=-0.5, m=0, sigma=0.03): admissible wings = True, butterfly free = False, 307 violating grid points, min g = -4.672 at k = -0.05

== Calendar arbitrage between consecutive expiries (total variance must not decrease) ==
T 0.25 -> 0.50: free (min w_later - w_earlier = 0.00738)
T 0.50 -> 1.00: free (min w_later - w_earlier = 0.01322)
T 1.00 -> 2.00: free (min w_later - w_earlier = 0.02386)

== Implied volatility surface (columns: expiry; * = interpolated in total variance between fitted expiries) ==

   K/F   0.250    0.375*   0.500    0.750*   1.000    1.500*   2.000 
  0.80   27.42%   26.10%   25.42%   24.33%   23.76%   22.74%   22.20%
  0.90   23.96%   22.88%   22.32%   21.42%   20.96%   20.12%   19.69%
  0.95   22.50%   21.57%   21.09%   20.24%   19.80%   19.05%   18.66%
  1.00   21.29%   20.51%   20.10%   19.28%   18.86%   18.18%   17.83%
  1.05   20.39%   19.70%   19.35%   18.57%   18.16%   17.53%   17.20%
  1.10   19.78%   19.15%   18.82%   18.08%   17.70%   17.09%   16.78%
  1.20   19.30%   18.65%   18.32%   17.66%   17.32%   16.72%   16.42%

Call T=0.75 K=105: vol 18.66%, price 4.6473 (Black-76 on forward 100.7528, discount factor 0.985112)
```
