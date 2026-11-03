#pragma once
// Observes the game's compute skinning resources before its graphics/shadow
// passes. Never binds, changes, dispatches, or reads back a GPU resource.
#include <reshade.hpp>
#include <d3d11.h>
#include <d3d11shader.h>
#include "shader_version_compat.h"
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdint>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>
#include <unordered_map>

namespace pawn_dance_compute {
using Microsoft::WRL::ComPtr;
inline std::atomic<uint64_t> seenDispatches{0},acceptedViews{0};
inline std::atomic<unsigned> knownShaders{0};
namespace detail {
struct Metadata { ComPtr<ID3D11ComputeShader> shader; int transformSlot=-1; };
inline std::unordered_map<uint64_t,Metadata> shaders;
inline std::recursive_mutex mutex;
inline bool (*enabledFn)()=nullptr;
inline void (*acceptFn)(ID3D11DeviceContext *,ID3D11ShaderResourceView *)=nullptr;
inline bool registered=false;
inline unsigned diagnosticRecords=0;
inline std::string diagnosticPath;
inline std::string json_string(const char *text) {
 std::string out="\"";
 if(text)for(unsigned i=0;text[i]&&i<256;i++){
  const unsigned char c=static_cast<unsigned char>(text[i]);
  if(c=='"'||c=='\\'){out+='\\';out+=char(c);}
  else if(c>=32&&c<127)out+=char(c);
  else out+='?';
 }
 return out+'"';
}
inline bool structured48(ID3D11ShaderResourceView *view) {
 if(!view)return false;
 D3D11_SHADER_RESOURCE_VIEW_DESC v{};view->GetDesc(&v);
 if(v.Format!=DXGI_FORMAT_UNKNOWN)return false;
 if(v.ViewDimension==D3D11_SRV_DIMENSION_BUFFER){if(!v.Buffer.NumElements)return false;}
 else if(v.ViewDimension==D3D11_SRV_DIMENSION_BUFFEREX){if(v.BufferEx.Flags||!v.BufferEx.NumElements)return false;}
 else return false;
 ComPtr<ID3D11Resource> resource;ComPtr<ID3D11Buffer> buffer;view->GetResource(&resource);
 if(FAILED(resource.As(&buffer)))return false;
 D3D11_BUFFER_DESC desc{};buffer->GetDesc(&desc);
 return desc.StructureByteStride==48&&(desc.MiscFlags&D3D11_RESOURCE_MISC_BUFFER_STRUCTURED)!=0;
}
inline void record_shader(ID3D11ComputeShader *shader,const void *code,size_t bytes) {
 if(!shader||!code||bytes<32||bytes>256*1024)return;
 std::lock_guard<std::recursive_mutex> lock(mutex);
 const uint64_t key=reinterpret_cast<uint64_t>(shader);
 if(shaders.contains(key)||shaders.size()>=256)return;
 ComPtr<ID3D11ShaderReflection> reflection;
 if(FAILED(D3DReflect(code,bytes,__uuidof(ID3D11ShaderReflection),reinterpret_cast<void **>(reflection.GetAddressOf()))))return;
 D3D11_SHADER_DESC description{};
 if(FAILED(reflection->GetDesc(&description))||D3D11_SHVER_GET_TYPE(description.Version)!=D3D11_SHVER_COMPUTE_SHADER)return;
 Metadata metadata;metadata.shader=shader;
 D3D11_SHADER_INPUT_BIND_DESC transform{};
 if(SUCCEEDED(reflection->GetResourceBindingDescByName("g_transformBuffer",&transform))&&transform.Type==D3D_SIT_STRUCTURED&&transform.BindCount==1&&transform.BindPoint<D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT)
  metadata.transformSlot=int(transform.BindPoint);
 // Unrelated compute programs must not exhaust the bounded skinning cache.
 // A late-loaded agent skin still needs its current-frame transform view.
 if(metadata.transformSlot<0)return;
 const int slot=metadata.transformSlot;
 shaders.emplace(key,std::move(metadata));knownShaders.store(unsigned(shaders.size()),std::memory_order_relaxed);
 if(diagnosticPath.empty()||diagnosticRecords>=32)return;
 ++diagnosticRecords;
 std::ostringstream line;
 line<<"{\"shader\":\""<<std::hex<<key<<std::dec<<"\",\"transformSlot\":"<<slot<<",\"resources\":[";
 bool first=true;
 for(unsigned i=0;i<description.BoundResources&&i<128;i++){
  D3D11_SHADER_INPUT_BIND_DESC resource{};if(FAILED(reflection->GetResourceBindingDesc(i,&resource)))continue;
  if(!first)line<<',';first=false;
  line<<"{\"name\":"<<json_string(resource.Name)<<",\"slot\":"<<resource.BindPoint<<",\"count\":"<<resource.BindCount<<",\"type\":"<<unsigned(resource.Type)<<'}';
 }
 line<<"]}\n";
 std::ofstream output(diagnosticPath,std::ios::app|std::ios::binary);if(output)output<<line.str();
}
inline void sample(ID3D11DeviceContext *context) {
 ++seenDispatches;
 if(!context)return;
 bool (*enabled)()=nullptr;
 void (*accept)(ID3D11DeviceContext *,ID3D11ShaderResourceView *)=nullptr;
 {std::lock_guard<std::recursive_mutex> lock(mutex);enabled=enabledFn;accept=acceptFn;}
 if(!enabled||!accept||!enabled())return;
 ComPtr<ID3D11ComputeShader> shader;context->CSGetShader(&shader,nullptr,nullptr);
 int slot=-1;
 {std::lock_guard<std::recursive_mutex> lock(mutex);auto found=shaders.find(reinterpret_cast<uint64_t>(shader.Get()));if(found!=shaders.end())slot=found->second.transformSlot;}
 if(slot<0)return;
 ComPtr<ID3D11ShaderResourceView> view;context->CSGetShaderResources(unsigned(slot),1,&view);
 if(!structured48(view.Get()))return;
 // Do not hold the metadata lock while entering the native renderer callback.
 accept(context,view.Get());++acceptedViews;
}
inline void pipeline(reshade::api::device *device,reshade::api::pipeline_layout,uint32_t count,const reshade::api::pipeline_subobject *objects,reshade::api::pipeline pipeline) {
 try {
  if(!device||device->get_api()!=reshade::api::device_api::d3d11||!objects)return;
  for(unsigned i=0;i<count;i++){
   if(objects[i].type!=reshade::api::pipeline_subobject_type::compute_shader||!objects[i].data||!objects[i].count)continue;
   const auto &code=*static_cast<const reshade::api::shader_desc *>(objects[i].data);
   record_shader(reinterpret_cast<ID3D11ComputeShader *>(pipeline.handle),code.code,code.code_size);
  }
 }catch(...){/* Diagnostics must never interrupt the game's pipeline creation. */}
}
inline bool dispatch(reshade::api::command_list *cmd,uint32_t,uint32_t,uint32_t) {
 try {if(cmd&&cmd->get_device()->get_api()==reshade::api::device_api::d3d11)sample(reinterpret_cast<ID3D11DeviceContext *>(cmd->get_native()));}catch(...){}
 return false;
}
}
inline void init(bool (*enabled)(),void (*accept)(ID3D11DeviceContext *,ID3D11ShaderResourceView *)) {
 std::lock_guard<std::recursive_mutex> lock(detail::mutex);
 detail::enabledFn=enabled;detail::acceptFn=accept;
 if(detail::registered)return;
 seenDispatches=0;acceptedViews=0;knownShaders=0;detail::diagnosticRecords=0;
 detail::diagnosticPath="D:\\MC-CS-Bridge\\logs\\pawn-dance-compute.jsonl";
 {std::ofstream reset(detail::diagnosticPath,std::ios::trunc|std::ios::binary);}
 reshade::register_event<reshade::addon_event::init_pipeline>(detail::pipeline);
 reshade::register_event<reshade::addon_event::dispatch>(detail::dispatch);
 detail::registered=true;
}
inline void shutdown() {
 std::lock_guard<std::recursive_mutex> lock(detail::mutex);
 detail::enabledFn=nullptr;detail::acceptFn=nullptr;
 if(detail::registered){reshade::unregister_event<reshade::addon_event::init_pipeline>(detail::pipeline);reshade::unregister_event<reshade::addon_event::dispatch>(detail::dispatch);detail::registered=false;}
 detail::shaders.clear();knownShaders=0;detail::diagnosticPath.clear();
}
}
