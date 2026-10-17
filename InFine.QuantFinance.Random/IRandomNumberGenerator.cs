namespace InFine.QuantFinance.Random
{
    public enum GaussianTransformEnum
    {
        NONE,
        InverseCumulativeDistributionFunction,
        BoxMuller,
        BoxMuller2,
    } ;

    public enum UniformTransformEnum
    {
        NONE,
        STANDARD,
        UNBIAISED,
    } ;

    interface IRandomNumberGenerator
    {
        void GenerateUniforms(UniformTransformEnum transform, double leftbound, double rightbound, int count, double[] target);
        void GenerateGaussians(GaussianTransformEnum transform, double mean, double var, int count, double[] target);
    }
}
