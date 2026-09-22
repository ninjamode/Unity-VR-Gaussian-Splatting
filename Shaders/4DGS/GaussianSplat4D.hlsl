// SPDX-License-Identifier: MIT
#ifndef GAUSSIAN_SPLAT_4D_INCLUDED
#define GAUSSIAN_SPLAT_4D_INCLUDED
struct Gaussian4D
{
    float4 positionOpacity;
    float4 scale;
    float4 rotation; // xyzw, normalized
};
#endif
