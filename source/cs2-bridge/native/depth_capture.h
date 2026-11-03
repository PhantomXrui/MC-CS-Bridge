#pragma once
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <unordered_map>
#include <cstdio>
#include <fstream>
namespace csdepth {
using Microsoft::WRL::ComPtr;
using namespace reshade::api;
ComPtr<ID3D11Texture2D> current, copy, candidate;
ComPtr<ID3D11ShaderResourceView> srv;
ComPtr<ID3D11Texture2D> multisampled;
ComPtr<ID3D11ShaderResourceView> msSrv;
ComPtr<ID3D11UnorderedAccessView> resolvedUav;
ComPtr<ID3D11ComputeShader> resolveShader;
uint32_t sourceSamples=0;
std::unordered_map<ID3D11Texture2D *,uint64_t> scores;
std::unordered_map<ID3D11Texture2D *,viewport> viewports;
viewport boundViewport{},capturedViewport{};
uint64_t best=0, frames=0;
uint32_t width=1280,height=720;
uint64_t currentView=0,copies=0;
uint64_t candidateScore=0;
bool insideEffects=false;
bool eligible(const D3D11_TEXTURE2D_DESC &d){
 // FSR keeps the same view but renders depth below the presentation resolution.
 return d.Width>=width/3 && d.Height>=height/3 && d.Width<=width && d.Height<=height
  && std::abs(double(d.Width)/d.Height-double(width)/height)<0.015
  && d.SampleDesc.Count>=1 && d.SampleDesc.Count<=16;
}
bool resolve_msaa(ID3D11Device *device,ID3D11DeviceContext *context){
 if(!resolveShader){
  const char *code=R"(
Texture2DMS<float> sourceDepth : register(t0);
RWTexture2D<float> resolvedDepth : register(u0);
[numthreads(8,8,1)] void main(uint3 p:SV_DispatchThreadID){
 uint w,h,s;sourceDepth.GetDimensions(w,h,s);if(p.x>=w||p.y>=h)return;
 float d=1.0;for(uint i=0;i<s;++i)d=min(d,sourceDepth.Load(p.xy,i));
 resolvedDepth[p.xy]=d;
})";
  ComPtr<ID3DBlob> bytecode,error;
  if(FAILED(D3DCompile(code,strlen(code),nullptr,nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&bytecode,&error)))return false;
  if(FAILED(device->CreateComputeShader(bytecode->GetBufferPointer(),bytecode->GetBufferSize(),nullptr,&resolveShader)))return false;
 }
 // Do not leak our compute state into the game's next pass.
 ComPtr<ID3D11ComputeShader> oldShader;ID3D11ClassInstance *classes[256]{};UINT count=256;
 context->CSGetShader(&oldShader,classes,&count);
 ComPtr<ID3D11ShaderResourceView> oldSrv;context->CSGetShaderResources(0,1,&oldSrv);
 ComPtr<ID3D11UnorderedAccessView> oldUav;context->CSGetUnorderedAccessViews(0,1,&oldUav);
 auto *input=msSrv.Get();auto *output=resolvedUav.Get();
 context->CSSetShader(resolveShader.Get(),nullptr,0);context->CSSetShaderResources(0,1,&input);context->CSSetUnorderedAccessViews(0,1,&output,nullptr);
 D3D11_TEXTURE2D_DESC d{};copy->GetDesc(&d);context->Dispatch((d.Width+7)/8,(d.Height+7)/8,1);
 ID3D11ShaderResourceView *nullSrv=nullptr;ID3D11UnorderedAccessView *nullUav=nullptr;
 context->CSSetShaderResources(0,1,&nullSrv);context->CSSetUnorderedAccessViews(0,1,&nullUav,nullptr);
 input=oldSrv.Get();output=oldUav.Get();context->CSSetShaderResources(0,1,&input);context->CSSetUnorderedAccessViews(0,1,&output,nullptr);context->CSSetShader(oldShader.Get(),classes,count);
 for(UINT i=0;i<count;++i)if(classes[i])classes[i]->Release();
 return true;
}
void snapshot(command_list *cmd,ID3D11Texture2D *tex) {
 if(insideEffects||!tex||scores[tex]<=best)return;
 D3D11_TEXTURE2D_DESC desc;tex->GetDesc(&desc);
 if(!eligible(desc))return;
 ComPtr<ID3D11Device> device;tex->GetDevice(&device);
 DXGI_FORMAT viewFormat,format;
 if(desc.Format==DXGI_FORMAT_D24_UNORM_S8_UINT||desc.Format==DXGI_FORMAT_R24G8_TYPELESS){format=DXGI_FORMAT_R24G8_TYPELESS;viewFormat=DXGI_FORMAT_R24_UNORM_X8_TYPELESS;}
 else if(desc.Format==DXGI_FORMAT_D32_FLOAT||desc.Format==DXGI_FORMAT_R32_TYPELESS){format=DXGI_FORMAT_R32_TYPELESS;viewFormat=DXGI_FORMAT_R32_FLOAT;}
 else return;
 D3D11_TEXTURE2D_DESC old={};if(copy)copy->GetDesc(&old);
 if(desc.SampleDesc.Count>1){
  D3D11_TEXTURE2D_DESC ms{};if(multisampled)multisampled->GetDesc(&ms);
  if(!multisampled||!copy||!srv||!msSrv||!resolvedUav||ms.Width!=desc.Width||ms.Height!=desc.Height||ms.Format!=format||ms.SampleDesc.Count!=desc.SampleDesc.Count||ms.SampleDesc.Quality!=desc.SampleDesc.Quality){
   srv.Reset();copy.Reset();multisampled.Reset();msSrv.Reset();resolvedUav.Reset();
   auto input=desc;input.Format=format;input.BindFlags=D3D11_BIND_SHADER_RESOURCE;input.Usage=D3D11_USAGE_DEFAULT;input.CPUAccessFlags=0;input.MiscFlags=0;
   if(FAILED(device->CreateTexture2D(&input,nullptr,&multisampled)))return;
   D3D11_SHADER_RESOURCE_VIEW_DESC v{};v.Format=viewFormat;v.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2DMS;
   if(FAILED(device->CreateShaderResourceView(multisampled.Get(),&v,&msSrv)))return;
   input.Format=DXGI_FORMAT_R32_FLOAT;input.SampleDesc={1,0};input.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;
   if(FAILED(device->CreateTexture2D(&input,nullptr,&copy))||FAILED(device->CreateShaderResourceView(copy.Get(),nullptr,&srv))||FAILED(device->CreateUnorderedAccessView(copy.Get(),nullptr,&resolvedUav)))return;
  }
  auto *context=reinterpret_cast<ID3D11DeviceContext *>(cmd->get_native());context->CopyResource(multisampled.Get(),tex);
  if(!resolve_msaa(device.Get(),context))return;
  sourceSamples=desc.SampleDesc.Count;capturedViewport=viewports[tex];best=scores[tex];++copies;return;
 }
 if(!copy||old.Width!=desc.Width||old.Height!=desc.Height||old.Format!=format){
  multisampled.Reset();msSrv.Reset();resolvedUav.Reset();
  srv.Reset();copy.Reset();desc.Format=format;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;desc.Usage=D3D11_USAGE_DEFAULT;desc.CPUAccessFlags=0;desc.MiscFlags=0;
  if(FAILED(device->CreateTexture2D(&desc,nullptr,&copy)))return;
  D3D11_SHADER_RESOURCE_VIEW_DESC v={};v.Format=viewFormat;v.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;v.Texture2D.MipLevels=1;
  if(FAILED(device->CreateShaderResourceView(copy.Get(),&v,&srv)))return;
 }
 auto *context=reinterpret_cast<ID3D11DeviceContext *>(cmd->get_native());context->CopyResource(copy.Get(),tex);sourceSamples=1;capturedViewport=viewports[tex];best=scores[tex];++copies;
}
void bind(command_list *cmd,uint32_t,const resource_view *,resource_view dsv){
 if(insideEffects||cmd->get_device()->get_api()!=device_api::d3d11)return;
 if(dsv.handle==currentView)return;
 currentView=dsv.handle;
 // A render-target switch does not destroy the depth texture. Keep a reference
 // and defer its copy until it is cleared, or until the composite actually needs it.
 if(current && scores[current.Get()]>candidateScore){D3D11_TEXTURE2D_DESC d{};current->GetDesc(&d);if(eligible(d)){candidate=current;candidateScore=scores[current.Get()];}}
 current.Reset();if(!dsv.handle)return;
 auto *view=reinterpret_cast<ID3D11DepthStencilView *>(dsv.handle);ComPtr<ID3D11Resource> resource;view->GetResource(&resource);resource.As(&current);
}
void viewport_bind(command_list *,uint32_t first,uint32_t count,const viewport *v){if(!insideEffects&&first==0&&count)boundViewport=v[0];}
bool draw(command_list *,uint32_t n,uint32_t instances,uint32_t,uint32_t){
 // Ignore the separate foreground/viewmodel range; only the world writes [0,.95].
 if(!insideEffects&&current&&boundViewport.width>0&&boundViewport.height>0&&boundViewport.min_depth<.001f&&boundViewport.max_depth<=.951f){
  scores[current.Get()]+=uint64_t(n)*instances;viewports[current.Get()]=boundViewport;
 }return false;
}
bool indexed(command_list *cmd,uint32_t n,uint32_t instances,uint32_t first,int32_t,uint32_t inst){return draw(cmd,n,instances,first,inst);}
bool clear(command_list *cmd,resource_view dsv,const float *depth,const uint8_t *,uint32_t,const rect *){
 if(insideEffects||!depth||!dsv.handle||cmd->get_device()->get_api()!=device_api::d3d11)return false;
 auto *view=reinterpret_cast<ID3D11DepthStencilView *>(dsv.handle);ComPtr<ID3D11Resource> r;view->GetResource(&r);ComPtr<ID3D11Texture2D> t;r.As(&t);snapshot(cmd,t.Get());if(t){scores[t.Get()]=0;if(t.Get()==candidate.Get()){candidate.Reset();candidateScore=0;}}return false;
}
void begin(effect_runtime *runtime,command_list *cmd){
 snapshot(cmd,candidate.Get());snapshot(cmd,current.Get());insideEffects=true;runtime->get_screenshot_width_and_height(&width,&height);
 if(srv){resource_view v{reinterpret_cast<uint64_t>(srv.Get())};runtime->update_texture_bindings("CSDEPTH",v,v);}
 if(copy){D3D11_TEXTURE2D_DESC d{};copy->GetDesc(&d);
  runtime->set_uniform_value_float(runtime->find_uniform_variable("MCPassthrough.fx","HostDepthUv"),capturedViewport.width/d.Width,capturedViewport.height/d.Height,capturedViewport.x/d.Width,capturedViewport.y/d.Height);
 }
 runtime->set_uniform_value_bool(runtime->find_uniform_variable("MCPassthrough.fx","HostDepthValid"),srv && best>0);
 ++frames; // Periodic synchronous log I/O must not interrupt the render thread.
}
void end(){insideEffects=false;best=0;scores.clear();viewports.clear();current.Reset();candidate.Reset();candidateScore=0;currentView=0;copies=0;}
void dump(){
 std::ofstream meta("D:\\MC-CS-Bridge\\logs\\cs2-depth-info.json");D3D11_TEXTURE2D_DESC captured{};if(copy)copy->GetDesc(&captured);
 meta<<"{\"presentationWidth\":"<<width<<",\"presentationHeight\":"<<height<<",\"depthWidth\":"<<captured.Width<<",\"depthHeight\":"<<captured.Height<<",\"samples\":"<<sourceSamples<<",\"viewport\":["<<capturedViewport.x<<","<<capturedViewport.y<<","<<capturedViewport.width<<","<<capturedViewport.height<<"],\"depthRange\":["<<capturedViewport.min_depth<<","<<capturedViewport.max_depth<<"]}";
 if(!copy)return;ComPtr<ID3D11Device> device;copy->GetDevice(&device);ComPtr<ID3D11DeviceContext> context;device->GetImmediateContext(&context);
 D3D11_TEXTURE2D_DESC d;copy->GetDesc(&d);auto fmt=d.Format;d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
 ComPtr<ID3D11Texture2D> staging;if(FAILED(device->CreateTexture2D(&d,nullptr,&staging)))return;context->CopyResource(staging.Get(),copy.Get());D3D11_MAPPED_SUBRESOURCE data;
 if(FAILED(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&data)))return;
 std::ofstream out("D:\\MC-CS-Bridge\\logs\\cs2-depth.raw",std::ios::binary);uint32_t head[]={d.Width,d.Height,uint32_t(fmt)};out.write(reinterpret_cast<char *>(head),sizeof head);
 for(uint32_t y=0;y<d.Height;y++)out.write(reinterpret_cast<char *>(data.pData)+y*data.RowPitch,d.Width*4);context->Unmap(staging.Get(),0);
}
void destroy(device *dev){if(dev->get_api()==device_api::d3d11){current.Reset();copy.Reset();candidate.Reset();candidateScore=0;srv.Reset();multisampled.Reset();msSrv.Reset();resolvedUav.Reset();resolveShader.Reset();scores.clear();viewports.clear();best=0;}}
void register_events(){
 reshade::register_event<reshade::addon_event::bind_viewports>(viewport_bind);
 reshade::register_event<reshade::addon_event::bind_render_targets_and_depth_stencil>(bind);
 reshade::register_event<reshade::addon_event::draw>(draw);
 reshade::register_event<reshade::addon_event::draw_indexed>(indexed);
 reshade::register_event<reshade::addon_event::clear_depth_stencil_view>(clear);
 reshade::register_event<reshade::addon_event::destroy_device>(destroy);
}
}
