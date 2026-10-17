using System;
using InFine.QuantFinance.ClosedForm;

namespace InFine.QuantFinance.Excel.Core
{
    public sealed partial class QuantFunctions
    {
        private static readonly string[] GreekHeaders =
            { "Price", "Delta", "Gamma", "Vega", "Theta", "Rho", "DivRho", "Vanna", "Volga", "DualDelta" };

        /// <summary>QF.BS.PRICE(type, S, K, T, r, q, vol)</summary>
        public object BsPrice(object? type, object? spot, object? strike, object? maturity, object? rate,
            object? dividendYield, object? vol) =>
            Run("QF.BS.PRICE", () => Finite(BlackScholes.Price(
                Names.ToOptionType(type, "type"), Arg.Double(spot, "S"), Arg.Double(strike, "K"), Arg.Double(maturity, "T"),
                Arg.Double(rate, "r"), Arg.Double(dividendYield, "q"), Arg.Double(vol, "vol"))));

        /// <summary>
        /// QF.BS.GREEKS(type, S, K, T, r, q, vol, [greek]) - one greek by name, or "ALL" (default) for a 1x10 row, or
        /// "HEADER" for a 2x10 block with a header row on top.
        /// </summary>
        public object BsGreeks(object? type, object? spot, object? strike, object? maturity, object? rate,
            object? dividendYield, object? vol, object? greek = null) =>
            Run("QF.BS.GREEKS", () =>
            {
                var g = BlackScholes.Greeks(
                    Names.ToOptionType(type, "type"), Arg.Double(spot, "S"), Arg.Double(strike, "K"), Arg.Double(maturity, "T"),
                    Arg.Double(rate, "r"), Arg.Double(dividendYield, "q"), Arg.Double(vol, "vol"));
                double[] values = { g.Price, g.Delta, g.Gamma, g.Vega, g.Theta, g.Rho, g.DividendRho, g.Vanna, g.Volga, g.DualDelta };

                string selector = Names.Normalize(Arg.OptionalText(greek, "greek") ?? "ALL");
                switch (selector)
                {
                    case "ALL":
                        return Row(values, withHeader: false);
                    case "HEADER" or "HEADERS" or "ALL+HEADER" or "ALLWITHHEADER" or "TABLE":
                        return Row(values, withHeader: true);
                }
                int index = selector switch
                {
                    "PRICE" => 0,
                    "DELTA" => 1,
                    "GAMMA" => 2,
                    "VEGA" => 3,
                    "THETA" => 4,
                    "RHO" => 5,
                    "DIVRHO" or "DIVIDENDRHO" or "PSI" => 6,
                    "VANNA" => 7,
                    "VOLGA" or "VOMMA" => 8,
                    "DUALDELTA" => 9,
                    _ => throw new XlException(XlError.Value,
                        $"greek: unknown greek '{greek}'. Expected one of: {string.Join(", ", GreekHeaders)}, ALL, HEADER"),
                };
                return Finite(values[index]);
            });

        /// <summary>QF.BLACK76.PRICE(type, F, K, T, DF, vol) - undiscounted forward F, discount factor DF to the payment date.</summary>
        public object Black76Price(object? type, object? forward, object? strike, object? maturity, object? discountFactor, object? vol) =>
            Run("QF.BLACK76.PRICE", () => Finite(Black76.Price(
                Names.ToOptionType(type, "type"), Arg.Double(forward, "F"), Arg.Double(strike, "K"),
                Arg.Double(discountFactor, "DF"), Arg.Double(maturity, "T"), Arg.Double(vol, "vol"))));

        /// <summary>QF.IMPLIEDVOL(price, type, S, K, T, r, q) - Black-Scholes-Merton implied volatility.</summary>
        public object ImpliedVol(object? price, object? type, object? spot, object? strike, object? maturity, object? rate,
            object? dividendYield) =>
            Run("QF.IMPLIEDVOL", () => Finite(ImpliedVolatility.BlackScholes(
                Arg.Double(spot, "S"), Arg.Double(strike, "K"), Arg.Double(maturity, "T"), Arg.Double(rate, "r"),
                Arg.Double(dividendYield, "q"), Arg.Double(price, "price"), Names.ToOptionType(type, "type"))));

        /// <summary>QF.IMPLIEDVOL.BLACK76(price, type, F, K, T, DF) - Black-76 implied volatility.</summary>
        public object ImpliedVolBlack76(object? price, object? type, object? forward, object? strike, object? maturity,
            object? discountFactor) =>
            Run("QF.IMPLIEDVOL.BLACK76", () => Finite(ImpliedVolatility.Black76(
                Arg.Double(forward, "F"), Arg.Double(strike, "K"), Arg.Double(maturity, "T"), Arg.Double(discountFactor, "DF"),
                Arg.Double(price, "price"), Names.ToOptionType(type, "type"))));

        private static object[,] Row(double[] values, bool withHeader)
        {
            var result = new object[withHeader ? 2 : 1, values.Length];
            int row = 0;
            if (withHeader)
                for (int j = 0; j < values.Length; j++) result[row, j] = GreekHeaders[j];
            if (withHeader) row++;
            for (int j = 0; j < values.Length; j++) result[row, j] = values[j];
            return result;
        }
    }
}
