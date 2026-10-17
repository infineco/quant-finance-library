"""Independent reference values for the path-dependent closed forms (standard library only, no numpy/scipy).

Every number is obtained by numerical integration of the law of the relevant functional of a geometric Brownian
motion, never by evaluating the Reiner-Rubinstein / Kemna-Vorst / Goldman-Sosin-Gatto / Conze-Viswanathan formulas
that the C# code implements:

* geometric Asian: ln G is Gaussian; its variance is the brute-force double sum sigma^2/n^2 sum_ij min(t_i, t_j)
  (the C# uses the O(n) weighted sum), and the price is a 1-D Simpson integral over the standard normal driver;
  the continuous case uses the exact moments of (1/T) int ln S. Average-strike: 1-D integral over D = ln S_T - ln G
  with the exact conditional moment E[exp(ln S_T) | D].
* barriers: reflection principle. With x = ln(S_T/S_0), nu = b - sigma^2/2 and h = ln(H/S_0), the density of x on
  paths that never touched the barrier is phi(x - nu T) - exp(2 nu h/sigma^2) phi(x - 2h - nu T) (phi = N(0, sigma^2 T)
  density); the rebate paid at the hit is integrated against the first-passage density
  |h| / (sigma sqrt(2 pi t^3)) exp(-(h - nu t)^2 / (2 sigma^2 t)); the knock-in is integrated against the complementary
  density (it is not obtained as vanilla - knock-out).
* lookbacks: E[(M - X)^+] etc. as integrals of the tail P(max >= a) = N((nu T - a)/s) + exp(2 nu a/sigma^2) N((-a - nu T)/s)
  (and the mirror image for the minimum), including a previously observed extremum.

Run:  python3 path_dependent_reference.py
The printed block is pasted verbatim (as constants) into PathDependentClosedFormTests.cs.
"""
import math

N = lambda x: 0.5 * math.erfc(-x / math.sqrt(2.0))
PHI = lambda x, mean, var: math.exp(-0.5 * (x - mean) ** 2 / var) / math.sqrt(2.0 * math.pi * var)


def simpson(f, a, b, n=4000):
    if b <= a:
        return 0.0
    n += n % 2
    h = (b - a) / n
    s = f(a) + f(b)
    for i in range(1, n):
        s += (4 if i % 2 else 2) * f(a + i * h)
    return s * h / 3.0


# ------------------------------------------------------------------ geometric Asian
def lognormal_call_put(sign, mean, var, strike, df):
    """E[(sign (e^Y - K))^+] df, Y ~ N(mean, var), by Simpson over the standard normal driver, kink located exactly."""
    sd = math.sqrt(var)
    z0 = (math.log(strike) - mean) / sd
    pay = lambda z: max(sign * (math.exp(mean + sd * z) - strike), 0.0) * PHI(z, 0.0, 1.0)
    lo, hi = (z0, 12.0) if sign > 0 else (-12.0, z0)
    return df * simpson(pay, lo, hi, 20000)


def asian_geo_discrete(sign, S, K, T, times, r, q, sig):
    n = len(times)
    mean = math.log(S) + (r - q - 0.5 * sig * sig) * sum(times) / n
    var = sig * sig * sum(min(a, b) for a in times for b in times) / (n * n)
    return lognormal_call_put(sign, mean, var, K, math.exp(-r * T))


def asian_geo_continuous(sign, S, K, T, r, q, sig, m=4000):
    # ln G = ln S + (1/T) int_0^T (nu t + sigma W_t) dt, Var = sigma^2 (1/T^2) int int min(s,t) ds dt, evaluated by midpoint rule
    nu = r - q - 0.5 * sig * sig
    mean = math.log(S) + nu * T / 2.0
    var = 0.0
    h = T / m
    # (1/T^2) int_0^T int_0^T min(s, t) ds dt = (1/T^2) int_0^T (t^2/2 + t (T - t)) dt ... integrate the inner exactly
    for i in range(m):
        t = (i + 0.5) * h
        var += (t * t / 2.0 + t * (T - t)) * h
    var *= sig * sig / (T * T)
    return lognormal_call_put(sign, mean, var, K, math.exp(-r * T))


