#!/usr/bin/env python3
"""Independent reference for the GARCH(1,1) log-likelihood tests (Python standard library only).

Generates a fixed return series with random.Random(20240607).gauss, evaluates the log-likelihood for a few parameter
sets with textbook formulas (math.lgamma for the Student-t constant) and prints C# literals to paste into
GarchLikelihoodReference in GarchTests.cs.

    python3 garch_loglik.py
"""
import math
import random

T = 250
rng = random.Random(20240607)
omega0, alpha0, beta0, mu0 = 2e-6, 0.09, 0.89, 4e-4
h = omega0 / (1 - alpha0 - beta0)
returns = []
for _ in range(T):
    eps = math.sqrt(h) * rng.gauss(0.0, 1.0)
    returns.append(mu0 + eps)
    h = omega0 + alpha0 * eps * eps + beta0 * h


def initial_variance(r, mu, init):
    e2 = [(x - mu) ** 2 for x in r]
    if init == "sample":
        return sum(e2) / len(e2)
    tau = min(75, len(r))
    w = [0.94 ** i for i in range(tau)]
    return sum(wi * ei for wi, ei in zip(w, e2[:tau])) / sum(w)


def loglik(r, mu, omega, alpha, beta, nu=None, init="sample"):
    h = initial_variance(r, mu, init)
    total = 0.0
    for x in r:
        e = x - mu
        if nu is None:
            total += -0.5 * (math.log(2 * math.pi) + math.log(h) + e * e / h)
        else:
            total += (math.lgamma((nu + 1) / 2) - math.lgamma(nu / 2) - 0.5 * math.log(math.pi * (nu - 2))
                      - 0.5 * math.log(h) - (nu + 1) / 2 * math.log(1 + e * e / (h * (nu - 2))))
        h = omega + alpha * e * e + beta * h
    return total, h  # h is now sigma^2_{T+1}


print("// returns")
print("{ " + ", ".join(repr(x) for x in returns) + " }")
cases = [
    ("normal, sample init", dict(mu=4e-4, omega=2e-6, alpha=0.09, beta=0.89, nu=None, init="sample")),
    ("normal, backcast init", dict(mu=4e-4, omega=2e-6, alpha=0.09, beta=0.89, nu=None, init="backcast")),
    ("normal, other params", dict(mu=0.0, omega=5e-6, alpha=0.05, beta=0.80, nu=None, init="sample")),
    ("student nu=6", dict(mu=4e-4, omega=2e-6, alpha=0.09, beta=0.89, nu=6.0, init="sample")),
    ("student nu=4.5 backcast", dict(mu=1e-4, omega=3e-6, alpha=0.07, beta=0.90, nu=4.5, init="backcast")),
]
for name, kw in cases:
    ll, nxt = loglik(returns, **kw)
    print(f"// {name}: {kw}\n//   logL = {ll!r}  sigma2_(T+1) = {nxt!r}")
