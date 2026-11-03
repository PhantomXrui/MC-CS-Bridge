#pragma once
#include "pawn_dance_shader.h"
#include "pawn_dance_glove_data.h"

// World gloves have hand/finger bones but no root_motion. Resolve their owner
// from both animated hands; the body's root is the only permitted dance pivot.
namespace pawn_dance_glove_shader {
using Microsoft::WRL::ComPtr;
constexpr unsigned MaxOwners=128;
struct alignas(16) Constants {
 pawn_dance_shader::WorldConstants dance;
 uint32_t ownerCount=0;
 float matchDistanceSquared=.0001f;
 uint32_t defaultSkeleton=0,reserved=0;
 uint32_t owners[MaxOwners][4]{}; // instance ID, body kind, root offset, reserved
};
static_assert(sizeof(Constants)==3136);
struct Compiled {
 ComPtr<ID3DBlob> bytecode;
 std::string source,error;
 explicit operator bool()const{return bytecode!=nullptr&&error.empty();}
};
struct Pass {
 ComPtr<ID3D11ComputeShader> shader;
 ComPtr<ID3D11Buffer> constants,result;
 ComPtr<ID3D11ShaderResourceView> view;
 ComPtr<ID3D11UnorderedAccessView> output;
 std::string error;
 explicit operator bool()const{return shader&&constants&&result&&view&&output&&error.empty();}
};
inline Compiled compile(){
 Compiled result;std::ostringstream source;source.precision(10);
 source<<"cbuffer GloveDance : register(b13) { float4 actorScale; uint applyTransform; uint actorCount; float maxDistance; float yawRadians; uint instanceId; uint rootBoneIndex; uint useInstanceRoot; uint allowAllVerifiedInstances; float4 actorFeet[64]; uint ownerCount; float matchDistanceSquared; uint defaultSkeleton; uint unused; uint4 owners[128]; };\n"
  <<"struct InstanceData {uint4 first;uint4 second;}; StructuredBuffer<InstanceData> instances : register(t14);\n"
  <<"struct Transform {float4 row0,row1,row2;}; StructuredBuffer<Transform> transforms : register(t15);\n"
  <<"float3 transformPoint(Transform m,float3 p) {float4 v=float4(p,1);return float3(dot(m.row0,v),dot(m.row1,v),dot(m.row2,v));}\n"
  <<"bool validOffset(uint base,uint offset,uint total) {return base<total&&offset<total-base;}\n";
 const auto &glove=pawn_dance_glove_data::gloveHands;
 source<<"static const uint2 gloveOffsets=uint2("<<glove.leftOffset<<','<<glove.rightOffset<<");\n"
  <<"static const float3 gloveLeft=float3("<<glove.leftBindPoint[0]<<','<<glove.leftBindPoint[1]<<','<<glove.leftBindPoint[2]<<");\n"
  <<"static const float3 gloveRight=float3("<<glove.rightBindPoint[0]<<','<<glove.rightBindPoint[1]<<','<<glove.rightBindPoint[2]<<");\n";
 constexpr unsigned count=pawn_dance_agent_data::KindCount+1;
 source<<"static const uint RigCount="<<pawn_dance_agent_data::KindCount<<";\nstatic const uint GloveKind="<<pawn_dance_agent_data::GloveKind<<";\n";
 source<<"static const uint3 bodyOffsets["<<count<<"]={";
 for(unsigned k=0;k<count;k++){const auto &h=pawn_dance_glove_data::hands[k];if(k)source<<',';source<<"uint3("<<h.leftOffset<<','<<h.rightOffset<<','<<pawn_dance_agent_data::roots[k]+2<<')';}
 source<<"};\nstatic const float3 bodyLeft["<<count<<"]={";
 for(unsigned k=0;k<count;k++){const auto &h=pawn_dance_glove_data::hands[k];if(k)source<<',';source<<"float3("<<h.leftBindPoint[0]<<','<<h.leftBindPoint[1]<<','<<h.leftBindPoint[2]<<')';}
 source<<"};\nstatic const float3 bodyRight["<<count<<"]={";
 for(unsigned k=0;k<count;k++){const auto &h=pawn_dance_glove_data::hands[k];if(k)source<<',';source<<"float3("<<h.rightBindPoint[0]<<','<<h.rightBindPoint[1]<<','<<h.rightBindPoint[2]<<')';}
 source<<R"HLSL(};
bool findOwner(uint p,out float3 pivot,out uint2 skeleton) {
 pivot=0;skeleton=0;uint count,stride,total,transformStride;
 instances.GetDimensions(count,stride);transforms.GetDimensions(total,transformStride);
 if(applyTransform==0||useInstanceRoot==0||allowAllVerifiedInstances==0||p>=min(ownerCount,128u)||instanceId>=count||stride!=32||transformStride!=48)return false;
 if(!isfinite(matchDistanceSquared)||matchDistanceSquared<=0||matchDistanceSquared>.0001)return false;
 uint gloveBase=instances[instanceId].first.y;
 uint2 handOffsets=defaultSkeleton!=0?bodyOffsets[1].xy:gloveOffsets;
 float3 leftBind=defaultSkeleton!=0?bodyLeft[1]:gloveLeft;
 float3 rightBind=defaultSkeleton!=0?bodyRight[1]:gloveRight;
 if(!validOffset(gloveBase,handOffsets.x,total)||!validOffset(gloveBase,handOffsets.y,total))return false;
 float gloveScale=transforms[gloveBase].row0.z;
 if(!isfinite(gloveScale)||gloveScale<.05||gloveScale>4)return false;
 float3 left=transformPoint(transforms[gloveBase+handOffsets.x],leftBind*gloveScale);
 float3 right=transformPoint(transforms[gloveBase+handOffsets.y],rightBind*gloveScale);
 if(!all(isfinite(left))||!all(isfinite(right)))return false;
 uint id=owners[p].x,kind=owners[p].y;
 if(id>=count||kind<1||kind>RigCount||kind==GloveKind)return false;
 uint base=instances[id].first.y;uint3 offsets=bodyOffsets[kind];
 if(offsets.x<2||offsets.y<2||owners[p].z!=offsets.z||!validOffset(base,offsets.x,total)||!validOffset(base,offsets.y,total)||!validOffset(base,offsets.z,total))return false;
 float scale=transforms[base].row0.z;
 if(!isfinite(scale)||scale<.05||scale>4)return false;
 float3 bodyL=transformPoint(transforms[base+offsets.x],bodyLeft[kind]*scale);
 float3 bodyR=transformPoint(transforms[base+offsets.y],bodyRight[kind]*scale);
 float3 dl=bodyL-left,dr=bodyR-right;
 if(!all(isfinite(bodyL))||!all(isfinite(bodyR))||dot(dl,dl)>=matchDistanceSquared||dot(dr,dr)>=matchDistanceSquared)return false;
 Transform root=transforms[base+offsets.z];pivot=float3(root.row0.w,root.row1.w,root.row2.w);
 skeleton=uint2(base,offsets.z);
 return all(isfinite(pivot));
}
RWStructuredBuffer<float4> ownerResultOut : register(u0);
groupshared float4 matches[128];
groupshared uint2 skeletons[128];
[numthreads(128,1,1)] void main(uint threadIndex : SV_GroupIndex) {
 float3 pivot;uint2 skeleton;bool valid=findOwner(threadIndex,pivot,skeleton);
 matches[threadIndex]=float4(pivot,valid?1.0:0.0);
 skeletons[threadIndex]=skeleton;
 GroupMemoryBarrierWithGroupSync();
 if(threadIndex==0){
  float3 selected=0;uint matched=0;uint2 selectedSkeleton=0;
  [loop]for(uint p=0;p<min(ownerCount,128u);p++)if(matches[p].w>0.5){
   if(matched==0){selected=matches[p].xyz;selectedSkeleton=skeletons[p];matched=1;}
   else if(any(skeletons[p]!=selectedSkeleton))matched=2;
  }
  // Body materials can use different instance IDs for the same bone palette.
  // Native code restricts all candidates to the same table views; base plus
  // root offset therefore identifies a skeleton. Distinct palettes remain
  // ambiguous even if their hands and root positions coincide.
  ownerResultOut[0]=float4(selected,matched==1?1.0:0.0);
 }
}
)HLSL";
 result.source=source.str();ComPtr<ID3DBlob> errors;
 if(FAILED(D3DCompile(result.source.data(),result.source.size(),"world_glove_owner",nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&result.bytecode,&errors))){
  result.error=errors?std::string(static_cast<const char *>(errors->GetBufferPointer()),errors->GetBufferSize()):"World glove owner CS compile failed";result.bytecode.Reset();
 }
 return result;
}
inline Pass create(ID3D11Device *device,const Compiled &compiled){
 Pass pass;if(!device||!compiled){pass.error="Missing world glove owner CS";return pass;}
 if(FAILED(device->CreateComputeShader(compiled.bytecode->GetBufferPointer(),compiled.bytecode->GetBufferSize(),nullptr,&pass.shader))){pass.error="World glove owner CS creation failed";return pass;}
 Constants initial;D3D11_BUFFER_DESC desc{};desc.ByteWidth=sizeof(Constants);desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;D3D11_SUBRESOURCE_DATA data{&initial,0,0};
 if(FAILED(device->CreateBuffer(&desc,&data,&pass.constants))){pass.error="World glove constants creation failed";return pass;}
 desc={};desc.ByteWidth=16;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;desc.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;desc.StructureByteStride=16;
 const float empty[4]={};data={empty,0,0};
 if(FAILED(device->CreateBuffer(&desc,&data,&pass.result))){pass.error="World glove result creation failed";return pass;}
 D3D11_SHADER_RESOURCE_VIEW_DESC view{};view.Format=DXGI_FORMAT_UNKNOWN;view.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;view.Buffer.NumElements=1;
 if(FAILED(device->CreateShaderResourceView(pass.result.Get(),&view,&pass.view))){pass.error="World glove result SRV creation failed";return pass;}
 D3D11_UNORDERED_ACCESS_VIEW_DESC output{};output.Format=DXGI_FORMAT_UNKNOWN;output.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;output.Buffer.NumElements=1;
 if(FAILED(device->CreateUnorderedAccessView(pass.result.Get(),&output,&pass.output)))pass.error="World glove result UAV creation failed";
 return pass;
}
}