def asian_geo_avg_strike(sign, S, T, times, r, q, sig, continuous=False):
    b = r - q
    if continuous:
        mY = math.log(S) + (b - 0.5 * sig * sig) * T / 2.0
        vY = sig * sig * T / 3.0
        cov = sig * sig * T / 2.0
    else:
        n = len(times)
        mY = math.log(S) + (b - 0.5 * sig * sig) * sum(times) / n
        vY = sig * sig * sum(min(a, c) for a in times for c in times) / (n * n)
        cov = sig * sig * sum(times) / n
    mX = math.log(S) + (b - 0.5 * sig * sig) * T
    vX = sig * sig * T
    mD, vD = mX - mY, vX + vY - 2.0 * cov
    covXD = vX - cov
    slope = covXD / vD
    vXgD = vX - covXD * covXD / vD

    def inner(d):
        # call: E[(e^X - e^Y)^+ | D = d] = e^X (1 - e^-d) 1_{d>0}; put: e^X (e^-d - 1) 1_{d<0}
        ex = math.exp(mX + slope * (d - mD) + 0.5 * vXgD)
        w = (1.0 - math.exp(-d)) if sign > 0 else (math.exp(-d) - 1.0)
        return ex * w * PHI(d, mD, vD)

    sd = math.sqrt(vD)
    lo, hi = (0.0, mD + 14 * sd) if sign > 0 else (mD - 14 * sd, 0.0)
    return math.exp(-r * T) * simpson(inner, lo, hi, 40000)


# ------------------------------------------------------------------ barrier
def barrier(sign, up, knock_in, S, K, H, R, T, r, q, sig):
    b = r - q
    nu = b - 0.5 * sig * sig
    var = sig * sig * T
    h = math.log(H / S)
    k = math.log(K / S)
    df = math.exp(-r * T)
    img = math.exp(2.0 * nu * h / (sig * sig))
    lo_all, hi_all = nu * T - 12 * math.sqrt(var), nu * T + 12 * math.sqrt(var)
    lo_all, hi_all = min(lo_all, h - 1.0, -1.0), max(hi_all, h + 1.0, 1.0)

    def pay(x):
        return max(sign * (S * math.exp(x) - K), 0.0)

    def survive(x):
        return PHI(x, nu * T, var) - img * PHI(x, 2.0 * h + nu * T, var)

    def hit_density(x):
        safe = (x > h) if not up else (x < h)
        return (img * PHI(x, 2.0 * h + nu * T, var)) if safe else PHI(x, nu * T, var)

    def integrate(fn, a, c, kinks):
        pts = sorted(set([a, c] + [p for p in kinks if a < p < c]))
        return sum(simpson(fn, pts[i], pts[i + 1], 6000) for i in range(len(pts) - 1))

    safe_lo, safe_hi = (h, hi_all) if not up else (lo_all, h)
    if not knock_in:
        value = df * integrate(lambda x: pay(x) * survive(x), safe_lo, safe_hi, [k, 0.0])
        # rebate paid at the hit: integral of R e^{-rt} g(t) dt, t = T u^2
        ah = abs(h)

        def reb(u):
            if u <= 0.0:
                return 0.0
            t = T * u * u
            g = ah / (sig * math.sqrt(2.0 * math.pi * t ** 3)) * math.exp(-(h - nu * t) ** 2 / (2.0 * sig * sig * t))
            return R * math.exp(-r * t) * g * 2.0 * T * u
        value += simpson(reb, 0.0, 1.0, 20000)
        return value
    value = df * integrate(lambda x: pay(x) * hit_density(x), lo_all, hi_all, [k, h])
    value += R * df * integrate(survive, safe_lo, safe_hi, [0.0])
    return value


# ------------------------------------------------------------------ lookback
def lookback(kind, sign, S, X, ext, T, r, q, sig):
    """kind 'float' or 'fixed'. ext = observed running min (call float / put fixed) or max (put float / call fixed)."""
    b = r - q
    nu = b - 0.5 * sig * sig
    s = sig * math.sqrt(T)
    df = math.exp(-r * T)
    pmax = lambda a: N((nu * T - a) / s) + math.exp(2.0 * nu * a / (sig * sig)) * N((-a - nu * T) / s)  # P(max >= a), a > 0
    pmin = lambda a: N((a - nu * T) / s) + math.exp(2.0 * nu * a / (sig * sig)) * N((a + nu * T) / s)   # P(min <= a), a < 0
    amax = max(nu * T, 0.0) + 14 * s + 1.0
    amin = min(nu * T, 0.0) - 14 * s - 1.0

    def exp_max_above(y0):   # int_{y0}^inf P(M_path >= ln(y/S)) dy, y0 >= S
        return simpson(lambda a: pmax(a) * S * math.exp(a), math.log(y0 / S), amax, 20000)

    def exp_min_below(y1):   # int_0^{y1} P(m_path <= ln(y/S)) dy, y1 <= S
        return simpson(lambda a: pmin(a) * S * math.exp(a), amin, math.log(y1 / S), 20000)

    if kind == 'float':
        if sign > 0:   # S_T - m_eff, m_eff = min(ext, path min)
            e_min = ext - exp_min_below(ext)
            return df * (S * math.exp(b * T) - e_min)
        e_max = ext + exp_max_above(ext)
        return df * (e_max - S * math.exp(b * T))
    if sign > 0:       # (M_eff - X)^+, M_eff = max(ext, path max)
        y0 = max(X, ext)
        return df * (max(ext - X, 0.0) + exp_max_above(y0))
    y1 = min(X, ext)   # (X - m_eff)^+
    return df * (max(X - ext, 0.0) + exp_min_below(y1))


