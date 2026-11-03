#pragma once
#include "pawn_dance_shader.h"
#include "pawn_dance_weapon_data.h"

// Independent attachment pass. The verified original pawn shader is unchanged.
namespace pawn_dance_weapon_shader {
using Microsoft::WRL::ComPtr;
constexpr unsigned MaxOwners=128;
struct alignas(16) Constants {
 pawn_dance_shader::WorldConstants dance;
 float weaponPoints[2][4]{};
 uint32_t ownerCount=0,hasHolster=0;
 float matchDistanceSquared=.0001f;
 uint32_t reserved=0;
 uint32_t owners[MaxOwners][4]{}; // instance ID, pawn kind, root transform offset, reserved
};
static_assert(sizeof(Constants)==3168);
struct Compiled {
 ComPtr<ID3DBlob> bytecode,ownerBytecode;
 std::string source,ownerSource,error;
 unsigned perViewSlot=0,csgoSlot=0,materialSlot=0;
 bool needsCsgo=false,needsMaterial=false;
 explicit operator bool()const{return bytecode!=nullptr&&ownerBytecode!=nullptr&&error.empty();}
};
struct Pass {
 ComPtr<ID3D11GeometryShader> shader;
 ComPtr<ID3D11Buffer> constants;
 ComPtr<ID3D11ComputeShader> ownerShader;
 ComPtr<ID3D11Buffer> ownerBuffer;
 ComPtr<ID3D11ShaderResourceView> ownerView;
 ComPtr<ID3D11UnorderedAccessView> ownerOutput;
 std::string error;
 explicit operator bool()const{return shader&&constants&&ownerShader&&ownerBuffer&&ownerView&&ownerOutput&&error.empty();}
};
inline Compiled compile(const void *code,size_t bytes){
 Compiled result;
 if(!code||bytes<32||bytes>256*1024){result.error="Invalid weapon VS bytecode";return result;}
 ComPtr<ID3D11ShaderReflection> reflection;
 if(FAILED(D3DReflect(code,bytes,__uuidof(ID3D11ShaderReflection),reinterpret_cast<void **>(reflection.GetAddressOf())))){result.error="Weapon VS reflection failed";return result;}
 D3D11_SHADER_DESC desc{};reflection->GetDesc(&desc);
 if(D3D11_SHVER_GET_TYPE(desc.Version)!=D3D11_SHVER_VERTEX_SHADER||!desc.OutputParameters||desc.OutputParameters>32){result.error="Unsupported weapon VS";return result;}
 std::ostringstream fields,copy;
 std::unordered_set<std::string> semantics;
 int position=-1,worldPosition=-1,normal=-1,normal2=-1,tangent=-1,clip=-1,baseClip=-1,maskWorld=-1,maskDepth=-1;
 unsigned components=0;
 for(unsigned i=0;i<desc.OutputParameters;i++){
  D3D11_SIGNATURE_PARAMETER_DESC p{};
  if(FAILED(reflection->GetOutputParameterDesc(i,&p))||!pawn_dance_shader::identifier(p.SemanticName)||!pawn_dance_shader::supported_system_value(p.SystemValueType)||p.Stream||!p.Mask||(p.Mask&~15u)){result.error="Unsupported weapon output";return result;}
  if(!semantics.insert(std::string(p.SemanticName)+std::to_string(p.SemanticIndex)).second){result.error="Duplicate weapon output";return result;}
  const unsigned width=pawn_dance_shader::components(p.Mask);components+=width;
  const char *type=p.ComponentType==D3D_REGISTER_COMPONENT_FLOAT32?"float":p.ComponentType==D3D_REGISTER_COMPONENT_UINT32?"uint":p.ComponentType==D3D_REGISTER_COMPONENT_SINT32?"int":nullptr;
  if(!type){result.error="Unknown weapon scalar";return result;}
  if(p.SystemValueType==D3D_NAME_POSITION){if(p.ComponentType!=D3D_REGISTER_COMPONENT_FLOAT32||p.Mask!=15){result.error="Invalid weapon clip position";return result;}position=int(i);}
  if(p.ComponentType==D3D_REGISTER_COMPONENT_FLOAT32){
   if(!strcmp(p.SemanticName,"TEXCOORD")){
    if(p.SemanticIndex==0&&width==3)worldPosition=int(i);
    if(p.SemanticIndex==3&&width==3)maskWorld=int(i);
    if(p.SemanticIndex==6&&width==2)maskDepth=int(i);
    if(p.SemanticIndex==1&&width==3)normal=int(i);
    if(p.SemanticIndex==5&&width==3)normal2=int(i);
    if(p.SemanticIndex==6&&width==4)tangent=int(i);
    if(p.SemanticIndex==9&&width==1)baseClip=int(i);
   }
   if(p.SystemValueType==D3D_NAME_CLIP_DISTANCE&&p.SemanticIndex==0&&width==1)clip=int(i);
  }
  if(p.ComponentType!=D3D_REGISTER_COMPONENT_FLOAT32)fields<<"nointerpolation ";
  fields<<type;if(width>1)fields<<width;fields<<" value"<<i<<" : "<<p.SemanticName<<p.SemanticIndex<<";\n";
  copy<<"o.value"<<i<<"=input[v].value"<<i<<";\n";
 }
 const bool mask=desc.OutputParameters==3&&maskWorld>=0&&maskDepth>=0&&worldPosition<0&&clip<0;
 if(position<0||(!mask&&clip<0)||components+1>128){result.error="Unsupported weapon position/clip signature";return result;}
 auto variable=[&](const char *buffer,const char *name,unsigned offset,unsigned size){D3D11_SHADER_VARIABLE_DESC v{};return SUCCEEDED(reflection->GetConstantBufferByName(buffer)->GetVariableByName(name)->GetDesc(&v))&&v.StartOffset==offset&&v.Size==size;};
 if(!variable("PerViewConstantBuffer_t","g_matWorldToProjection",0,64)||!variable("PerViewConstantBuffer_t","g_matProjectionToWorld",64,64)||!variable("PerViewConstantBuffer_t","g_vClipPlane0",272,16)||!variable("PerViewConstantBuffer_t","g_vWorldToCameraOffset",528,16)){result.error="Unverified weapon projection layout";return result;}
 D3D11_SHADER_INPUT_BIND_DESC binding{};
 if(FAILED(reflection->GetResourceBindingDescByName("PerViewConstantBuffer_t",&binding))){result.error="Missing weapon view CB";return result;}result.perViewSlot=binding.BindPoint;
 if(FAILED(reflection->GetResourceBindingDescByName("g_instanceBuffer",&binding))){result.error="Missing weapon instance buffer";return result;}
 if(worldPosition>=0){
  if(!variable("PerViewConstantBufferCsgo_t","g_vHighPrecisionLightingOffsetWs",496,16)||FAILED(reflection->GetResourceBindingDescByName("PerViewConstantBufferCsgo_t",&binding))){result.error="Unverified weapon world varying";return result;}
  result.needsCsgo=true;result.csgoSlot=binding.BindPoint;
 }
 D3D11_SHADER_VARIABLE_DESC playerPlane{},firstPerson{},preview{};
 auto globals=reflection->GetConstantBufferByName("$Globals");
 const bool hasPlayerPlane=SUCCEEDED(globals->GetVariableByName("g_vPlayerRenderClipPlane")->GetDesc(&playerPlane));
 const bool hasFirstPerson=SUCCEEDED(globals->GetVariableByName("g_bRenderingFirstPersonLegs")->GetDesc(&firstPerson));
 const bool hasPreview=SUCCEEDED(globals->GetVariableByName("g_bStickerProjectionPreview")->GetDesc(&preview));
 if(hasPlayerPlane||hasFirstPerson||hasPreview){
  if(FAILED(reflection->GetResourceBindingDescByName("$Globals",&binding))||(hasPlayerPlane&&(playerPlane.Size!=16||playerPlane.StartOffset%16))||(hasFirstPerson&&(firstPerson.Size!=4||firstPerson.StartOffset%4))||(hasPreview&&(preview.Size!=4||preview.StartOffset%4))){result.error="Unverified weapon material CB";return result;}
  result.needsMaterial=true;result.materialSlot=binding.BindPoint;
 }
 std::ostringstream source;
 source.precision(10);
 source<<"cbuffer WeaponDance : register(b13) { float4 actorScale; uint applyTransform; uint actorCount; float maxDistance; float yawRadians; uint instanceId; uint rootBoneIndex; uint useInstanceRoot; uint allowAllVerifiedInstances; float4 actorFeet[64]; float4 weaponPoints[2]; uint ownerCount; uint hasHolster; float matchDistanceSquared; uint unused1; uint4 owners[128]; };\n"
  <<"cbuffer OriginalPerView : register(b12) { column_major float4x4 worldToProjection : packoffset(c0); column_major float4x4 projectionToWorld : packoffset(c4); float4 clipPlane : packoffset(c17); float4 worldToCameraOffset : packoffset(c33); };\n";
 if(result.needsCsgo)source<<"cbuffer OriginalCsgoView : register(b11) {float4 lightingOffset : packoffset(c31);};\n";
 if(result.needsMaterial){
  source<<"cbuffer OriginalMaterial : register(b10) {\n";
  if(hasPlayerPlane)source<<"float4 playerClipPlane : packoffset(c"<<playerPlane.StartOffset/16<<");\n";
  if(hasFirstPerson)source<<"uint firstPersonLegs : packoffset(c"<<firstPerson.StartOffset/16<<'.'<<"xyzw"[(firstPerson.StartOffset%16)/4]<<");\n";
  if(hasPreview)source<<"uint stickerPreview : packoffset(c"<<preview.StartOffset/16<<'.'<<"xyzw"[(preview.StartOffset%16)/4]<<");\n";
  source<<"};\n";
 }
 source<<"struct InstanceData {uint4 first;uint4 second;}; StructuredBuffer<InstanceData> instances : register(t14);\n"
  <<"struct Transform {float4 row0,row1,row2;}; StructuredBuffer<Transform> transforms : register(t15);\n"
  <<"float3 transformPoint(Transform m,float3 p) {float4 v=float4(p,1);return float3(dot(m.row0,v),dot(m.row1,v),dot(m.row2,v));}\n"
  <<"float3 rotate(float3 p,float sn,float cs) {return float3(cs*p.x-sn*p.y,sn*p.x+cs*p.y,p.z);}\n"
  <<"bool validOffset(uint base,uint offset,uint total) {return base<total&&offset<total-base;}\n";
 const unsigned anchorCount=unsigned(sizeof(pawn_dance_weapon_data::anchors)/sizeof(pawn_dance_weapon_data::anchors[0]));
 source<<"static const uint AnchorCount="<<anchorCount<<";\nstatic const uint3 anchorInfo["<<anchorCount<<"]={";
 for(unsigned i=0;i<anchorCount;i++){const auto &a=pawn_dance_weapon_data::anchors[i];if(i)source<<',';source<<"uint3("<<a.kind<<','<<a.transformOffset<<','<<a.weaponPoint<<')';}source<<"};\nstatic const float3 anchorPoints["<<anchorCount<<"]={";
 for(unsigned i=0;i<anchorCount;i++){const auto &a=pawn_dance_weapon_data::anchors[i];if(i)source<<',';source<<"float3("<<a.point[0]<<','<<a.point[1]<<','<<a.point[2]<<')';}source<<"};\n";
 source<<"static const uint RigCount="<<pawn_dance_agent_data::KindCount<<";\nstatic const uint GloveKind="<<pawn_dance_agent_data::GloveKind<<";\nstatic const uint2 anchorRanges["<<pawn_dance_agent_data::KindCount+1<<"]={";
 for(unsigned i=0;i<=pawn_dance_agent_data::KindCount;i++){if(i)source<<',';source<<"uint2("<<pawn_dance_agent_data::anchorRanges[i][0]<<','<<pawn_dance_agent_data::anchorRanges[i][1]<<')';}source<<"};\n";
 source<<R"HLSL(
bool findOwnerCandidate(uint p,out float3 pivot) {
 pivot=0; uint count,stride,total,transformStride; instances.GetDimensions(count,stride); transforms.GetDimensions(total,transformStride);
 if(applyTransform==0||p>=min(ownerCount,128u)||instanceId>=count||stride!=32||transformStride!=48)return false;
 uint weaponBase=instances[instanceId].first.y;
 if(!validOffset(weaponBase,2,total)||(instances[instanceId].second.z&15u)==0)return false;
 float weaponScale=transforms[weaponBase].row0.z;
 if(!isfinite(weaponScale)||weaponScale<.05||weaponScale>4)return false;
 Transform weaponRoot=transforms[weaponBase+2];
 float3 weaponWorld[2];weaponWorld[0]=transformPoint(weaponRoot,weaponPoints[0].xyz*weaponScale);weaponWorld[1]=transformPoint(weaponRoot,weaponPoints[1].xyz*weaponScale);
  uint id=owners[p].x,kind=owners[p].y;if(id>=count||kind<1||kind>RigCount||kind==GloveKind)return false;
  uint base=instances[id].first.y;if(!validOffset(base,owners[p].z,total))return false;
  float modelScale=transforms[base].row0.z;if(!isfinite(modelScale)||modelScale<.05||modelScale>4)return false;
  bool sameSocket=false;
  [loop]for(uint a=anchorRanges[kind].x;a<anchorRanges[kind].y;a++){
   uint3 info=anchorInfo[a];if(info.x!=kind||(info.z==1&&hasHolster==0)||!validOffset(base,info.y,total))continue;
   float3 socket=transformPoint(transforms[base+info.y],anchorPoints[a]*modelScale);
   float3 difference=socket-weaponWorld[info.z];
   if(all(isfinite(socket))&&dot(difference,difference)<matchDistanceSquared){sameSocket=true;break;}
  }
  if(!sameSocket)return false;
  Transform root=transforms[base+owners[p].z];float3 foot=float3(root.row0.w,root.row1.w,root.row2.w);
  if(!all(isfinite(foot)))return false;
  // Playback only bypasses the server's actor-position list. The verified
  // same-frame owner, instance tables and exact weapon socket remain required.
  bool authorized=allowAllVerifiedInstances!=0&&useInstanceRoot!=0;
  [loop]for(uint a=0;a<min(actorCount,64u);a++){float3 d=foot-actorFeet[a].xyz;if(dot(d,d)<maxDistance*maxDistance){authorized=true;break;}}
  if(!authorized)return false;
  pivot=foot;return true;
}
)HLSL";
 // Resolve candidates in parallel for this draw, then reduce in the original
 // owner order. Every thread reaches the barrier, including invalid candidates.
 // The first matching pivot and ambiguous-owner rejection remain unchanged.
 result.ownerSource=source.str()+R"HLSL(
RWStructuredBuffer<float4> ownerResultOut : register(u0);
groupshared float4 ownerCandidates[128];
[numthreads(128,1,1)] void main(uint threadIndex : SV_GroupIndex) {
 float3 foot;bool valid=findOwnerCandidate(threadIndex,foot);
 ownerCandidates[threadIndex]=float4(foot,valid?1.0:0.0);
 GroupMemoryBarrierWithGroupSync();
 if(threadIndex==0){
  float3 pivot=0;bool matched=false,ambiguous=false;
  [loop]for(uint p=0;p<min(ownerCount,128u);p++){
   float4 candidate=ownerCandidates[p];if(candidate.w==0)continue;
   if(matched){float3 d=candidate.xyz-pivot;if(dot(d,d)>.0001)ambiguous=true;}
   else{pivot=candidate.xyz;matched=true;}
  }
  ownerResultOut[0]=float4(pivot,matched&&!ambiguous?1.0:0.0);
 }
}
)HLSL";
 ComPtr<ID3DBlob> ownerErrors;
 if(FAILED(D3DCompile(result.ownerSource.data(),result.ownerSource.size(),"original_weapon_owner",nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&result.ownerBytecode,&ownerErrors))){result.error=ownerErrors?std::string(static_cast<const char *>(ownerErrors->GetBufferPointer()),ownerErrors->GetBufferSize()):"Weapon owner CS compile failed";result.ownerBytecode.Reset();return result;}
 source<<"StructuredBuffer<float4> ownerResult : register(t16);\n";
 source<<"struct Input {"<<fields.str()<<"};\nstruct Output {"<<fields.str()<<"uint primitive : SV_PrimitiveID;};\n"
  <<"[maxvertexcount(3)] void main(triangle Input input[3],uint primitive : SV_PrimitiveID,inout TriangleStream<Output> stream) {float4 owner=ownerResult[0];float3 pivot=owner.xyz;bool selected=owner.w>0.5;";
 if(hasPreview)source<<"selected=selected&&stickerPreview==0;";
 source<<"float sn,cs;sincos(yawRadians,sn,cs);float3 scale=max(actorScale.xyz,float3(.05,.05,.05));\n[unroll]for(uint v=0;v<3;v++){Output o;"<<copy.str()<<"if(selected){\n";
 if(mask)source<<"float3 before=input[v].value"<<maskWorld<<";\n";
 else if(worldPosition>=0)source<<"float3 before=input[v].value"<<worldPosition<<"+lightingOffset.xyz;\n";
 else source<<"float4 homogeneous=mul(projectionToWorld,input[v].value"<<position<<");float3 before=homogeneous.xyz/homogeneous.w;\n";
 source<<"float3 world=pivot+rotate((before-pivot)*scale,sn,cs);o.value"<<position<<"=mul(worldToProjection,float4(world+worldToCameraOffset.xyz,1));\n";
 if(worldPosition>=0)source<<"o.value"<<worldPosition<<"=world-lightingOffset.xyz;\n";
 if(mask)source<<"o.value"<<maskWorld<<"=world;o.value"<<maskDepth<<"=o.value"<<position<<".zw;\n";
 if(normal>=0)source<<"o.value"<<normal<<"=normalize(rotate(input[v].value"<<normal<<"/scale,sn,cs));\n";
 if(normal2>=0)source<<"o.value"<<normal2<<"=normalize(rotate(input[v].value"<<normal2<<"/scale,sn,cs));\n";
 if(tangent>=0){source<<"float3 tangent=rotate(input[v].value"<<tangent<<".xyz*scale,sn,cs);";int n=normal2>=0?normal2:normal;if(n>=0)source<<"tangent-=o.value"<<n<<"*dot(tangent,o.value"<<n<<");";source<<"o.value"<<tangent<<".xyz=normalize(tangent);\n";}
 source<<"float4 plane=clipPlane;\n";
 if(hasPlayerPlane){source<<"if(";if(baseClip>=0)source<<"input[v].value"<<baseClip<<">=0";else source<<"dot(float4(before,1),clipPlane)>=0";if(hasFirstPerson)source<<"&&firstPersonLegs==0";source<<")plane=float4(playerClipPlane.xyz,-playerClipPlane.w);\n";}
 if(clip>=0)source<<"o.value"<<clip<<"=dot(float4(world,1),plane);\n";
 if(baseClip>=0)source<<"o.value"<<baseClip<<"=dot(float4(world,1),clipPlane);\n";
 source<<"}o.primitive=primitive;stream.Append(o);}stream.RestartStrip();}\n";
 result.source=source.str();ComPtr<ID3DBlob> errors;
 if(FAILED(D3DCompile(result.source.data(),result.source.size(),"original_weapon_dance",nullptr,nullptr,"main","gs_5_0",D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&result.bytecode,&errors))){result.error=errors?std::string(static_cast<const char *>(errors->GetBufferPointer()),errors->GetBufferSize()):"Weapon GS compile failed";result.bytecode.Reset();}
 return result;
}
inline Pass create(ID3D11Device *device,const Compiled &compiled){
 Pass pass;if(!device||!compiled){pass.error="Missing weapon GS/owner CS";return pass;}
 if(FAILED(device->CreateGeometryShader(compiled.bytecode->GetBufferPointer(),compiled.bytecode->GetBufferSize(),nullptr,&pass.shader))){pass.error="Weapon GS creation failed";return pass;}
 if(FAILED(device->CreateComputeShader(compiled.ownerBytecode->GetBufferPointer(),compiled.ownerBytecode->GetBufferSize(),nullptr,&pass.ownerShader))){pass.error="Weapon owner CS creation failed";return pass;}
 Constants initial;D3D11_BUFFER_DESC desc{};desc.ByteWidth=sizeof(Constants);desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;D3D11_SUBRESOURCE_DATA data{&initial,0,0};
 if(FAILED(device->CreateBuffer(&desc,&data,&pass.constants))){pass.error="Weapon constants creation failed";return pass;}
 desc={};desc.ByteWidth=16;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;desc.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;desc.StructureByteStride=16;
 const float empty[4]={};data={empty,0,0};
 if(FAILED(device->CreateBuffer(&desc,&data,&pass.ownerBuffer))){pass.error="Weapon owner result buffer creation failed";return pass;}
 D3D11_SHADER_RESOURCE_VIEW_DESC view{};view.Format=DXGI_FORMAT_UNKNOWN;view.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;view.Buffer.NumElements=1;
 if(FAILED(device->CreateShaderResourceView(pass.ownerBuffer.Get(),&view,&pass.ownerView))){pass.error="Weapon owner result SRV creation failed";return pass;}
 D3D11_UNORDERED_ACCESS_VIEW_DESC output{};output.Format=DXGI_FORMAT_UNKNOWN;output.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;output.Buffer.NumElements=1;
 if(FAILED(device->CreateUnorderedAccessView(pass.ownerBuffer.Get(),&output,&pass.ownerOutput))){pass.error="Weapon owner result UAV creation failed";return pass;}
 return pass;
}
}
