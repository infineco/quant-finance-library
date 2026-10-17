# 01 - Option pricing

Prices a 1-year at-the-money European option (S = K = 100, r = 2.5%, q = 1%, sigma = 20%) and shows:

- the Black-Scholes-Merton price and all analytic Greeks of the call and the put (`BlackScholes.Greeks`: delta, gamma,
  vega, theta, rho, dividend rho, vanna, volga), and the put-call parity residual;
- the implied-volatility round trip (`ImpliedVolatility.BlackScholes`, Jaeckel's "Let's Be Rational"): the price goes in,
  the 20% comes back to machine precision;
- the Heston price of the same option by the COS method (`Heston.Price`), expressed as a Black-76 implied volatility;
- a strike ladder from 80 to 120 with the Black-Scholes and Heston prices and the Heston smile.

Everything is closed form or semi-analytic: no random numbers, no timing.

## Run

```sh
dotnet run -c Release --project samples/01-OptionPricing
```

## Expected output

```text
== Black-Scholes-Merton: S=100 K=100 T=1y r=2.5% q=1% sigma=20% ==

                    Call         Put
Price           8.586530    7.112538
Delta           0.563794   -0.426256
Gamma           0.019449    0.019449
Vega           38.897079   38.897079
Theta/yr       -4.520736   -3.072511
Rho            47.792866  -49.738125
Div. rho      -56.379396   42.625587
Vanna           0.048621    0.048621
Volga          -0.850874   -0.850874

Put-call parity residual: -8.88E-15

== Implied volatility round trip ==
input sigma 0.2000000000; implied from the call 0.2000000000; from the put 0.2000000000
max |error| = 1.67E-16

== Heston (COS method) on the same option ==
Feller ratio 1.00 (variance stays positive: True)
Heston call 8.1584 (BSM 8.5865); put 6.6844 (BSM 7.1125); implied vol 18.90%

== Strike ladder, 1y calls: Black-Scholes at 20% vs Heston, with Heston implied vol ==

  Strike   BSM price      Heston   Heston IV   BSM delta    BSM vega
    80.0     21.9819     22.5458      22.99%      0.8926     17.1716
    85.0     17.9594     18.4437      21.93%      0.8300     24.2535
    90.0     14.3543     14.6308      20.89%      0.7511     30.8757
    95.0     11.2226     11.1782      19.88%      0.6603     35.9867
   100.0      8.5865      8.1584      18.90%      0.5638     38.8971
   105.0      6.4344      5.6356      17.97%      0.4678     39.4035
   110.0      4.7275      3.6503      17.11%      0.3777     37.7417
   115.0      3.4099      2.2007      16.34%      0.2972     34.4340
   120.0      2.4178      1.2321      15.68%      0.2284     30.1124
```

Heston with a negative spot-variance correlation (rho = -0.7) produces a downward-sloping smile, which Black-Scholes at one
volatility cannot. Theta is per year (divide by 365 for a daily figure); the convention of every Greek is documented on
`OptionGreeks`.