if __name__ == '__main__':
    out = []

    def emit(name, value):
        out.append('%s = %.14e' % (name, value))

    S, r, q, sig = 100.0, 0.05, 0.02, 0.25
    T = 1.0
    # Asian geometric: 12 equally spaced fixings, irregular fixings, continuous
    eq12 = [T * k / 12 for k in range(1, 13)]
    irr = [0.1, 0.15, 0.4, 0.55, 0.9, 1.0]
    for sign, nm in ((1, 'Call'), (-1, 'Put')):
        emit('GeoEq12_%s_K100' % nm, asian_geo_discrete(sign, S, 100.0, T, eq12, r, q, sig))
        emit('GeoEq12_%s_K90' % nm, asian_geo_discrete(sign, S, 90.0, T, eq12, r, q, sig))
        emit('GeoIrregular_%s_K105' % nm, asian_geo_discrete(sign, S, 105.0, 1.0, irr, r, q, sig))
        emit('GeoContinuous_%s_K100' % nm, asian_geo_continuous(sign, S, 100.0, T, r, q, sig))
        emit('GeoContinuous_%s_K110_T2' % nm, asian_geo_continuous(sign, S, 110.0, 2.0, 0.03, 0.0, 0.2))
        emit('GeoAvgStrikeEq12_%s' % nm, asian_geo_avg_strike(sign, S, T, eq12, r, q, sig))
        emit('GeoAvgStrikeIrregular_%s' % nm, asian_geo_avg_strike(sign, S, T, irr, r, q, sig))
        emit('GeoAvgStrikeContinuous_%s' % nm, asian_geo_avg_strike(sign, S, T, None, r, q, sig, continuous=True))

    # (strike, barrier) pairs covering K > H and K < H on both sides of the spot
    DOWN_CASES = ((100.0, 90.0), (100.0, 95.0), (110.0, 90.0), (90.0, 85.0), (90.0, 95.0))
    UP_CASES = ((100.0, 115.0), (100.0, 108.0), (110.0, 125.0), (90.0, 120.0), (110.0, 108.0))
    # Barriers: S=100, T=1, r=5%, q=2%, sigma=25%, rebate 3 (and 0)
    for up in (False, True):
        for knock_in in (False, True):
            for sign in (1, -1):
                for K, H in (DOWN_CASES if not up else UP_CASES):
                    for R in (0.0, 3.0):
                        nm = 'Barrier_%s_%s_%s_K%d_H%d_R%d' % ('Up' if up else 'Down', 'In' if knock_in else 'Out', 'Call' if sign > 0 else 'Put', K, H, R)
                        emit(nm, barrier(sign, up, knock_in, S, K, H, R, T, r, q, sig))
    # zero carry (b = 0) and a different maturity
    emit('Barrier_ZeroCarry_DownOut_Call', barrier(1, False, False, 100.0, 100.0, 90.0, 2.0, 0.5, 0.04, 0.04, 0.3))
    emit('Barrier_ZeroCarry_UpIn_Put', barrier(-1, True, True, 100.0, 100.0, 120.0, 2.0, 0.5, 0.04, 0.04, 0.3))

    # Lookbacks
    for sign, nm in ((1, 'Call'), (-1, 'Put')):
        emit('LookFloat_%s_New' % nm, lookback('float', sign, S, None, S, T, r, q, sig))
        emit('LookFloat_%s_Seasoned' % nm, lookback('float', sign, S, None, 92.0 if sign > 0 else 109.0, T, r, q, sig))
        emit('LookFloat_%s_ZeroCarry' % nm, lookback('float', sign, S, None, S, 0.75, 0.03, 0.03, 0.3))
        emit('LookFixed_%s_ATM' % nm, lookback('fixed', sign, S, 100.0, S, T, r, q, sig))
        emit('LookFixed_%s_OTM' % nm, lookback('fixed', sign, S, 115.0 if sign > 0 else 85.0, S, T, r, q, sig))
        emit('LookFixed_%s_ITM_Seasoned' % nm, lookback('fixed', sign, S, 95.0 if sign > 0 else 105.0, 104.0 if sign > 0 else 96.0, T, r, q, sig))
        emit('LookFixed_%s_ZeroCarry' % nm, lookback('fixed', sign, S, 100.0, S, 0.75, 0.03, 0.03, 0.3))
    print('\n'.join(out))
