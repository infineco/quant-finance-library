using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Scheduling
{
    [IntrinsicType("hybridizer::deviceallocator<>")]
    [IntrinsicIncludeCUDA("deviceallocator.cuh")]
    [IntrinsicIncludePhi("deviceallocator.h")]
    public struct DeviceAllocator<T> where T : struct
    {
        public static T[] Allocate(int count) { return new T[count]; }
        public static void Free(T[] ar) { }
    }
}
