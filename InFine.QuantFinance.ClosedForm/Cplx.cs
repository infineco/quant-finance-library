using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Minimal complex number for kernel code: a plain value type with the handful of operations the Fourier
    /// pricers need (arithmetic, principal square root, exponential and logarithm). No allocation, no exceptions,
    /// only <c>Math.*</c>, so Hybridizer can follow every call into it.
    /// <see cref="System.Numerics.Complex"/> is deliberately not used in kernel paths: it is a struct too, but its
    /// implementation is outside the code Hybridizer transforms.
    /// </summary>
    /// <remarks>
    /// Branch cuts are the principal ones, on the negative real axis: <see cref="Sqrt"/> returns the root with a
    /// non-negative real part and <see cref="Log"/> an argument in (-pi, pi].
    /// </remarks>
    public struct Cplx
    {
        /// <summary>Real part.</summary>
        public double Re;

        /// <summary>Imaginary part.</summary>
        public double Im;

        /// <summary>re + i im.</summary>
        public Cplx(double re, double im)
        {
            Re = re;
            Im = im;
        }

        /// <summary>Sum.</summary>
        public static Cplx operator +(Cplx a, Cplx b) { return new Cplx(a.Re + b.Re, a.Im + b.Im); }

        /// <summary>Sum with a real number.</summary>
        public static Cplx operator +(Cplx a, double b) { return new Cplx(a.Re + b, a.Im); }

        /// <summary>Sum with a real number.</summary>
        public static Cplx operator +(double a, Cplx b) { return new Cplx(a + b.Re, b.Im); }

        /// <summary>Difference.</summary>
        public static Cplx operator -(Cplx a, Cplx b) { return new Cplx(a.Re - b.Re, a.Im - b.Im); }

        /// <summary>Real number minus a complex number.</summary>
        public static Cplx operator -(double a, Cplx b) { return new Cplx(a - b.Re, -b.Im); }

        /// <summary>Negation.</summary>
        public static Cplx operator -(Cplx a) { return new Cplx(-a.Re, -a.Im); }

        /// <summary>Product.</summary>
        public static Cplx operator *(Cplx a, Cplx b)
        {
            return new Cplx(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re);
        }

        /// <summary>Product with a real number.</summary>
        public static Cplx operator *(double a, Cplx b) { return new Cplx(a * b.Re, a * b.Im); }

        /// <summary>Product with a real number.</summary>
        public static Cplx operator *(Cplx a, double b) { return new Cplx(a.Re * b, a.Im * b); }

        /// <summary>Quotient, by Smith's algorithm (no overflow of |b|^2 for large or small denominators).</summary>
        public static Cplx operator /(Cplx a, Cplx b)
        {
            if (Math.Abs(b.Re) >= Math.Abs(b.Im))
            {
                double r = b.Im / b.Re, den = b.Re + b.Im * r;
                return new Cplx((a.Re + a.Im * r) / den, (a.Im - a.Re * r) / den);
            }
            else
            {
                double r = b.Re / b.Im, den = b.Re * r + b.Im;
                return new Cplx((a.Re * r + a.Im) / den, (a.Im * r - a.Re) / den);
            }
        }

        /// <summary>Quotient by a real number.</summary>
        public static Cplx operator /(Cplx a, double b) { return new Cplx(a.Re / b, a.Im / b); }

        /// <summary>|z|, without intermediate overflow or underflow.</summary>
        public static double Abs(Cplx z)
        {
            double x = Math.Abs(z.Re), y = Math.Abs(z.Im);
            double big = x > y ? x : y, small = x > y ? y : x;
            if (big == 0.0 || double.IsInfinity(big)) return big;
            double r = small / big;
            return big * Math.Sqrt(1.0 + r * r);
        }

        /// <summary>exp(z).</summary>
        public static Cplx Exp(Cplx z)
        {
            double m = Math.Exp(z.Re);
            return new Cplx(m * Math.Cos(z.Im), m * Math.Sin(z.Im));
        }

        /// <summary>Principal logarithm: ln|z| + i arg z, arg in (-pi, pi].</summary>
        public static Cplx Log(Cplx z)
        {
            return new Cplx(Math.Log(Abs(z)), Math.Atan2(z.Im, z.Re));
        }

        /// <summary>
        /// Principal square root (non-negative real part), computed without cancellation: the larger component comes
        /// from sqrt((|z| + |Re z|) / 2) and the other one from Im z divided by twice it.
        /// </summary>
        public static Cplx Sqrt(Cplx z)
        {
            if (z.Re == 0.0 && z.Im == 0.0) return new Cplx(0.0, z.Im);
            double t = Math.Sqrt(0.5 * (Abs(z) + Math.Abs(z.Re)));
            if (z.Re >= 0.0) return new Cplx(t, 0.5 * z.Im / t);
            return new Cplx(0.5 * Math.Abs(z.Im) / t, z.Im >= 0.0 ? t : -t);
        }
    }
}
