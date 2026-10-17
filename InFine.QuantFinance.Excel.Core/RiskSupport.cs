using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>A covariance matrix with the names of its assets: what a <c>QF.Cov</c> handle holds.</summary>
    public sealed record CovarianceMatrix(double[,] Matrix, string[] Names)
    {
        public int Dimension => Matrix.GetLength(0);
    }

    /// <summary>
    /// Helpers for the asynchronous risk functions: a stable key for an Excel call (function name plus a digest of its
    /// arguments) and a measure of the size of a call, so the shim can run small ones inline and long ones in the
    /// background.
    /// </summary>
    public static class RiskCalls
    {
        /// <summary>
        /// A key that is equal for two calls of the same function with the same argument values, and different
        /// otherwise: <c>"QF.MCVAR|&lt;sha256&gt;"</c>. It identifies the call to Excel-DNA's asynchronous machinery, which
        /// otherwise could not tell whether a recalculation repeats the call in flight (reference equality on the argument
        /// arrays would never match).
        /// </summary>
        public static string Key(string function, params object?[] arguments) => function + "|" + Digest(arguments);

        /// <summary>Total number of cells held by the range arguments (a scalar counts for one).</summary>
        public static long CellCount(params object?[] arguments)
        {
            long cells = 0;
            foreach (var argument in arguments)
                cells += argument switch
                {
                    null => 0,
                    object[,] range => range.Length,
                    _ => 1,
                };
            return cells;
        }

        /// <summary>SHA-256 digest (hex) of values and ranges, independent of the culture and of array identity.</summary>
        internal static string Digest(params object?[] parts)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[8];
            void Add(object? part)
            {
                switch (part)
                {
                    case null:
                        sha.AppendData(new byte[] { 0 });
                        break;
                    case double d:
                        sha.AppendData(new byte[] { 1 });
                        BitConverter.TryWriteBytes(buffer, BitConverter.DoubleToInt64Bits(d));
                        sha.AppendData(buffer);
                        break;
                    case bool b:
                        sha.AppendData(new byte[] { 2, b ? (byte)1 : (byte)0 });
                        break;
                    case string s:
                        sha.AppendData(new byte[] { 3 });
                        sha.AppendData(Encoding.UTF8.GetBytes(s));
                        sha.AppendData(new byte[] { 0 });
                        break;
                    case double[,] matrix:
                        AddShape(matrix.GetLength(0), matrix.GetLength(1));
                        foreach (double d in matrix) Add(d);
                        break;
                    case double[] vector:
                        AddShape(vector.Length, 1);
                        foreach (double d in vector) Add(d);
                        break;
                    case string[] texts:
                        AddShape(texts.Length, 1);
                        foreach (string s in texts) Add(s);
                        break;
                    case object[,] range:
                        AddShape(range.GetLength(0), range.GetLength(1));
                        foreach (object? item in range) Add(item);
                        break;
                    default:
                        Add(Convert.ToString(part, CultureInfo.InvariantCulture));
                        break;
                }
            }
            void AddShape(int rows, int columns)
            {
                sha.AppendData(new byte[] { 4 });
                BitConverter.TryWriteBytes(buffer, ((long)rows << 32) | (uint)columns);
                sha.AppendData(buffer);
            }
            foreach (var part in parts) Add(part);
            return Convert.ToHexString(sha.GetHashAndReset());
        }
    }
}
