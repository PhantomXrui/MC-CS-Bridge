#pragma once
#include <d3dcompiler.h>
#include <d3d11shader.h>
// MinGW's D3D11 header omits the shader-version helpers. Its D3D12 header
// supplies the same DXBC version-token definitions.
#if defined(__MINGW32__) && !defined(D3D11_SHVER_GET_TYPE)
#include <d3d12shader.h>
#define D3D11_SHVER_GET_TYPE D3D12_SHVER_GET_TYPE
#define D3D11_SHVER_VERTEX_SHADER D3D12_SHVER_VERTEX_SHADER
#define D3D11_SHVER_COMPUTE_SHADER D3D12_SHVER_COMPUTE_SHADER
// The bundled MinGW headers declare IID_ID3D11ShaderReflection but omit its
// __uuidof metadata. Match the header's compiler-version-specific IID.
#if D3D_COMPILER_VERSION <= 42
__CRT_UUID_DECL(ID3D11ShaderReflection, 0x17f27486, 0xa342, 0x4d10, 0x88, 0x42, 0xab, 0x08, 0x74, 0xe7, 0xf6, 0x70)
#elif D3D_COMPILER_VERSION == 43
__CRT_UUID_DECL(ID3D11ShaderReflection, 0x0a233719, 0x3960, 0x4578, 0x9d, 0x7c, 0x20, 0x3b, 0x8b, 0x1d, 0x9c, 0xc1)
#else
__CRT_UUID_DECL(ID3D11ShaderReflection, 0x8d536ca1, 0x0cca, 0x4956, 0xa8, 0x37, 0x78, 0x69, 0x63, 0x75, 0x55, 0x84)
#endif
#endif
