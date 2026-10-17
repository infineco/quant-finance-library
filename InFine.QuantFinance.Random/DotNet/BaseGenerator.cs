using System;

namespace InFine.QuantFinance.Random.DotNet
{
    public class BaseGenerator : IRandomNumberGenerator
    {
        public virtual void GenerateUniforms(UniformTransformEnum transform, double leftbound, double rightbound, int count, double[] target)
        {
            throw new NotImplementedException();
        }

        public virtual void GenerateGaussians(GaussianTransformEnum transform, double mean, double stdev, int count, double[] target)
        {
            throw new NotImplementedException();
        }
    }
}
