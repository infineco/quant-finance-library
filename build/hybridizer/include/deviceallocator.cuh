#pragma once
// Device-side allocator named by [IntrinsicType("hybridizer::deviceallocator<>")] in
// InFine.QuantFinance.Scheduling/AlignedIndex.cs. Copied from research/native/InFine.QuantFinance.Kepler.

namespace hybridizer
{
	template <typename T>
	struct deviceallocator
	{
		static __device__ T* Allocate (int size)
		{
			T* res = 0 ;
			::cudaMalloc ((void**) &res, sizeof(T) * size) ;
			return res ;
		}

		static __device__ void Free(T* buffer)
		{
			::cudaFree (buffer) ;
		}
	} ;
}
