#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "../source/cs2-bridge/native/pawn_dance_weapon_shader.h"
#include "../source/cs2-bridge/native/pawn_dance_glove_shader.h"
#include "../source/cs2-bridge/native/pawn_dance_cache.h"
#include <cmath>
#include <iostream>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
void check(HRESULT h){if(FAILED(h))throw std::runtime_error("D3D11 operation failed");}
void require(bool b,const char*m){if(!b)throw std::runtime_error(m);}
struct Vertex{float p[3];};

int main()try{
 ComPtr<ID3D11Device>d;ComPtr<ID3D11DeviceContext>c;
 check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&d,nullptr,&c));
 const char*vsText=R"(
 cbuffer View:register(b0){column_major float4x4 m:packoffset(c0);float4 plane:packoffset(c17);float4 offset:packoffset(c33);};
 struct Out{float4 p:SV_POSITION;float clip:SV_ClipDistance0;float3 world:TEXCOORD3;float2 depth:TEXCOORD6;};
 Out main(float3 p:POSITION){Out o;o.p=mul(m,float4(p+offset.xyz,1));o.clip=dot(float4(p,1),plane);o.world=p;o.depth=o.p.zw;return o;})";
 ComPtr<ID3DBlob>code;check(D3DCompile(vsText,strlen(vsText),nullptr,nullptr,nullptr,"main","vs_5_0",0,0,&code,nullptr));
 ComPtr<ID3D11VertexShader>vs;check(d->CreateVertexShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&vs));
 D3D11_INPUT_ELEMENT_DESC input={"POSITION",0,DXGI_FORMAT_R32G32B32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0};
 ComPtr<ID3D11InputLayout>layout;check(d->CreateInputLayout(&input,1,code->GetBufferPointer(),code->GetBufferSize(),&layout));
 auto buffer=[&](UINT size,UINT flags,const void*p,UINT stride=0){ComPtr<ID3D11Buffer>b;D3D11_BUFFER_DESC desc{};desc.ByteWidth=size;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=flags;desc.StructureByteStride=stride;desc.MiscFlags=stride?D3D11_RESOURCE_MISC_BUFFER_STRUCTURED:0;D3D11_SUBRESOURCE_DATA initial{p,0,0};check(d->CreateBuffer(&desc,p?&initial:nullptr,&b));return b;};
 auto srv=[&](ID3D11Buffer*b,UINT count){ComPtr<ID3D11ShaderResourceView>s;D3D11_SHADER_RESOURCE_VIEW_DESC desc{};desc.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;desc.Buffer.NumElements=count;check(d->CreateShaderResourceView(b,&desc,&s));return s;};
 Vertex vertices[]={{{108,203,300}},{{112,207,320}},{{104,209,350}}};
 auto vb=buffer(sizeof vertices,D3D11_BIND_VERTEX_BUFFER,vertices);
 unsigned instances[8]={0,3};float transforms[64][12]={};
 auto ib=buffer(sizeof instances,D3D11_BIND_SHADER_RESOURCE,instances,32);auto tb=buffer(sizeof transforms,D3D11_BIND_SHADER_RESOURCE,transforms,48);
 auto is=srv(ib.Get(),1),ts=srv(tb.Get(),64);
 auto view=buffer(544,D3D11_BIND_CONSTANT_BUFFER,nullptr);
 auto constants=buffer(sizeof(pawn_dance_weapon_shader::Constants),D3D11_BIND_CONSTANT_BUFFER,nullptr);
 auto out=buffer(3*9*sizeof(float),D3D11_BIND_STREAM_OUTPUT,nullptr);
 D3D11_BUFFER_DESC staging{};staging.ByteWidth=3*9*sizeof(float);staging.Usage=D3D11_USAGE_STAGING;staging.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
 ComPtr<ID3D11Buffer>read;check(d->CreateBuffer(&staging,nullptr,&read));
 auto *v=vb.Get();UINT stride=sizeof(Vertex),zero=0;c->IASetVertexBuffers(0,1,&v,&stride,&zero);c->IASetInputLayout(layout.Get());c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);c->VSSetShader(vs.Get(),nullptr,0);
 auto *viewPtr=view.Get(),*constantPtr=constants.Get();c->VSSetConstantBuffers(0,1,&viewPtr);c->GSSetConstantBuffers(12,1,&viewPtr);c->GSSetConstantBuffers(13,1,&constantPtr);
 ID3D11ShaderResourceView*srvs[]={is.Get(),ts.Get()};c->GSSetShaderResources(14,2,srvs);
 float ownerData[4]={100,200,300,1};auto owner=buffer(16,D3D11_BIND_SHADER_RESOURCE,ownerData,16);auto ownerView=srv(owner.Get(),1);auto*ownerPtr=ownerView.Get();c->GSSetShaderResources(16,1,&ownerPtr);
 unsigned checked=0;
 for(unsigned variant=0;variant<4;++variant){
  const uint64_t hashes[]={0x5F84963B372D2DB9ull,0x767A1DCE231A3DE9ull,0x55CA8E3F5231840Aull,0x55CA8E3F5231840Aull};auto hash=hashes[variant];bool mask=variant>=2,attachment=variant==3;
  // D3D11 links stage registers directly; mirror the captured VS register order.
  std::string variantVS=vsText;
  if(mask){auto begin=variantVS.find("struct Out{");auto end=variantVS.find("};",begin);variantVS.replace(begin,end+2-begin,"struct Out{float3 world:TEXCOORD3;float2 depth:TEXCOORD6;float4 p:SV_POSITION;float clip:SV_ClipDistance0;};");}
  ComPtr<ID3DBlob>variantCode;check(D3DCompile(variantVS.data(),variantVS.size(),nullptr,nullptr,nullptr,"main","vs_5_0",0,0,&variantCode,nullptr));ComPtr<ID3D11VertexShader>variantShader;check(d->CreateVertexShader(variantCode->GetBufferPointer(),variantCode->GetBufferSize(),nullptr,&variantShader));c->VSSetShader(variantShader.Get(),nullptr,0);
  auto compiled=pawn_dance_cache::body(hash);require(bool(compiled),"missing cached pawn depth shader");require(compiled.csgoViewSourceSlot==UINT32_MAX&&compiled.materialSourceSlot==UINT32_MAX,"depth must not bind nonexistent material buffers");
  D3D11_SO_DECLARATION_ENTRY entries[]={{0,"SV_POSITION",0,0,4,0},{0,mask?"TEXCOORD":"SV_ClipDistance",mask?3u:0u,0,BYTE(mask?3:1),0},{0,"TEXCOORD",6,0,2,0}};
  if(attachment){auto weapon=pawn_dance_cache::weapon(hash);require(bool(weapon),"missing attachment mask shader");compiled.bytecode=weapon.bytecode;}
  UINT outputFloats=mask?9:5;UINT outputStride=outputFloats*sizeof(float);ComPtr<ID3D11GeometryShader>gs;check(d->CreateGeometryShaderWithStreamOutput(compiled.bytecode->GetBufferPointer(),compiled.bytecode->GetBufferSize(),entries,mask?3:2,&outputStride,1,D3D11_SO_NO_RASTERIZED_STREAM,nullptr,&gs));c->GSSetShader(gs.Get(),nullptr,0);
  for(unsigned root=0;root<33;root++)for(int perspective=0;perspective<2;perspective++)for(int scenario=0;scenario<8;scenario++){
   // M transforms camera-relative coordinates. Source's inverse additionally
   // subtracts the camera offset: inverse(M)*clip is NOT yet absolute world.
   float offset[3]={-90,-180,-280};float m[4][4]={{2,0,0,7},{0,3,0,-9},{0,0,4,11},{0,0,perspective?.01f:0,1}};
   double aug[4][8]={};for(int r=0;r<4;r++)for(int k=0;k<4;k++){aug[r][k]=m[r][k];aug[r][k+4]=r==k;}
   for(int k=0;k<4;k++){double div=aug[k][k];for(int j=0;j<8;j++)aug[k][j]/=div;for(int r=0;r<4;r++)if(r!=k){double f=aug[r][k];for(int j=0;j<8;j++)aug[r][j]-=f*aug[k][j];}}
   float data[136]={};for(int r=0;r<4;r++)for(int k=0;k<4;k++){data[k*4+r]=m[r][k];data[16+k*4+r]=float(aug[r][k+4]-(r<3?offset[r]*aug[3][k+4]:0));}
   data[70]=1;data[71]=-299;for(int k=0;k<3;k++)data[132+k]=offset[k];c->UpdateSubresource(view.Get(),0,nullptr,data,0,0);
   memset(transforms,0,sizeof transforms);transforms[3+root+2][3]=100;transforms[3+root+2][7]=200;transforms[3+root+2][11]=300;c->UpdateSubresource(tb.Get(),0,nullptr,transforms,0,0);
   pawn_dance_shader::WorldConstants params;params.enabled=1;params.useInstanceRoot=1;params.allowAllVerifiedInstances=1;params.rootBoneIndex=root;params.scale[0]=1.55f;params.scale[1]=1.55f;params.scale[2]=.18f;params.yawRadians=.71f;
   bool selected=scenario<3;
   if(scenario==1){params.allowAllVerifiedInstances=0;params.actorCount=1;params.feet[0][0]=100;params.feet[0][1]=200;params.feet[0][2]=300;}
   if(scenario==2){params.scale[0]=params.scale[1]=params.scale[2]=1;params.yawRadians=0;}
   if(scenario==3)params.enabled=0;
   if(scenario==4)params.instanceId=2;
   if(scenario==5)params.rootBoneIndex=100;
   if(scenario==6){params.allowAllVerifiedInstances=0;params.actorCount=1;params.feet[0][0]=9999;}
   if(scenario==7)params.useInstanceRoot=0;
   pawn_dance_weapon_shader::Constants allParams;allParams.dance=params;c->UpdateSubresource(constants.Get(),0,nullptr,&allParams,0,0);ownerData[3]=selected?1.0f:0.0f;c->UpdateSubresource(owner.Get(),0,nullptr,ownerData,0,0);auto*outPtr=out.Get();c->SOSetTargets(1,&outPtr,&zero);c->Draw(3,0);ID3D11Buffer*none=nullptr;c->SOSetTargets(1,&none,&zero);c->CopyResource(read.Get(),out.Get());D3D11_MAPPED_SUBRESOURCE mapped{};check(c->Map(read.Get(),0,D3D11_MAP_READ,0,&mapped));
   auto*actual=static_cast<const float*>(mapped.pData);
   for(int n=0;n<3;n++){float w[3];memcpy(w,vertices[n].p,sizeof w);if(selected){float x=(w[0]-100)*params.scale[0],y=(w[1]-200)*params.scale[1];w[0]=100+cosf(params.yawRadians)*x-sinf(params.yawRadians)*y;w[1]=200+sinf(params.yawRadians)*x+cosf(params.yawRadians)*y;w[2]=300+(w[2]-300)*params.scale[2];}float expected[9]={};for(int r=0;r<4;r++){expected[r]=m[r][3];for(int k=0;k<3;k++)expected[r]+=m[r][k]*(w[k]+offset[k]);}if(mask){for(int k=0;k<3;k++)expected[4+k]=w[k];expected[7]=expected[2];expected[8]=expected[3];}else expected[4]=w[2]-299;for(unsigned k=0;k<outputFloats;k++){float a=actual[n*outputFloats+k];if(!(std::isfinite(a)&&fabsf(a-expected[k])<.003f)){std::cerr<<"variant="<<variant<<" root="<<root<<" perspective="<<perspective<<" scenario="<<scenario<<" vertex="<<n<<" component="<<k<<" actual="<<a<<" expected="<<expected[k]<<"\n";throw std::runtime_error("depth/color projected position or clipping mismatch");}++checked;}}
   c->Unmap(read.Get(),0);
  }
 }
 std::cout<<"PASS "<<checked<<" pawn depth outputs: two depth shaders plus body/attachment mask, 33 root indices, perspective/orthographic, squash/rotation, disabled/invalid/far-owner rejection\n";return 0;
}catch(const std::exception&e){std::cerr<<"FAIL "<<e.what()<<'\n';return 1;}
