"""Independent reference values for the Risk tests (standard library only, no numpy).

Line-by-line transcription of the MATLAB code published by Olivier Ledoit and Michael Wolf (covCor.m for
"Honey, I shrunk the sample covariance matrix", 2004, and cov1Para.m for "A well-conditioned estimator for
large-dimensional covariance matrices", 2004), written with explicit matrix primitives in the same order of
operations as the MATLAB, so that it shares no code structure with the C# implementation (which loops over the
formulas of the papers). Also prints the sample covariance and the RiskMetrics EWMA of the same data.

Run:  python3 ledoit_wolf_reference.py
The data block printed first is pasted verbatim into LedoitWolfTests.cs / CovarianceTests.cs.
"""
import random

T, N = 12, 4


def make_data():
    rng = random.Random(20260101)
    # Correlated, heavy-ish tailed daily returns in percent, rounded to 4 decimals so the C# literal is exact.
    rows = []
    for _ in range(T):
        f = rng.gauss(0, 1)
        row = [round(0.8 * f * (1 + 0.3 * i) + rng.gauss(0, 1) * (0.6 + 0.2 * i) + (rng.random() < 0.15) * 2.5, 4)
               for i in range(N)]
        rows.append(row)
    return rows


# --- tiny dense-matrix primitives (lists of lists) -------------------------------------------------------
def zeros(r, c): return [[0.0] * c for _ in range(r)]
def transpose(a): return [list(col) for col in zip(*a)]
def matmul(a, b):
    bt = transpose(b)
    return [[sum(x * y for x, y in zip(row, col)) for col in bt] for row in a]
def scale(a, k): return [[k * v for v in row] for row in a]
def emul(a, b): return [[x * y for x, y in zip(ra, rb)] for ra, rb in zip(a, b)]
def eadd(a, b): return [[x + y for x, y in zip(ra, rb)] for ra, rb in zip(a, b)]
def esub(a, b): return [[x - y for x, y in zip(ra, rb)] for ra, rb in zip(a, b)]
def esq(a): return emul(a, a)
def ecube(a): return [[v ** 3 for v in row] for row in a]
def total(a): return sum(sum(row) for row in a)
def diag(a): return [a[i][i] for i in range(len(a))]
def fro2(a): return total(esq(a))


def demean(x):
    t = len(x)
    mean = [sum(col) / t for col in zip(*x)]
    return [[v - m for v, m in zip(row, mean)] for row in x]


def cov_cor(x_raw):
    """covCor.m: shrinkage towards constant correlation. Returns (sigma, shrinkage, sample)."""
    t, n = len(x_raw), len(x_raw[0])
    x = demean(x_raw)
    sample = scale(matmul(transpose(x), x), 1.0 / t)
    var = diag(sample)
    sqrtvar = [v ** 0.5 for v in var]
    # sqrtvar(:,ones(n,1)) is the matrix with sqrtvar_i in row i; its transpose has sqrtvar_j in column j.
    sv_col = [[sqrtvar[i]] * n for i in range(n)]
    sv_row = transpose(sv_col)
    outer = emul(sv_col, sv_row)
    ratio = [[sample[i][j] / outer[i][j] for j in range(n)] for i in range(n)]
    r_bar = (total(ratio) - n) / (n * (n - 1))
    prior = scale(outer, r_bar)
    for i in range(n): prior[i][i] = var[i]
    # pi-hat
    y = esq(x)
    xtx_t = scale(matmul(transpose(x), x), 1.0 / t)
    phi_mat = eadd(esub(scale(matmul(transpose(y), y), 1.0 / t), scale(emul(matmul(transpose(x), x), sample), 2.0 / t)), esq(sample))
    phi = total(phi_mat)
    # rho-hat
    term1 = scale(matmul(transpose(ecube(x)), x), 1.0 / t)
    help_ = xtx_t
    help_diag = diag(help_)
    var_col = [[var[i]] * n for i in range(n)]
    term2 = emul([[help_diag[i]] * n for i in range(n)], sample)
    term3 = emul(help_, var_col)
    term4 = emul(var_col, sample)
    theta_mat = eadd(esub(esub(term1, term2), term3), term4)
    for i in range(n): theta_mat[i][i] = 0.0
    inv_ratio = [[sqrtvar[j] / sqrtvar[i] for j in range(n)] for i in range(n)]  # (1./sqrtvar)*sqrtvar'
    rho = sum(phi_mat[i][i] for i in range(n)) + r_bar * total(emul(inv_ratio, theta_mat))
    # gamma-hat, shrinkage constant
    gamma = fro2(esub(sample, prior))
    kappa = (phi - rho) / gamma
    shrinkage = max(0.0, min(1.0, kappa / t))
    sigma = eadd(scale(prior, shrinkage), scale(sample, 1.0 - shrinkage))
    return sigma, shrinkage, sample, r_bar


def cov1_para(x_raw):
    """cov1Para.m: shrinkage towards the scaled identity. Returns (sigma, shrinkage)."""
    t, n = len(x_raw), len(x_raw[0])
    x = demean(x_raw)
    sample = scale(matmul(transpose(x), x), 1.0 / t)
    mn = sum(diag(sample)) / n
    prior = [[mn if i == j else 0.0 for j in range(n)] for i in range(n)]
    d2 = fro2(esub(sample, prior)) / n
    b2 = 0.0
    for k in range(t):
        xk = [[v] for v in x[k]]
        b2 += fro2(esub(matmul(xk, transpose(xk)), sample)) / n
    b2 = b2 / t ** 2
    b2 = min(b2, d2)
    shrinkage = b2 / d2
    sigma = eadd(scale(prior, shrinkage), scale(sample, 1.0 - shrinkage))
    return sigma, shrinkage


def sample_cov(x_raw, ddof):
    t = len(x_raw)
    x = demean(x_raw)
    return scale(matmul(transpose(x), x), 1.0 / (t - ddof))


def ewma(x, lam, seed_window):
    t, n = len(x), len(x[0])
    if seed_window == 0:
        s = [[x[0][i] * x[0][j] for j in range(n)] for i in range(n)]
        start = 1
    else:
        s = zeros(n, n)
        for k in range(seed_window):
            for i in range(n):
                for j in range(n):
                    s[i][j] += x[k][i] * x[k][j] / seed_window
        start = seed_window
    for k in range(start, t):
        s = [[lam * s[i][j] + (1 - lam) * x[k][i] * x[k][j] for j in range(n)] for i in range(n)]
    return s


def lit(m):
    return "{\n" + ",\n".join("    { " + ", ".join(repr(v) for v in row) + " }" for row in m) + "\n}"


if __name__ == "__main__":
    data = make_data()
    print("// data (T x N)")
    print(lit(data))
    sig, d, s, rbar = cov_cor(data)
    print("// covCor: delta =", repr(d), " rBar =", repr(rbar)); print(lit(sig))
    sig1, d1 = cov1_para(data)
    print("// cov1Para: delta =", repr(d1)); print(lit(sig1))
    print("// sample MLE (divisor T)"); print(lit(s))
    print("// sample unbiased (divisor T-1)"); print(lit(sample_cov(data, 1)))
    print("// ewma lambda=0.94 first outer product"); print(lit(ewma(data, 0.94, 0)))
    print("// ewma lambda=0.90 seedWindow=5"); print(lit(ewma(data, 0.90, 5)))
