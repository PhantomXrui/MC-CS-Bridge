#pragma once
// Builds a geometry shader for the original, already-skinned character mesh.
// This factory does not bind shaders, change a context, or issue a draw.
#include <d3d11.h>
#include <d3d11shader.h>
#include "shader_version_compat.h"
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <array>
#include <cstdint>
#include <cstring>
#include <sstream>
#include <string>
#include <unordered_set>
#include <vector>

namespace pawn_dance_shader {
using Microsoft::WRL::ComPtr;
constexpr unsigned ConstantBufferSlot=13;
constexpr unsigned PerViewSlot=12,CsgoViewSlot=11,MaterialSlot=10,MaxActors=64;
constexpr unsigned InstanceResourceSlot=14,TransformResourceSlot=15;
enum class Mode {ClipAffine,WorldActors,WorldAttachment};
struct alignas(16) Constants {
    // Row-major storage, column-vector multiplication: output = matrix * input.
    float clipToClip[16]={1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1};
    uint32_t enabled=0;
    uint32_t padding[3]={};
};
static_assert(sizeof(Constants)==80);
struct alignas(16) WorldConstants {
    float scale[4]={1,1,1,0};
    uint32_t enabled=0,actorCount=0;
    float maxDistance=80,yawRadians=0;
    uint32_t instanceId=0,rootBoneIndex=0,useInstanceRoot=0,allowAllVerifiedInstances=0;
    // Original Source world coordinates; w is the pawn's existing uniform scale.
    float feet[MaxActors][4]={};
};
static_assert(sizeof(WorldConstants)==1072);
struct CompiledPass {
    ComPtr<ID3DBlob> bytecode;
    std::string source,error;
    unsigned originalOutputParameters=0;
    Mode mode=Mode::ClipAffine;
    unsigned constantBytes=sizeof(Constants),perViewSourceSlot=1,csgoViewSourceSlot=3,materialSourceSlot=0;
    explicit operator bool()const{return bytecode!=nullptr&&error.empty();}
};
struct Pass {
    ComPtr<ID3D11GeometryShader> shader;
    ComPtr<ID3D11Buffer> constants;
    std::string error;
    explicit operator bool()const{return shader!=nullptr&&constants!=nullptr&&error.empty();}
};
inline bool identifier(const char *name){
    if(!name||!name[0])return false;
    for(const unsigned char c:std::string(name))if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'))return false;
    return !(name[0]>='0'&&name[0]<='9');
}
inline unsigned components(unsigned mask){unsigned count=0;for(;mask;mask>>=1)count+=mask&1u;return count;}
inline bool supported_system_value(D3D_NAME value){
    return value==D3D_NAME_UNDEFINED||value==D3D_NAME_POSITION||value==D3D_NAME_CLIP_DISTANCE||value==D3D_NAME_CULL_DISTANCE
        ||value==D3D_NAME_RENDER_TARGET_ARRAY_INDEX||value==D3D_NAME_VIEWPORT_ARRAY_INDEX;
}
// Depth-only character passes have no world/normal varyings. Leaving their
// original position behind while moving the color pass creates opaque ghosts.
// Source's projectionToWorld returns absolute world coordinates (its inverse
// incorporates worldToCameraOffset), as in the existing attachment path.
inline CompiledPass compile_depth(ID3D11ShaderReflection *reflection,Mode mode,
    unsigned parameters,int position,int clipDistance,const std::string &fields,const std::string &copy,int maskWorld=-1,int maskDepth=-1){
    CompiledPass result;result.mode=mode;result.constantBytes=sizeof(WorldConstants);
    result.originalOutputParameters=parameters;
    result.csgoViewSourceSlot=result.materialSourceSlot=UINT32_MAX;
    const bool mask=maskWorld>=0&&maskDepth>=0&&parameters==3&&clipDistance<0;
    if(mode!=Mode::WorldActors||(!mask&&clipDistance<0)){result.error="Unsupported pawn depth mode";return result;}
    auto variable=[&](const char *name,unsigned offset,unsigned bytes){
        D3D11_SHADER_VARIABLE_DESC d{};return SUCCEEDED(reflection->GetConstantBufferByName("PerViewConstantBuffer_t")->GetVariableByName(name)->GetDesc(&d))&&d.StartOffset==offset&&d.Size==bytes;
    };
    D3D11_SHADER_INPUT_BIND_DESC binding{};
    if(!variable("g_matWorldToProjection",0,64)||!variable("g_matProjectionToWorld",64,64)
        ||!variable("g_vClipPlane0",272,16)||!variable("g_vWorldToCameraOffset",528,16)
        ||FAILED(reflection->GetResourceBindingDescByName("PerViewConstantBuffer_t",&binding))){result.error="Unverified pawn depth projection layout";return result;}
    result.perViewSourceSlot=binding.BindPoint;
    if(FAILED(reflection->GetResourceBindingDescByName("g_instanceBuffer",&binding))){result.error="Missing pawn depth instance table";return result;}
    // Exact mask layout verified from 55CA8E3F5231840A disassembly:
    // TEXCOORD3 = absolute world; TEXCOORD6 = projected z,w.
    if(!mask&&parameters!=2){result.error="Unverified pawn depth output signature";return result;}
    std::ostringstream s;
    s<<"cbuffer PawnDance:register(b13){float4 actorScale;uint applyTransform;uint actorCount;float maxDistance;float yawRadians;uint instanceId;uint rootBoneIndex;uint useInstanceRoot;uint allowAllVerifiedInstances;float4 actorFeet[64];};\n"
     <<"cbuffer OriginalPerView:register(b12){column_major float4x4 worldToProjection:packoffset(c0);column_major float4x4 projectionToWorld:packoffset(c4);float4 clipPlane:packoffset(c17);float4 worldToCameraOffset:packoffset(c33);};\n"
     <<"struct InstanceData{uint4 first;uint4 second;};StructuredBuffer<InstanceData> pawnInstances:register(t14);\n"
     <<"struct BoneTransform{float4 row0,row1,row2;};StructuredBuffer<BoneTransform> pawnTransforms:register(t15);\n"
     <<"float3 rotate(float3 v,float sn,float cs){return float3(cs*v.x-sn*v.y,sn*v.x+cs*v.y,v.z);}\n"
     <<"struct Input{"<<fields<<"};struct Output{"<<fields<<"uint primitiveId:SV_PrimitiveID;};\n"
     <<"[maxvertexcount(3)]void main(triangle Input input[3],uint primitiveId:SV_PrimitiveID,inout TriangleStream<Output> output){\n"
     <<"uint instances,stride,transforms,transformStride;pawnInstances.GetDimensions(instances,stride);pawnTransforms.GetDimensions(transforms,transformStride);\n"
     <<"bool valid=applyTransform!=0&&useInstanceRoot!=0&&instanceId<instances&&stride==32&&transformStride==48;float3 pivot=0;\n"
     <<"if(valid){uint base=pawnInstances[instanceId].first.y;valid=base<transforms&&(transforms-base)>2&&rootBoneIndex<(transforms-base-2);if(valid){BoneTransform root=pawnTransforms[base+rootBoneIndex+2];pivot=float3(root.row0.w,root.row1.w,root.row2.w);valid=all(isfinite(pivot));}}\n"
     <<"bool selected=valid&&allowAllVerifiedInstances!=0;if(valid&&!selected)[loop]for(uint a=0;a<min(actorCount,64u);++a){float3 d=pivot-actorFeet[a].xyz;if(dot(d,d)<maxDistance*maxDistance){selected=true;break;}}\n"
     <<"float sn,cs;sincos(yawRadians,sn,cs);float3 scale=max(actorScale.xyz,float3(.05,.05,.05));\n"
     <<"[unroll]for(uint v=0;v<3;++v){Output o;"<<copy
     <<"if(selected){";
    if(mask)s<<"float4 h=float4(input[v].value"<<maskWorld<<",1);";
    else s<<"float4 h=mul(projectionToWorld,input[v].value"<<position<<");";
    s<<"if(all(isfinite(h))&&abs(h.w)>1e-8){float3 before=h.xyz/h.w;float3 world=pivot+rotate((before-pivot)*scale,sn,cs);"
     <<"o.value"<<position<<"=mul(worldToProjection,float4(world+worldToCameraOffset.xyz,1));";
    if(mask)s<<"o.value"<<maskWorld<<"=world;o.value"<<maskDepth<<"=o.value"<<position<<".zw;";
    else s<<"o.value"<<clipDistance<<"=dot(float4(world,1),clipPlane);";
    s<<"}}\no.primitiveId=primitiveId;output.Append(o);}output.RestartStrip();}\n";
    result.source=s.str();ComPtr<ID3DBlob> errors;
    if(FAILED(D3DCompile(result.source.data(),result.source.size(),"pawn_dance_depth",nullptr,nullptr,"main","gs_5_0",D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&result.bytecode,&errors))){
        result.error=errors?std::string(static_cast<const char *>(errors->GetBufferPointer()),errors->GetBufferSize()):"Pawn depth compile failed";result.bytecode.Reset();
    }
    return result;
}
inline CompiledPass compile(const void *vertexBytecode,size_t vertexBytes,Mode mode=Mode::ClipAffine){
    CompiledPass result;
    result.mode=mode;result.constantBytes=mode!=Mode::ClipAffine?sizeof(WorldConstants):sizeof(Constants);
    if(!vertexBytecode||vertexBytes<32||vertexBytes>256*1024){result.error="Missing or out-of-bounds vertex shader bytecode";return result;}
    ComPtr<ID3D11ShaderReflection> reflection;
    if(FAILED(D3DReflect(vertexBytecode,vertexBytes,__uuidof(ID3D11ShaderReflection),reinterpret_cast<void **>(reflection.GetAddressOf())))){
        result.error="D3DReflect failed for vertex shader";return result;
    }
    D3D11_SHADER_DESC desc{};
    if(FAILED(reflection->GetDesc(&desc))||D3D11_SHVER_GET_TYPE(desc.Version)!=D3D11_SHVER_VERTEX_SHADER||desc.OutputParameters==0||desc.OutputParameters>32){
        result.error="Unsupported vertex shader/output signature";return result;
    }
    std::ostringstream fields,copy;
    std::unordered_set<std::string> semantics;
    int position=-1,worldPosition=-1,normal=-1,normalOriginal=-1,tangent=-1,clipDistance=-1,baseClip=-1;
    int eyeAxes[3]={-1,-1,-1};int maskWorld=-1,maskDepth=-1;
    unsigned outputComponents=0;
    for(unsigned i=0;i<desc.OutputParameters;i++){
        D3D11_SIGNATURE_PARAMETER_DESC p{};
        if(FAILED(reflection->GetOutputParameterDesc(i,&p))||!identifier(p.SemanticName)||!supported_system_value(p.SystemValueType)
            ||p.Stream!=0||p.Mask==0||(p.Mask&~15u)!=0){result.error="Unsupported output semantic, stream, or component mask";return result;}
        std::string semantic=std::string(p.SemanticName)+std::to_string(p.SemanticIndex);
        if(!semantics.insert(semantic).second){result.error="Duplicate output semantic";return result;}
        unsigned count=components(p.Mask);outputComponents+=count;
        const char *type=nullptr;
        if(p.ComponentType==D3D_REGISTER_COMPONENT_FLOAT32)type="float";
        else if(p.ComponentType==D3D_REGISTER_COMPONENT_UINT32)type="uint";
        else if(p.ComponentType==D3D_REGISTER_COMPONENT_SINT32)type="int";
        else{result.error="Unsupported output scalar type";return result;}
        if(p.SystemValueType==D3D_NAME_POSITION){
            if(position>=0||p.ComponentType!=D3D_REGISTER_COMPONENT_FLOAT32||p.Mask!=15){result.error="SV_POSITION must be one float4";return result;}
            position=int(i);
        }
        if(p.ComponentType==D3D_REGISTER_COMPONENT_FLOAT32){
            if(std::string(p.SemanticName)=="TEXCOORD"){
                if(p.SemanticIndex==0&&count==3)worldPosition=int(i);
                if(p.SemanticIndex==3&&count==3)maskWorld=int(i);
                if(p.SemanticIndex==6&&count==2)maskDepth=int(i);
                if(p.SemanticIndex==1&&count==3)normal=int(i);
                if(p.SemanticIndex==5&&count==3)normalOriginal=int(i);
                if(p.SemanticIndex==6&&count==4)tangent=int(i);
                if(p.SemanticIndex==9&&count==1)baseClip=int(i);
                if(p.SemanticIndex>=15&&p.SemanticIndex<=17&&count==4)eyeAxes[p.SemanticIndex-15]=int(i);
            }
            if(p.SystemValueType==D3D_NAME_CLIP_DISTANCE&&p.SemanticIndex==0&&count==1)clipDistance=int(i);
        }
        // Integer varyings must remain flat; floating interpolation is chosen by
        // the original pixel shader input declarations, which this pass preserves.
        if(p.ComponentType!=D3D_REGISTER_COMPONENT_FLOAT32)fields<<"nointerpolation ";
        fields<<type;if(count>1)fields<<count;
        fields<<" value"<<i<<" : "<<p.SemanticName<<p.SemanticIndex<<";\n";
        copy<<"o.value"<<i<<" = input[v].value"<<i<<";\n";
    }
    if(position<0){result.error="Vertex shader has no SV_POSITION output";return result;}
    if(outputComponents+1>128){result.error="No GS output capacity for primitive ID preservation";return result;}
    std::ostringstream source,transform,beforeLoop;
    if(mode!=Mode::ClipAffine){
        if(worldPosition<0)return compile_depth(reflection.Get(),mode,desc.OutputParameters,position,clipDistance,fields.str(),copy.str(),maskWorld,maskDepth);
        if(worldPosition<0||normal<0||normalOriginal<0||tangent<0||baseClip<0||clipDistance<0){result.error="Unverified pawn world/normal/clip output signature";return result;}
        auto variable=[&](const char *buffer,const char *name,unsigned expected,unsigned bytes)->bool{
            D3D11_SHADER_VARIABLE_DESC d{};return SUCCEEDED(reflection->GetConstantBufferByName(buffer)->GetVariableByName(name)->GetDesc(&d))&&d.StartOffset==expected&&d.Size==bytes;
        };
        if(!variable("PerViewConstantBuffer_t","g_matWorldToProjection",0,64)
            ||!variable("PerViewConstantBuffer_t","g_vWorldToCameraOffset",528,16)
            ||!variable("PerViewConstantBuffer_t","g_vClipPlane0",272,16)
            ||!variable("PerViewConstantBufferCsgo_t","g_vHighPrecisionLightingOffsetWs",496,16)){
            result.error="Unverified Source 2 per-view constant buffer layout";return result;
        }
        D3D11_SHADER_INPUT_BIND_DESC binding{};
        if(FAILED(reflection->GetResourceBindingDescByName("PerViewConstantBuffer_t",&binding))){result.error="Missing per-view CB binding";return result;}result.perViewSourceSlot=binding.BindPoint;
        if(FAILED(reflection->GetResourceBindingDescByName("PerViewConstantBufferCsgo_t",&binding))){result.error="Missing CS per-view CB binding";return result;}result.csgoViewSourceSlot=binding.BindPoint;
        if(FAILED(reflection->GetResourceBindingDescByName("$Globals",&binding))){result.error="Missing pawn material CB binding";return result;}result.materialSourceSlot=binding.BindPoint;
        D3D11_SHADER_VARIABLE_DESC plane{},firstPerson{};
        auto globals=reflection->GetConstantBufferByName("$Globals");
        if(FAILED(globals->GetVariableByName("g_vPlayerRenderClipPlane")->GetDesc(&plane))||plane.Size!=16||plane.StartOffset%16
            ||FAILED(globals->GetVariableByName("g_bRenderingFirstPersonLegs")->GetDesc(&firstPerson))||firstPerson.Size!=4||firstPerson.StartOffset%4){
            result.error="Unverified pawn clip-plane material layout";return result;
        }
        source<<"cbuffer PawnDance : register(b"<<ConstantBufferSlot<<") { float4 actorScale; uint applyTransform; uint actorCount; float maxDistance; float yawRadians; uint instanceId; uint rootBoneIndex; uint useInstanceRoot; uint allowAllVerifiedInstances; float4 actorFeet["<<MaxActors<<"]; };\n"
            <<"cbuffer OriginalPerView : register(b"<<PerViewSlot<<") { column_major float4x4 worldToProjection : packoffset(c0); float4 clipPlane : packoffset(c17); float4 worldToCameraOffset : packoffset(c33); };\n"
            <<"cbuffer OriginalCsgoView : register(b"<<CsgoViewSlot<<") { float4 lightingOffset : packoffset(c31); };\n"
            <<"cbuffer OriginalMaterial : register(b"<<MaterialSlot<<") { float4 playerClipPlane : packoffset(c"<<plane.StartOffset/16<<"); uint firstPersonLegs : packoffset(c"<<firstPerson.StartOffset/16<<'.'<<"xyzw"[(firstPerson.StartOffset%16)/4]<<"); };\n"
            <<"struct InstanceData { uint4 first; uint4 second; }; StructuredBuffer<InstanceData> pawnInstances : register(t"<<InstanceResourceSlot<<");\n"
            <<"struct BoneTransform { float4 row0; float4 row1; float4 row2; }; StructuredBuffer<BoneTransform> pawnTransforms : register(t"<<TransformResourceSlot<<");\n"
            <<"float3 danceRotate(float3 value,float sn,float cs) { return float3(cs*value.x-sn*value.y,sn*value.x+cs*value.y,value.z); }\n";
        if(mode==Mode::WorldAttachment){
            // The attachment owner pass has already verified both hands against
            // one same-frame body. Never infer a glove pivot from its fingers.
            source<<"StructuredBuffer<float4> ownerResult : register(t16);\n";
            beforeLoop<<"uint ownerCount,ownerStride; ownerResult.GetDimensions(ownerCount,ownerStride); float4 owner=0; if(ownerCount==1&&ownerStride==16)owner=ownerResult[0];\n"
                <<"int selected=(applyTransform!=0&&useInstanceRoot!=0&&owner.w>0.5&&all(isfinite(owner)))?0:-1; float3 dancePivot=owner.xyz; float spinSin,spinCos; sincos(yawRadians,spinSin,spinCos);\n";
        }else{
        beforeLoop<<"float3 triangleCenter=(input[0].value"<<worldPosition<<"+input[1].value"<<worldPosition<<"+input[2].value"<<worldPosition<<")/3.0+lightingOffset.xyz;\n"
            <<"float3 modelRoot=triangleCenter; bool validRoot=true; if(useInstanceRoot!=0) { uint instances,stride,transforms,transformStride; pawnInstances.GetDimensions(instances,stride); pawnTransforms.GetDimensions(transforms,transformStride); validRoot=instanceId<instances&&stride==32&&transformStride==48; if(validRoot) { uint base=pawnInstances[instanceId].first.y; validRoot=base<transforms&&(transforms-base)>2&&rootBoneIndex<(transforms-base-2); if(validRoot) { BoneTransform root=pawnTransforms[base+rootBoneIndex+2]; modelRoot=float3(root.row0.w,root.row1.w,root.row2.w); validRoot=all(isfinite(modelRoot)); } } }\n"
            // Local playback can authorize a verified draw without server actor
            // positions. It still requires a valid instance root, never a
            // triangle-centroid or a fabricated actor at an arbitrary distance.
            <<"int selected=(applyTransform!=0&&validRoot&&useInstanceRoot!=0&&allowAllVerifiedInstances!=0)?0:-1; float best=1e30;\n"
            // An instance-root draw needs only authorization: its pivot is always
            // modelRoot. Preserve the closest-actor search for the centroid path.
            <<"if(selected<0&&applyTransform!=0&&validRoot) [loop] for(uint a=0;a<min(actorCount,"<<MaxActors<<"u);++a) { float bodyScale=max(actorFeet[a].w,0.1); float3 d=modelRoot-actorFeet[a].xyz; float outside=max(-10.0*bodyScale-d.z,0.0)+max(d.z-110.0*bodyScale,0.0); float score=useInstanceRoot!=0?dot(d,d):dot(d.xy,d.xy)+outside*outside; if(score<best&&score<maxDistance*maxDistance){best=score;selected=int(a);if(useInstanceRoot!=0)break;} }\n"
            <<"float3 dancePivot=useInstanceRoot!=0?modelRoot:(selected>=0?actorFeet[selected].xyz:float3(0,0,0)); float spinSin,spinCos; sincos(yawRadians,spinSin,spinCos);\n";
        }
        transform<<"if(selected>=0) { float3 before=input[v].value"<<worldPosition<<"+lightingOffset.xyz; float3 s=max(actorScale.xyz,float3(0.05,0.05,0.05)); float3 world=dancePivot+danceRotate((before-dancePivot)*s,spinSin,spinCos);\n"
            <<"o.value"<<position<<"=mul(worldToProjection,float4(world+worldToCameraOffset.xyz,1));\n"
            <<"o.value"<<worldPosition<<"=world-lightingOffset.xyz;\n"
            <<"o.value"<<normal<<"=normalize(danceRotate(input[v].value"<<normal<<"/s,spinSin,spinCos));\n"
            <<"o.value"<<normalOriginal<<"=normalize(danceRotate(input[v].value"<<normalOriginal<<"/s,spinSin,spinCos));\n"
            <<"float3 tangentScaled=danceRotate(input[v].value"<<tangent<<".xyz*s,spinSin,spinCos); o.value"<<tangent<<".xyz=normalize(tangentScaled-o.value"<<normalOriginal<<"*dot(tangentScaled,o.value"<<normalOriginal<<"));\n"
            <<"float4 planeUsed=(input[v].value"<<baseClip<<">=0&&firstPersonLegs==0)?float4(playerClipPlane.xyz,-playerClipPlane.w):clipPlane;\n"
            <<"o.value"<<clipDistance<<"=dot(float4(world,1),planeUsed); o.value"<<baseClip<<"=dot(float4(world,1),clipPlane);\n";
        if(eyeAxes[0]>=0&&eyeAxes[1]>=0&&eyeAxes[2]>=0){
            // These are the eye projection axes, not a tangent vector. Their
            // inverse scale preserves dot(world-eyeOrigin, eyeAxis) after squash.
            transform<<"float3 eyeBefore=float3(input[v].value"<<eyeAxes[0]<<".w,input[v].value"<<eyeAxes[1]<<".w,input[v].value"<<eyeAxes[2]<<".w); float3 eyeAfter=dancePivot+danceRotate((eyeBefore-dancePivot)*s,spinSin,spinCos);\n";
            for(unsigned axis=0;axis<3;axis++)transform<<"o.value"<<eyeAxes[axis]<<".xyz=danceRotate(input[v].value"<<eyeAxes[axis]<<".xyz/s,spinSin,spinCos); o.value"<<eyeAxes[axis]<<".w=eyeAfter."<<"xyz"[axis]<<";\n";
        }
        transform<<"}\n";
    }else{
        source<<"cbuffer PawnDance : register(b"<<ConstantBufferSlot<<") { row_major float4x4 clipToClip; uint applyTransform; uint3 reserved; };\n";
        transform<<"if (applyTransform != 0) o.value"<<position<<" = mul(clipToClip, input[v].value"<<position<<");\n";
    }
    source<<"struct VertexInput {\n"<<fields.str()<<"};\n"
        <<"struct VertexOutput {\n"<<fields.str()<<"uint primitiveId : SV_PrimitiveID;\n};\n"
        <<"[maxvertexcount(3)] void main(triangle VertexInput input[3], uint primitiveId : SV_PrimitiveID, inout TriangleStream<VertexOutput> output) {\n"
        <<beforeLoop.str()<<"[unroll] for (uint v=0; v<3; ++v) { VertexOutput o;\n"<<copy.str()<<transform.str()
        <<"o.primitiveId = primitiveId; output.Append(o);\n} output.RestartStrip();\n}\n";
    result.source=source.str();result.originalOutputParameters=desc.OutputParameters;
    ComPtr<ID3DBlob> errors;
    HRESULT hr=D3DCompile(result.source.data(),result.source.size(),"pawn_dance_passthrough",nullptr,nullptr,"main","gs_5_0",
        D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&result.bytecode,&errors);
    if(FAILED(hr)){
        if(errors)result.error.assign(static_cast<const char *>(errors->GetBufferPointer()),errors->GetBufferSize());
        else result.error="D3DCompile failed for generated geometry shader";
        result.bytecode.Reset();
    }
    return result;
}
inline Pass create(ID3D11Device *device,const CompiledPass &compiled){
    Pass result;if(!device||!compiled){result.error="Missing device or compiled geometry shader";return result;}
    if(FAILED(device->CreateGeometryShader(compiled.bytecode->GetBufferPointer(),compiled.bytecode->GetBufferSize(),nullptr,&result.shader))){result.error="CreateGeometryShader failed";return result;}
    D3D11_BUFFER_DESC desc{};desc.ByteWidth=compiled.constantBytes;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    Constants identity;WorldConstants worldIdentity;D3D11_SUBRESOURCE_DATA initial{compiled.mode!=Mode::ClipAffine?static_cast<const void *>(&worldIdentity):static_cast<const void *>(&identity),0,0};
    if(FAILED(device->CreateBuffer(&desc,&initial,&result.constants))){result.error="CreateBuffer failed for dance constants";result.shader.Reset();}
    return result;
}
inline bool can_apply(ID3D11DeviceContext *context,std::string &reason){
    reason.clear();if(!context){reason="Missing D3D11 context";return false;}
    ComPtr<ID3D11GeometryShader> geometry;context->GSGetShader(&geometry,nullptr,nullptr);
    if(geometry){reason="Original draw already has a geometry shader";return false;}
    ComPtr<ID3D11HullShader> hull;ComPtr<ID3D11DomainShader> domain;
    context->HSGetShader(&hull,nullptr,nullptr);context->DSGetShader(&domain,nullptr,nullptr);
    if(hull||domain){reason="Tessellated draw is unsupported";return false;}
    D3D11_PRIMITIVE_TOPOLOGY topology{};context->IAGetPrimitiveTopology(&topology);
    if(topology!=D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST&&topology!=D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP){reason="Only non-adjacent triangles are supported";return false;}
    return true;
}
}
