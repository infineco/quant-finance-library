#pragma once
// Device helpers named by [IntrinsicFunction] attributes in InFine.QuantFinance.Scheduling/Tools.cs.
// Adapted from research/native/InFine.QuantFinance.Kepler/quantfinance.tools.h: the mask-less __shfl_xor of the
// Kepler era no longer exists for sm_70 and later (CUDA 9+), so the warp reduction uses __shfl_xor_sync on the
// full warp. Callers must therefore reach it with all 32 lanes of the warp active.

namespace hybridizer
{
	hyb_device hyb_inline int atomicInc_int(int *address, int val) { return (int) atomicInc((unsigned int *) address, (unsigned int) val); }

	template <typename T>
	hyb_device hyb_inline
	T reduce_warp_add(const T& i) {
		T val = i;
		#pragma unroll
		for (int mask = warpSize/2; mask > 0; mask /= 2)
			val += __shfl_xor_sync(0xffffffffu, val, mask);
		return val;
	}
}
