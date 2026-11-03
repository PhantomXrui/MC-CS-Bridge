#pragma once
#include "pawn_dance_shader.h"
#include "pawn_dance_compute.h"
#include "pawn_dance_weapon_shader.h"
#include "pawn_dance_glove_shader.h"
#include "pawn_dance_cache.h"
#include "pawn_dance_readback.h"
#include <reshade.hpp>
#include <windows.h>
#include <d3d11_1.h>
#include <algorithm>
#include <atomic>
#include <bitset>
#include <cmath>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

// Applies the dance to the game's original animated triangles. No proxy entities,
// changed game assets, pawn hiding, online transport, or server memory writes.
namespace pawn_dance_native {
using Microsoft::WRL::ComPtr;
using pawn_dance_agent_data::fingerprints;
using pawn_dance_agent_data::KindCount;
using pawn_dance_agent_data::GloveKind;
struct Program {ComPtr<ID3D11VertexShader> original;pawn_dance_shader::CompiledPass compiled;pawn_dance_shader::Pass pass;uint64_t lastFrame=0;int lastKind=0;uint32_t lastInstance=UINT_MAX;};
struct WeaponProgram {ComPtr<ID3D11VertexShader> original;pawn_dance_weapon_shader::Compiled compiled;pawn_dance_weapon_shader::Pass pass;};
constexpr unsigned MaxWeaponMeshes=256;
using WeaponMask=std::bitset<MaxWeaponMeshes>;
static_assert(sizeof(pawn_dance_weapon_data::meshes)/sizeof(pawn_dance_weapon_data::meshes[0])<=MaxWeaponMeshes);
struct WeaponIdentity {WeaponMask meshes{};unsigned stride=0;};
struct BufferIdentity {int kind=0;unsigned stride=0;};
struct ShaderBindings {ComPtr<ID3D11VertexShader> original;int instance=-1,transform=-1;};
struct InstanceLayout {ComPtr<ID3D11InputLayout> original;unsigned slot=0,offset=0,step=0;};
struct Actor {uint32_t pawn=0;int kind=0;float x=0,y=0,z=0,scale=1;};
struct OwnerCandidate {uint32_t instance=0,kind=0;ComPtr<ID3D11ShaderResourceView> instances,transforms;};
std::vector<OwnerCandidate> ownerCandidates;
pawn_dance_readback::Queue readbacks;
std::unordered_map<uint64_t,Program> programs;
std::unordered_map<uint64_t,WeaponProgram> weaponPrograms;
std::unordered_map<uint64_t,WeaponIdentity> weaponBuffers;
std::unordered_map<uint64_t,BufferIdentity> buffers;
std::unordered_map<uint64_t,BufferIdentity> gloveBuffers;
ComPtr<ID3D11Device> gloveDevice;
pawn_dance_glove_shader::Pass gloveOwnerPass;
std::unordered_map<uint64_t,ShaderBindings> bindings;
std::unordered_map<uint64_t,InstanceLayout> layouts;
std::unordered_map<uint64_t,unsigned> sequentialInstances;
ComPtr<ID3D11ShaderResourceView> lastTransforms;
ID3D11DeviceContext *transformContext=nullptr;
uint64_t transformFrame=0;
// Resource destruction callbacks can run synchronously while a diagnostic
// staging buffer or compiled pass is released on this same thread.
std::recursive_mutex mutex;
std::atomic<unsigned> programCount{0};
HANDLE mapping=nullptr;uint8_t *shared=nullptr;
uint64_t lastOpen=0,frame=0,totalDraws=0,skipped=0;
uint32_t diagnosticError=0;
unsigned fallbackReads=0,eligibleActors=0,fallbackFrameReads=0;
unsigned instanceFallbackReads=0,lastSkipReason=0,skipReasons[12]{};
unsigned observedTransformViewDimension=0,observedInstanceViewDimension=0;
uint64_t weaponDraws=0,weaponSkipped=0;
uint64_t gloveDraws=0,gloveSkipped=0;
unsigned gloveFrameReads=0,gloveReasons[12]{};
unsigned weaponFallbackReads=0,weaponLastReason=0,weaponReasons[12]{},weaponFrameReads=0;
uint32_t verifiedShaderSeen=0,verifiedBodyReady=0,verifiedWeaponReady=0;
bool registered=false;std::atomic<bool> active{false};
std::atomic<bool> localPreview{false};
uint64_t localPreviewStart=0,localPreviewDrawStart=0,localPreviewWeaponStart=0;
uint64_t localPreviewGloveStart=0,localPreviewGloveSkipStart=0;
bool localPreviewDrawReported=false,localPreviewWeaponReported=false;
bool localPreviewGloveReported=false;
thread_local bool insideDraw=false;
pawn_dance_shader::WorldConstants constants[KindCount+1];
uint64_t kindDraws[KindCount+1]{};
template<class T>T read(const uint8_t *data,size_t at){T value{};std::memcpy(&value,data+at,sizeof value);return value;}
template<class T>void publish(size_t at,T value){if(shared)std::memcpy(shared+at,&value,sizeof value);}
bool skip(unsigned reason){++skipped;lastSkipReason=reason;if(reason<12)++skipReasons[reason];return false;}
bool skip_weapon(unsigned reason){++weaponSkipped;weaponLastReason=reason;if(reason<12)++weaponReasons[reason];return false;}
bool skip_glove(unsigned reason){
 ++gloveSkipped;
 if(reason<12&&gloveReasons[reason]++==0){
  const std::string message="Local playback world glove pass skipped, reason="+std::to_string(reason);
  reshade::log::message(reshade::log::level::info,message.c_str());
 }
 return false;
}
uint64_t digest(const void *data,size_t bytes){uint64_t h=14695981039346656037ull;auto p=static_cast<const uint8_t *>(data);for(size_t i=0;i<bytes;i++){h^=p[i];h*=1099511628211ull;}return h;}
uint32_t verified_shader(const void *data,size_t bytes){
 // These exact original VS programs were captured from the supported local
 // meshes and passed the factory/GPU checks. Unrelated material variants must
 // not consume the cache budget before the real head or weapon VS is loaded.
 constexpr uint64_t hashes[]={0x2387017422B413AAull,0x2475524DEE2B5F61ull,0x2B5416567DE20D04ull,0x326CC496B5F6E5CBull,0x41D35E7502867175ull,0x5F84963B372D2DB9ull,0x6645D904CD7FE9EAull,0x6EA9886B3EE82400ull,0x767A1DCE231A3DE9ull,0x863CD8C4A9EB8F9Aull,0x9222CF965174A3D7ull,0x94BB64F78B68DCCAull,0xB5A684C1FE2F78DAull,0xC3BD282FFD4D68BAull,0xC746CAA5F9B8F40Aull,0xD43E2F2D8E488C16ull,0xD9E0E2B018FB4459ull,0xEB29C414326DFA0Cull,0xF41808054430E732ull};
 const uint64_t hash=digest(data,bytes);for(unsigned i=0;i<sizeof(hashes)/sizeof(hashes[0]);i++)if(hash==hashes[i])return 1u<<i;return 0;
}
int identify(const void *data,unsigned bytes,unsigned stride){
 if(!data||bytes<4096||!stride)return 0;
 uint64_t lastMask=UINT64_MAX,h=0;
 for(const auto &fp:fingerprints){
  if(fp.stride!=stride||bytes<fp.bytes||bytes>=fp.bytes+256)continue;
  if(fp.uvMask!=lastMask){lastMask=fp.uvMask;h=14695981039346656037ull;const auto *p=static_cast<const uint8_t *>(data);
   for(unsigned i=0;i<4096;i++){h^=(fp.uvMask&(1ull<<(i%stride)))?0:p[i];h*=1099511628211ull;}}
  if(h==fp.prefix)return fp.kind;
 }
 return 0;
}
bool candidate(unsigned bytes,unsigned stride){for(const auto &fp:fingerprints)if(fp.stride==stride&&bytes>=fp.bytes&&bytes<fp.bytes+256)return true;return false;}
bool glove_candidate(unsigned bytes,unsigned stride){for(const auto &fp:pawn_dance_glove_data::fingerprints)if(fp.stride==stride&&bytes>=fp.bytes&&bytes<fp.bytes+256)return true;return false;}
bool identify_glove(const void *data,unsigned bytes,unsigned stride){
 if(!data||bytes<4096||!stride)return false;
 uint64_t lastMask=UINT64_MAX,h=0;
 for(const auto &fp:pawn_dance_glove_data::fingerprints){
  if(fp.stride!=stride||bytes<fp.bytes||bytes>=fp.bytes+256)continue;
  if(fp.uvMask!=lastMask){lastMask=fp.uvMask;h=14695981039346656037ull;const auto *p=static_cast<const uint8_t *>(data);
   for(unsigned i=0;i<4096;i++){h^=(fp.uvMask&(1ull<<(i%stride)))?0:p[i];h*=1099511628211ull;}}
  if(h==fp.prefix)return true;
 }
 return false;
}
bool weapon_candidate(unsigned bytes,unsigned stride){for(const auto &fp:pawn_dance_weapon_data::fingerprints)if(fp.stride==stride&&bytes>=fp.bytes&&bytes<fp.bytes+256)return true;return false;}
WeaponMask identify_weapon(const void *data,unsigned bytes,unsigned stride){
 if(!data||bytes<4096||!stride)return {};
 WeaponMask result;uint64_t lastMask=UINT64_MAX,h=0;
 for(const auto &fp:pawn_dance_weapon_data::fingerprints){
  if(fp.stride!=stride||bytes<fp.bytes||bytes>=fp.bytes+256)continue;
  if(fp.uvMask!=lastMask){
   lastMask=fp.uvMask;
   if(!lastMask)h=digest(data,4096);
   else{h=14695981039346656037ull;const auto *p=static_cast<const uint8_t *>(data);
    for(unsigned i=0;i<4096;i++){h^=(fp.uvMask&(1ull<<(i%stride)))?0:p[i];h*=1099511628211ull;}}
  }
  if(fp.prefix==h)result.set(fp.mesh);
 }
 return result;
}
bool open_mapping(){
 if(shared)return true;uint64_t now=GetTickCount64();if(now-lastOpen<1000)return false;lastOpen=now;
 mapping=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,L"Local\\MC_CS_PawnDance");if(!mapping)return false;
 shared=static_cast<uint8_t *>(MapViewOfFile(mapping,FILE_MAP_READ|FILE_MAP_WRITE,0,0,4096));if(!shared){CloseHandle(mapping);mapping=nullptr;return false;}return true;
}
int64_t unix_ms(){FILETIME ft{};GetSystemTimePreciseAsFileTime(&ft);ULARGE_INTEGER t{};t.LowPart=ft.dwLowDateTime;t.HighPart=ft.dwHighDateTime;return int64_t((t.QuadPart-116444736000000000ull)/10000ull);}
// Called by the add-on's explicitly enabled local playback input path. This
// mode changes verified rendered meshes only; it publishes no server request.
bool local_preview_enabled(){return localPreview.load();}
void set_local_preview(bool enabled){
 std::lock_guard<std::recursive_mutex> lock(mutex);
 if(localPreview.load()==enabled)return;
 if(enabled){
  localPreviewStart=GetTickCount64();localPreviewDrawStart=totalDraws;localPreviewWeaponStart=weaponDraws;
  localPreviewGloveStart=gloveDraws;localPreviewGloveSkipStart=gloveSkipped;localPreviewGloveReported=false;std::fill(std::begin(gloveReasons),std::end(gloveReasons),0);
  localPreviewDrawReported=false;localPreviewWeaponReported=false;
  reshade::log::message(reshade::log::level::info,"Local playback dance ON (126 BPM; verified world models)");
 }else{
  const std::string message="Local playback dance OFF; verified model draws="+std::to_string(totalDraws-localPreviewDrawStart)+", weapon draws="+std::to_string(weaponDraws-localPreviewWeaponStart)+", world glove passes="+std::to_string(gloveDraws-localPreviewGloveStart)+", skipped gloves="+std::to_string(gloveSkipped-localPreviewGloveSkipStart);
  reshade::log::message(reshade::log::level::info,message.c_str());
 }
 localPreview.store(enabled);
}
void prepare_constants(double phase,bool local){
 const double squash=.5-.5*std::cos(6.283185307179586*phase);
 for(int kind=0;kind<=KindCount;kind++){
  constants[kind]=pawn_dance_shader::WorldConstants{};auto &c=constants[kind];
  c.enabled=1;c.scale[0]=c.scale[1]=float(1+.55*squash);c.scale[2]=float(1-.82*squash);
  c.yawRadians=float(std::fmod(phase,4.0)*1.5707963267948966);c.maxDistance=64;
  c.useInstanceRoot=1;c.rootBoneIndex=pawn_dance_agent_data::roots[kind];
  // Kind zero supplies attachment constants. Shared first-person gloves do
  // not identify a world player and must not gain blanket authorization.
  c.allowAllVerifiedInstances=local&&kind!=GloveKind?1u:0u;
 }
}
void begin(){
 std::lock_guard<std::recursive_mutex> lock(mutex);
 ++frame;fallbackFrameReads=0;weaponFrameReads=0;gloveFrameReads=0;active=false;eligibleActors=0;ownerCandidates.clear();
 const bool mapped=open_mapping();
 // The original shared diagnostics reserve four slots here. More supported
 // rigs must not overwrite the fallback-read counters beginning at 3352.
 for(int k=1;k<=KindCount&&k<=4;k++)publish<uint64_t>(3320+(k-1)*8,kindDraws[k]);
 publish<int32_t>(3072,programCount.load()>0?1:0);publish<uint64_t>(3080,GetTickCount64());publish<uint64_t>(3088,totalDraws);publish<uint32_t>(3096,eligibleActors);publish<uint32_t>(3100,uint32_t((std::min)(skipped,uint64_t(UINT32_MAX))));
 publish<uint32_t>(3112,lastSkipReason);publish<uint32_t>(3116,unsigned(sequentialInstances.size()));publish<uint32_t>(3120,instanceFallbackReads);publish<uint32_t>(3124,unsigned(layouts.size()));publish<uint32_t>(3128,unsigned(bindings.size()));publish<uint32_t>(3132,unsigned(programs.size()));for(unsigned i=0;i<12;i++)publish<uint32_t>(3136+i*4,skipReasons[i]);
 publish<uint32_t>(3184,observedTransformViewDimension);publish<uint32_t>(3188,observedInstanceViewDimension);publish<uint32_t>(3192,0);
 publish<uint64_t>(3200,pawn_dance_compute::seenDispatches.load());publish<uint64_t>(3208,pawn_dance_compute::acceptedViews.load());publish<uint32_t>(3216,pawn_dance_compute::knownShaders.load());
 publish<uint32_t>(3220,unsigned(weaponPrograms.size()));publish<uint64_t>(3224,weaponDraws);publish<uint64_t>(3232,weaponSkipped);publish<uint32_t>(3240,weaponLastReason);publish<uint32_t>(3244,weaponFallbackReads);for(unsigned i=0;i<12;i++)publish<uint32_t>(3248+4*i,weaponReasons[i]);
 publish<uint32_t>(3352,fallbackReads);publish<uint32_t>(3356,weaponFallbackReads);
 publish<uint32_t>(3308,2);publish<uint32_t>(3296,verifiedShaderSeen);publish<uint32_t>(3300,verifiedBodyReady);publish<uint32_t>(3304,verifiedWeaponReady);
 if(localPreview.load()){
  prepare_constants(double(GetTickCount64()-localPreviewStart)*126.0/60000.0,true);
  diagnosticError=0;publish<uint32_t>(3076,0);active=true;return;
 }
 if(!mapped)return;
 uint64_t seq=read<uint64_t>(shared,8);if(seq&1)return;uint8_t snapshot[3072]{};std::memcpy(snapshot,shared,sizeof snapshot);MemoryBarrier();if(seq!=read<uint64_t>(shared,8))return;
 if(read<uint32_t>(snapshot,0)!=0x50444E43||read<uint32_t>(snapshot,4)!=2)return;
 uint64_t heartbeat=read<uint64_t>(snapshot,16);if(GetTickCount64()-heartbeat>1500){publish<uint32_t>(3076,1);return;}
 diagnosticError=0;publish<uint32_t>(3076,0);if(!read<int32_t>(snapshot,24))return;
 int count=read<int32_t>(snapshot,28);float bpm=read<float>(snapshot,40);int64_t start=read<int64_t>(snapshot,32);
 if(count<0||count>64||!std::isfinite(bpm)||bpm<40||bpm>240||start<0){publish<uint32_t>(3076,2);return;}
 uint32_t target=read<uint32_t>(snapshot,44);std::vector<Actor> actors;
 for(int i=0;i<count;i++){const uint8_t *a=snapshot+128+i*32;Actor actor{read<uint32_t>(a,0),read<int32_t>(a,4),read<float>(a,8),read<float>(a,12),read<float>(a,16),read<float>(a,20)};
  if(target&&target!=actor.pawn)continue;
  if((actor.kind<1||actor.kind>=GloveKind)||!std::isfinite(actor.x)||!std::isfinite(actor.y)||!std::isfinite(actor.z)||!std::isfinite(actor.scale)||actor.scale<.1f||actor.scale>3)continue;actors.push_back(actor);
 }
 if(actors.empty())return;
 double phase=(unix_ms()-start)*double(bpm)/60000.0;prepare_constants(phase,false);
 for(int kind=0;kind<=KindCount;kind++){auto &c=constants[kind];
  for(const auto &a:actors)if((kind==0||kind==GloveKind||a.kind==kind)&&c.actorCount<pawn_dance_shader::MaxActors){auto &p=c.feet[c.actorCount++];p[0]=a.x;p[1]=a.y;p[2]=a.z;p[3]=a.scale;}
 }
 eligibleActors=unsigned(actors.size());publish<uint32_t>(3096,eligibleActors);active=true;
}
void pipeline(reshade::api::device *device,reshade::api::pipeline_layout,uint32_t count,const reshade::api::pipeline_subobject *objects,reshade::api::pipeline pipeline){
 if(device->get_api()!=reshade::api::device_api::d3d11)return;
 std::lock_guard<std::recursive_mutex> lock(mutex);
 for(unsigned i=0;i<count;i++){
  if(objects[i].type==reshade::api::pipeline_subobject_type::input_layout&&objects[i].data&&layouts.size()<1024){
   const auto *elements=static_cast<const reshade::api::input_element *>(objects[i].data);
   for(unsigned j=0;j<objects[i].count;j++){const auto &e=elements[j];
    if(e.semantic&&!_stricmp(e.semantic,"TEXCOORD")&&e.semantic_index==13&&e.format==reshade::api::format::r32_uint&&e.instance_step_rate==1&&e.buffer_binding<D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT){InstanceLayout layout;layout.original=reinterpret_cast<ID3D11InputLayout *>(pipeline.handle);layout.slot=e.buffer_binding;layout.offset=e.offset;layout.step=e.instance_step_rate;layouts.insert_or_assign(pipeline.handle,std::move(layout));break;}
   }
  }
  if(objects[i].type!=reshade::api::pipeline_subobject_type::vertex_shader||!objects[i].data)continue;
  const auto &code=*static_cast<const reshade::api::shader_desc *>(objects[i].data);
  if(!code.code||code.code_size>256*1024)continue;
  const uint32_t verified=verified_shader(code.code,code.code_size);verifiedShaderSeen|=verified;
  bool pawnInstanceLayout=false;ComPtr<ID3D11ShaderReflection> reflection;
  if(SUCCEEDED(D3DReflect(code.code,code.code_size,__uuidof(ID3D11ShaderReflection),reinterpret_cast<void **>(reflection.GetAddressOf())))){
   D3D11_SHADER_INPUT_BIND_DESC resource{};ShaderBindings slots;slots.original=reinterpret_cast<ID3D11VertexShader *>(pipeline.handle);
   if(SUCCEEDED(reflection->GetResourceBindingDescByName("g_instanceBuffer",&resource))&&resource.BindCount==1)slots.instance=int(resource.BindPoint);
   if(SUCCEEDED(reflection->GetResourceBindingDescByName("g_transformBuffer",&resource))&&resource.BindCount==1)slots.transform=int(resource.BindPoint);
   pawnInstanceLayout=slots.instance>=0;
   if((slots.instance>=0||slots.transform>=0)&&bindings.size()<2048)bindings.insert_or_assign(pipeline.handle,std::move(slots));
  }
  if((programs.size()<64||((verified||pawnInstanceLayout)&&programs.size()<256))&&!programs.contains(pipeline.handle)){
   auto compiled=pawn_dance_cache::body(digest(code.code,code.code_size));
   if(compiled){Program program;program.original=reinterpret_cast<ID3D11VertexShader *>(pipeline.handle);program.compiled=std::move(compiled);program.pass=pawn_dance_shader::create(reinterpret_cast<ID3D11Device *>(device->get_native()),program.compiled);if(program.pass){
    programs.emplace(pipeline.handle,std::move(program));programCount.store(unsigned(programs.size()));verifiedBodyReady|=verified;
   }}
  }
  // Weapon depth/minimal variants are independent of the verified pawn pass;
  // both programs may be generated for a shared original VS.
  if((weaponPrograms.size()<96||(verified&&weaponPrograms.size()<2048))&&!weaponPrograms.contains(pipeline.handle)){
   auto compiled=pawn_dance_cache::weapon(digest(code.code,code.code_size));
   if(compiled){WeaponProgram program;program.original=reinterpret_cast<ID3D11VertexShader *>(pipeline.handle);program.compiled=std::move(compiled);program.pass=pawn_dance_weapon_shader::create(reinterpret_cast<ID3D11Device *>(device->get_native()),program.compiled);if(program.pass){weaponPrograms.emplace(pipeline.handle,std::move(program));verifiedWeaponReady|=verified;}}
  }
 }
}
void init_resource(reshade::api::device *device,const reshade::api::resource_desc &desc,const reshade::api::subresource_data *initial,reshade::api::resource_usage,reshade::api::resource resource){
 if(device->get_api()!=reshade::api::device_api::d3d11||desc.type!=reshade::api::resource_type::buffer||!initial||!initial->data)return;
 unsigned bytes=unsigned(desc.buffer.size);if(desc.buffer.size>4*1024*1024||bytes<4096)return;
 for(const auto &fp:pawn_dance_glove_data::fingerprints){if(bytes<fp.bytes||bytes>=fp.bytes+256)continue;if(identify_glove(initial->data,bytes,fp.stride)){std::lock_guard<std::recursive_mutex> lock(mutex);gloveBuffers[resource.handle]={1,fp.stride};break;}}
 for(const auto &fp:pawn_dance_weapon_data::fingerprints){if(bytes<fp.bytes||bytes>=fp.bytes+256)continue;auto mask=identify_weapon(initial->data,bytes,fp.stride);if(mask.any()){std::lock_guard<std::recursive_mutex> lock(mutex);weaponBuffers[resource.handle]={mask,fp.stride};break;}}
 if(bytes>1024*1024)return;
 if(bytes==524288){
  auto *buffer=reinterpret_cast<ID3D11Buffer *>(resource.handle);D3D11_BUFFER_DESC d{};buffer->GetDesc(&d);
  if(d.Usage==D3D11_USAGE_IMMUTABLE&&(d.BindFlags&D3D11_BIND_VERTEX_BUFFER)&&!d.StructureByteStride){
   bool sequence=true;for(unsigned i=0;i<bytes/4;i++)if(read<uint32_t>(static_cast<const uint8_t *>(initial->data),i*4)!=i){sequence=false;break;}
   if(sequence){std::lock_guard<std::recursive_mutex> lock(mutex);sequentialInstances[resource.handle]=bytes/4;return;}
  }
 }
 for(const auto &fp:fingerprints){if(bytes<fp.bytes||bytes>=fp.bytes+256)continue;int kind=identify(initial->data,bytes,fp.stride);if(kind){std::lock_guard<std::recursive_mutex> lock(mutex);buffers[resource.handle]={kind,fp.stride};return;}}
}
void destroy_resource(reshade::api::device *,reshade::api::resource resource){std::lock_guard<std::recursive_mutex> lock(mutex);readbacks.erase(resource.handle);buffers.erase(resource.handle);weaponBuffers.erase(resource.handle);gloveBuffers.erase(resource.handle);sequentialInstances.erase(resource.handle);}
bool mesh_prefix(ID3D11DeviceContext *context,ID3D11Buffer *buffer,unsigned bytes,unsigned stride){
 std::vector<uint8_t> data;if(!readbacks.read(context,buffer,4096,frame,data))return false;
 const uint64_t key=reinterpret_cast<uint64_t>(buffer);
 // Identify all categories from the single completed copy; avoid three reads.
 buffers[key]={identify(data.data(),bytes,stride),stride};
 weaponBuffers[key]={identify_weapon(data.data(),bytes,stride),stride};
 gloveBuffers[key]={identify_glove(data.data(),bytes,stride)?1:0,stride};
 return true;
}
BufferIdentity identity(ID3D11DeviceContext *context,ID3D11Buffer *buffer,unsigned stride){
 if(!buffer)return{};const uint64_t key=reinterpret_cast<uint64_t>(buffer);auto hit=buffers.find(key);if(hit!=buffers.end())return hit->second;
 D3D11_BUFFER_DESC desc{};buffer->GetDesc(&desc);if(!candidate(desc.ByteWidth,stride))return{};
 if(!mesh_prefix(context,buffer,desc.ByteWidth,stride))return{};++fallbackReads;return buffers[key];
}
WeaponIdentity weapon_identity(ID3D11DeviceContext *context,ID3D11Buffer *buffer,unsigned stride){
 if(!buffer)return{};const uint64_t key=reinterpret_cast<uint64_t>(buffer);auto hit=weaponBuffers.find(key);if(hit!=weaponBuffers.end())return hit->second;
 D3D11_BUFFER_DESC desc{};buffer->GetDesc(&desc);if(!weapon_candidate(desc.ByteWidth,stride))return{};
 if(!mesh_prefix(context,buffer,desc.ByteWidth,stride))return{};++weaponFallbackReads;return weaponBuffers[key];
}
BufferIdentity glove_identity(ID3D11DeviceContext *context,ID3D11Buffer *buffer,unsigned stride){
 if(!buffer)return{};const uint64_t key=reinterpret_cast<uint64_t>(buffer);auto hit=gloveBuffers.find(key);if(hit!=gloveBuffers.end())return hit->second;
 D3D11_BUFFER_DESC desc{};buffer->GetDesc(&desc);if(!glove_candidate(desc.ByteWidth,stride))return{};
 if(!mesh_prefix(context,buffer,desc.ByteWidth,stride))return{};return gloveBuffers[key];
}
struct SavedConstants {
 ID3D11DeviceContext *context;ComPtr<ID3D11DeviceContext1> context1;ID3D11Buffer *buffers[4]{};ID3D11ShaderResourceView *resources[3]{};UINT first[4]{},counts[4]{};
 explicit SavedConstants(ID3D11DeviceContext *ctx):context(ctx){ctx->QueryInterface(__uuidof(ID3D11DeviceContext1),reinterpret_cast<void **>(context1.GetAddressOf()));if(context1)context1->GSGetConstantBuffers1(10,4,buffers,first,counts);else ctx->GSGetConstantBuffers(10,4,buffers);ctx->GSGetShaderResources(pawn_dance_shader::InstanceResourceSlot,3,resources);}
 ~SavedConstants(){context->GSSetShader(nullptr,nullptr,0);if(context1)context1->GSSetConstantBuffers1(10,4,buffers,first,counts);else context->GSSetConstantBuffers(10,4,buffers);context->GSSetShaderResources(pawn_dance_shader::InstanceResourceSlot,3,resources);for(auto *buffer:buffers)if(buffer)buffer->Release();for(auto *resource:resources)if(resource)resource->Release();}
 bool bind_original(unsigned vertexSlot,unsigned geometrySlot){ComPtr<ID3D11Buffer> buffer;UINT start=0,count=4096;if(context1)context1->VSGetConstantBuffers1(vertexSlot,1,&buffer,&start,&count);else context->VSGetConstantBuffers(vertexSlot,1,&buffer);if(!buffer)return false;auto *p=buffer.Get();if(context1)context1->GSSetConstantBuffers1(geometrySlot,1,&p,&start,&count);else context->GSSetConstantBuffers(geometrySlot,1,&p);return true;}
};
struct SavedCompute {
 ID3D11DeviceContext *context;ComPtr<ID3D11DeviceContext1> context1;ComPtr<ID3D11ComputeShader> shader;ID3D11ClassInstance *classes[256]{};UINT classCount=256;ID3D11Buffer *constant=nullptr;UINT first=0,count=4096;ID3D11ShaderResourceView *resources[2]{};ID3D11UnorderedAccessView *output=nullptr;
 explicit SavedCompute(ID3D11DeviceContext *ctx):context(ctx){ctx->QueryInterface(__uuidof(ID3D11DeviceContext1),reinterpret_cast<void **>(context1.GetAddressOf()));ctx->CSGetShader(&shader,classes,&classCount);if(context1)context1->CSGetConstantBuffers1(13,1,&constant,&first,&count);else ctx->CSGetConstantBuffers(13,1,&constant);ctx->CSGetShaderResources(14,2,resources);ctx->CSGetUnorderedAccessViews(0,1,&output);}
 ~SavedCompute(){UINT preserve=UINT_MAX;context->CSSetUnorderedAccessViews(0,1,&output,&preserve);context->CSSetShaderResources(14,2,resources);if(context1)context1->CSSetConstantBuffers1(13,1,&constant,&first,&count);else context->CSSetConstantBuffers(13,1,&constant);context->CSSetShader(shader.Get(),classes,classCount);if(constant)constant->Release();if(output)output->Release();for(auto *resource:resources)if(resource)resource->Release();for(unsigned i=0;i<classCount;i++)if(classes[i])classes[i]->Release();}
};
bool structured_view(ID3D11ShaderResourceView *view,unsigned stride){
 if(!view)return false;D3D11_SHADER_RESOURCE_VIEW_DESC vd{};view->GetDesc(&vd);
 if(stride==48)observedTransformViewDimension=unsigned(vd.ViewDimension);if(stride==32)observedInstanceViewDimension=unsigned(vd.ViewDimension);
 const bool bufferView=vd.ViewDimension==D3D11_SRV_DIMENSION_BUFFER||(vd.ViewDimension==D3D11_SRV_DIMENSION_BUFFEREX&&vd.BufferEx.Flags==0);
 if(!bufferView||vd.Format!=DXGI_FORMAT_UNKNOWN||!vd.Buffer.NumElements)return false;
 ComPtr<ID3D11Resource> resource;ComPtr<ID3D11Buffer> buffer;view->GetResource(&resource);if(FAILED(resource.As(&buffer)))return false;D3D11_BUFFER_DESC d{};buffer->GetDesc(&d);
 return d.StructureByteStride==stride&&(d.MiscFlags&D3D11_RESOURCE_MISC_BUFFER_STRUCTURED)!=0;
}
bool same_view_range(ID3D11ShaderResourceView *a,ID3D11ShaderResourceView *b){
 if(a==b)return true;if(!a||!b)return false;D3D11_SHADER_RESOURCE_VIEW_DESC da{},db{};a->GetDesc(&da);b->GetDesc(&db);if(da.Format!=db.Format||da.Buffer.FirstElement!=db.Buffer.FirstElement||da.Buffer.NumElements!=db.Buffer.NumElements)return false;
 ComPtr<ID3D11Resource> ra,rb;a->GetResource(&ra);b->GetResource(&rb);return ra.Get()==rb.Get();
}
bool compute_enabled(){return active&&!insideDraw;}
void accept_compute_transform(ID3D11DeviceContext *context,ID3D11ShaderResourceView *view){
 if(!active||insideDraw)return;std::lock_guard<std::recursive_mutex> lock(mutex);
 if(structured_view(view,48)){lastTransforms=view;transformContext=context;transformFrame=frame;}
}
void record_owner_candidate(ID3D11Buffer *vertexBuffer,unsigned kind,uint32_t id,ID3D11ShaderResourceView *instances,ID3D11ShaderResourceView *transforms){
 // Register verified body skeleton instances; exclude shared first-person gloves.
 // Collapse material/head/body draws for the same instance and table views. IDs refer only to these exact, same-frame GPU buffer views.
 if(kind<1||kind>KindCount||kind==GloveKind||ownerCandidates.size()>=128)return;
 for(const auto &candidate:ownerCandidates)if(candidate.instance==id&&same_view_range(candidate.instances.Get(),instances)&&same_view_range(candidate.transforms.Get(),transforms))return;
 OwnerCandidate candidate;candidate.instance=id;candidate.kind=kind;candidate.instances=instances;candidate.transforms=transforms;ownerCandidates.push_back(std::move(candidate));
 publish<uint32_t>(3192,unsigned(ownerCandidates.size()));
}
unsigned verify_instances(ID3D11DeviceContext *context,ID3D11Buffer *stream){
 const uint64_t key=reinterpret_cast<uint64_t>(stream);auto hit=sequentialInstances.find(key);if(hit!=sequentialInstances.end())return hit->second;
 D3D11_BUFFER_DESC desc{};stream->GetDesc(&desc);
 if(desc.ByteWidth!=524288||desc.Usage!=D3D11_USAGE_IMMUTABLE||desc.BindFlags!=D3D11_BIND_VERTEX_BUFFER||desc.StructureByteStride)return 0;
 std::vector<uint8_t> data;if(!readbacks.read(context,stream,desc.ByteWidth,frame,data))return 0;
 ++instanceFallbackReads;unsigned count=desc.ByteWidth/4;
 for(unsigned i=0;i<count;i++)if(read<uint32_t>(data.data(),i*4)!=i){count=0;break;}
 sequentialInstances[key]=count;return count;
}
unsigned instance_id(ID3D11DeviceContext *context,uint32_t firstInstance,uint32_t &id){
 ComPtr<ID3D11InputLayout> layout;context->IAGetInputLayout(&layout);auto found=layouts.find(reinterpret_cast<uint64_t>(layout.Get()));if(found==layouts.end())return 5;
 ComPtr<ID3D11Buffer> stream;UINT stride=0,offset=0;context->IAGetVertexBuffers(found->second.slot,1,&stream,&stride,&offset);if(!stream||stride!=4)return 6;
 unsigned count=verify_instances(context,stream.Get());if(!count)return 7;
 uint64_t at=uint64_t(offset)+found->second.offset+uint64_t(firstInstance)*stride;if(at%4||at/4>=count)return 8;id=uint32_t(at/4);return 0;
}
bool draw_glove(ID3D11DeviceContext *context,uint64_t shaderKey,ID3D11Buffer *vb,ID3D11Buffer *ib,unsigned stride,uint32_t indexCount,uint32_t instanceCount,uint32_t firstIndex,int32_t vertexOffset,uint32_t firstInstance,bool defaultSkeleton=false){
 if(!localPreview.load())return false;
 if(!defaultSkeleton){const auto vi=glove_identity(context,vb,stride),ii=glove_identity(context,ib,2);
  if(!vi.kind||!ii.kind||vi.stride!=stride||ii.stride!=2)return false;}
 // This separate whitelist contains only installed w_ glove meshes and has
 // no overlap with v_ viewmodels/default gloves. The first-person-legs
 // material flag is not treated as a general viewmodel discriminator.
 // Reuse the attachment GS factory for color AND depth signatures. Ownership
 // still comes from the independent bilateral-hand compute pass below.
 auto found=weaponPrograms.find(shaderKey);if(found==weaponPrograms.end()||!found->second.pass)return skip_glove(1);
 auto slots=bindings.find(shaderKey);if(slots==bindings.end()||slots->second.instance<0)return skip_glove(2);
 if(instanceCount!=1)return skip_glove(3);
 if(transformContext!=context||transformFrame!=frame||!lastTransforms)return skip_glove(4);
 uint32_t instanceId=0;if(instance_id(context,firstInstance,instanceId))return skip_glove(5);
 ComPtr<ID3D11ShaderResourceView> instanceView;context->VSGetShaderResources(unsigned(slots->second.instance),1,&instanceView);if(!structured_view(instanceView.Get(),32))return skip_glove(6);
 pawn_dance_glove_shader::Constants c{};c.dance=constants[0];c.dance.instanceId=instanceId;c.defaultSkeleton=defaultSkeleton?1:0;
 for(const auto &owner:ownerCandidates){
  if(!same_view_range(owner.instances.Get(),instanceView.Get())||!same_view_range(owner.transforms.Get(),lastTransforms.Get()))continue;
  if(c.ownerCount>=pawn_dance_glove_shader::MaxOwners)break;
  auto &entry=c.owners[c.ownerCount++];entry[0]=owner.instance;entry[1]=owner.kind;entry[2]=pawn_dance_agent_data::roots[owner.kind]+2;
 }
 // A glove preceding its body in the frame is deliberately left unchanged.
 // No previous-frame instance ID or nearest-position fallback is permitted.
 if(!c.ownerCount)return skip_glove(7);
 std::string reason;if(!pawn_dance_shader::can_apply(context,reason))return skip_glove(8);
 ComPtr<ID3D11Device> device;context->GetDevice(&device);
 if(gloveDevice.Get()!=device.Get()){
  gloveOwnerPass={};gloveDevice=device;auto compiled=pawn_dance_cache::glove();gloveOwnerPass=pawn_dance_glove_shader::create(device.Get(),compiled);
  if(!gloveOwnerPass){const std::string message="World glove owner pass unavailable: "+(compiled?gloveOwnerPass.error:compiled.error);reshade::log::message(reshade::log::level::error,message.c_str());}
 }
 if(!gloveOwnerPass)return skip_glove(9);
 auto &program=found->second;SavedConstants saved(context);
 if(!saved.bind_original(program.compiled.perViewSlot,pawn_dance_shader::PerViewSlot)||(program.compiled.needsCsgo&&!saved.bind_original(program.compiled.csgoSlot,pawn_dance_shader::CsgoViewSlot))||(program.compiled.needsMaterial&&!saved.bind_original(program.compiled.materialSlot,pawn_dance_shader::MaterialSlot)))return skip_glove(10);
 context->UpdateSubresource(gloveOwnerPass.constants.Get(),0,nullptr,&c,0,0);
 struct ReentryGuard{ReentryGuard(){insideDraw=true;}~ReentryGuard(){insideDraw=false;}} reentry;
 auto *constantBuffer=gloveOwnerPass.constants.Get();
 {
  SavedCompute compute(context);ID3D11ShaderResourceView *empty=nullptr;context->GSSetShaderResources(16,1,&empty);
  ID3D11ShaderResourceView *inputs[]={instanceView.Get(),lastTransforms.Get()};context->CSSetShaderResources(14,2,inputs);context->CSSetConstantBuffers(13,1,&constantBuffer);
  auto *output=gloveOwnerPass.output.Get();UINT preserve=UINT_MAX;context->CSSetUnorderedAccessViews(0,1,&output,&preserve);context->CSSetShader(gloveOwnerPass.shader.Get(),nullptr,0);context->Dispatch(1,1,1);
 }
 // Only the common WorldConstants prefix is read by this GS. Bind a correctly
 // sized attachment buffer rather than the glove CS's different owner layout.
 pawn_dance_weapon_shader::Constants geometry{};geometry.dance=c.dance;
 context->UpdateSubresource(program.pass.constants.Get(),0,nullptr,&geometry,0,0);
 auto *geometryBuffer=program.pass.constants.Get();
 auto *ownerView=gloveOwnerPass.view.Get();context->GSSetShaderResources(16,1,&ownerView);context->GSSetConstantBuffers(pawn_dance_shader::ConstantBufferSlot,1,&geometryBuffer);context->GSSetShader(program.pass.shader.Get(),nullptr,0);
 context->DrawIndexedInstanced(indexCount,instanceCount,firstIndex,vertexOffset,firstInstance);++gloveDraws;
 if(!localPreviewGloveReported){localPreviewGloveReported=true;const std::string message="Local playback dance: first verified world glove pass submitted, body candidates="+std::to_string(c.ownerCount);reshade::log::message(reshade::log::level::info,message.c_str());}
 return true;
}
bool draw_weapon(ID3D11DeviceContext *context,uint64_t shaderKey,ID3D11Buffer *vb,ID3D11Buffer *ib,unsigned stride,uint32_t indexCount,uint32_t instanceCount,uint32_t firstIndex,int32_t vertexOffset,uint32_t firstInstance){
 auto vi=weapon_identity(context,vb,stride),ii=weapon_identity(context,ib,2);const auto common=vi.meshes&ii.meshes;if(common.none()||vi.stride!=stride||ii.stride!=2)return false;
 // A shared IB may have several HD/legacy candidates; the independently
 // fingerprinted VB must reduce that set to a unique actual mesh.
 if(common.count()!=1)return skip_weapon(1);unsigned mesh=0;while(!common.test(mesh))++mesh;
 auto found=weaponPrograms.find(shaderKey);if(found==weaponPrograms.end())return skip_weapon(2);
 auto slots=bindings.find(shaderKey);if(slots==bindings.end()||slots->second.instance<0)return skip_weapon(3);
 if(instanceCount!=1)return skip_weapon(4);if(transformContext!=context||transformFrame!=frame||!lastTransforms)return skip_weapon(5);
 uint32_t instanceId=0;if(instance_id(context,firstInstance,instanceId))return skip_weapon(6);
 ComPtr<ID3D11ShaderResourceView> instanceView;context->VSGetShaderResources(unsigned(slots->second.instance),1,&instanceView);if(!structured_view(instanceView.Get(),32))return skip_weapon(7);
 pawn_dance_weapon_shader::Constants c{};c.dance=constants[0];c.dance.instanceId=instanceId;
 const auto &model=pawn_dance_weapon_data::meshes[mesh];std::memcpy(c.weaponPoints[0],model.held,12);std::memcpy(c.weaponPoints[1],model.holster,12);c.hasHolster=model.hasHolster?1:0;
 // Candidate IDs are valid only for these exact GPU tables and this frame.
 // Never match by nearest actor, stale IDs or a weapon's server AbsOrigin.
 for(const auto &owner:ownerCandidates){if(!same_view_range(owner.instances.Get(),instanceView.Get())||!same_view_range(owner.transforms.Get(),lastTransforms.Get()))continue;
  if(c.ownerCount>=pawn_dance_weapon_shader::MaxOwners)break;auto &entry=c.owners[c.ownerCount++];entry[0]=owner.instance;entry[1]=owner.kind;entry[2]=pawn_dance_agent_data::roots[owner.kind]+2;
 }
 if(!c.ownerCount)return skip_weapon(8);std::string reason;if(!pawn_dance_shader::can_apply(context,reason))return skip_weapon(9);
 auto &program=found->second;context->UpdateSubresource(program.pass.constants.Get(),0,nullptr,&c,0,0);
 SavedConstants saved(context);
 if(!saved.bind_original(program.compiled.perViewSlot,pawn_dance_shader::PerViewSlot)||(program.compiled.needsCsgo&&!saved.bind_original(program.compiled.csgoSlot,pawn_dance_shader::CsgoViewSlot))||(program.compiled.needsMaterial&&!saved.bind_original(program.compiled.materialSlot,pawn_dance_shader::MaterialSlot)))return skip_weapon(10);
 struct ReentryGuard{ReentryGuard(){insideDraw=true;}~ReentryGuard(){insideDraw=false;}} reentry;
 auto *constantBuffer=program.pass.constants.Get();
 {
  // Resolve ownership once on the GPU. Doing this inside the GS would repeat
  // the same socket search for every triangle of each high-detail gun.
  SavedCompute compute(context);ID3D11ShaderResourceView *empty=nullptr;context->GSSetShaderResources(16,1,&empty);
  ID3D11ShaderResourceView *inputs[]={instanceView.Get(),lastTransforms.Get()};context->CSSetShaderResources(14,2,inputs);context->CSSetConstantBuffers(13,1,&constantBuffer);auto *output=program.pass.ownerOutput.Get();UINT preserve=UINT_MAX;context->CSSetUnorderedAccessViews(0,1,&output,&preserve);context->CSSetShader(program.pass.ownerShader.Get(),nullptr,0);context->Dispatch(1,1,1);
 }
 auto *ownerView=program.pass.ownerView.Get();context->GSSetShaderResources(16,1,&ownerView);context->GSSetConstantBuffers(pawn_dance_shader::ConstantBufferSlot,1,&constantBuffer);context->GSSetShader(program.pass.shader.Get(),nullptr,0);
 context->DrawIndexedInstanced(indexCount,instanceCount,firstIndex,vertexOffset,firstInstance);++weaponDraws;
 if(localPreview.load()&&!localPreviewWeaponReported){localPreviewWeaponReported=true;reshade::log::message(reshade::log::level::info,"Local playback dance: first verified weapon pass submitted");}
 return true;
}
bool indexed(reshade::api::command_list *cmd,uint32_t indexCount,uint32_t instanceCount,uint32_t firstIndex,int32_t vertexOffset,uint32_t firstInstance){
 if(insideDraw||!active||!instanceCount||cmd->get_device()->get_api()!=reshade::api::device_api::d3d11)return false;
 auto *context=reinterpret_cast<ID3D11DeviceContext *>(cmd->get_native());ComPtr<ID3D11VertexShader> vertex;context->VSGetShader(&vertex,nullptr,nullptr);if(!vertex)return false;
 std::lock_guard<std::recursive_mutex> lock(mutex);auto shaderKey=reinterpret_cast<uint64_t>(vertex.Get());auto slots=bindings.find(shaderKey);
 // The body shaders consume the computed vertex cache, while eye and rigid
 // shaders expose the shared transform SRV. Only reuse a validated view seen
 // on this context in this frame, never a stale map/device buffer.
 if(slots!=bindings.end()&&slots->second.transform>=0){ComPtr<ID3D11ShaderResourceView> view;context->VSGetShaderResources(unsigned(slots->second.transform),1,&view);if(structured_view(view.Get(),48)){lastTransforms=view;transformContext=context;transformFrame=frame;}}
 auto found=programs.find(shaderKey);if(found==programs.end()&&!weaponPrograms.contains(shaderKey))return false;
 ComPtr<ID3D11Buffer> vb,ib;UINT stride=0,offset=0,indexOffset=0;DXGI_FORMAT format{};context->IAGetVertexBuffers(0,1,&vb,&stride,&offset);context->IAGetIndexBuffer(&ib,&format,&indexOffset);
 if(offset!=0||indexOffset!=0||format!=DXGI_FORMAT_R16_UINT)return skip(1);
 auto vi=identity(context,vb.Get(),stride),ii=identity(context,ib.Get(),2);if(!vi.kind||vi.kind!=ii.kind||vi.stride!=stride||ii.stride!=2){
  if(draw_glove(context,shaderKey,vb.Get(),ib.Get(),stride,indexCount,instanceCount,firstIndex,vertexOffset,firstInstance))return true;
  return draw_weapon(context,shaderKey,vb.Get(),ib.Get(),stride,indexCount,instanceCount,firstIndex,vertexOffset,firstInstance);
 }
 if(vi.kind==GloveKind&&localPreview.load())return draw_glove(context,shaderKey,vb.Get(),ib.Get(),stride,indexCount,instanceCount,firstIndex,vertexOffset,firstInstance,true);
 if(!constants[vi.kind].actorCount&&!constants[vi.kind].allowAllVerifiedInstances)return false;
 if(slots==bindings.end()||slots->second.instance<0)return skip(2);
 if(instanceCount!=1)return skip(3);
 if(transformContext!=context||transformFrame!=frame||!lastTransforms)return skip(4);
 uint32_t instanceId=0;unsigned failure=instance_id(context,firstInstance,instanceId);if(failure)return skip(failure);
 ComPtr<ID3D11ShaderResourceView> instanceView;context->VSGetShaderResources(unsigned(slots->second.instance),1,&instanceView);if(!structured_view(instanceView.Get(),32))return skip(9);
 // Depth draws establish current-frame owners before their color passes.
 // Identity has already required matching verified vertex and index buffers.
 record_owner_candidate(vb.Get(),vi.kind,instanceId,instanceView.Get(),lastTransforms.Get());
 if(found==programs.end())return false;
 std::string reason;if(!pawn_dance_shader::can_apply(context,reason))return skip(10);
 auto &program=found->second;
 if(program.lastFrame!=frame||program.lastKind!=vi.kind||program.lastInstance!=instanceId){auto c=constants[vi.kind];c.instanceId=instanceId;context->UpdateSubresource(program.pass.constants.Get(),0,nullptr,&c,0,0);program.lastFrame=frame;program.lastKind=vi.kind;program.lastInstance=instanceId;}
 SavedConstants saved(context);
 if(!saved.bind_original(program.compiled.perViewSourceSlot,pawn_dance_shader::PerViewSlot)
    ||(program.compiled.csgoViewSourceSlot!=UINT32_MAX&&!saved.bind_original(program.compiled.csgoViewSourceSlot,pawn_dance_shader::CsgoViewSlot))
    ||(program.compiled.materialSourceSlot!=UINT32_MAX&&!saved.bind_original(program.compiled.materialSourceSlot,pawn_dance_shader::MaterialSlot)))return skip(11);
 ID3D11ShaderResourceView *rootResources[]={instanceView.Get(),lastTransforms.Get()};context->GSSetShaderResources(pawn_dance_shader::InstanceResourceSlot,2,rootResources);
 auto *constantBuffer=program.pass.constants.Get();context->GSSetConstantBuffers(pawn_dance_shader::ConstantBufferSlot,1,&constantBuffer);context->GSSetShader(program.pass.shader.Get(),nullptr,0);
 struct ReentryGuard{ReentryGuard(){insideDraw=true;}~ReentryGuard(){insideDraw=false;}} reentry;
 context->DrawIndexedInstanced(indexCount,instanceCount,firstIndex,vertexOffset,firstInstance);++totalDraws;++kindDraws[vi.kind];
 if(localPreview.load()&&!localPreviewDrawReported){localPreviewDrawReported=true;const std::string message="Local playback dance: first verified model pass submitted, kind="+std::to_string(vi.kind);reshade::log::message(reshade::log::level::info,message.c_str());}
 return true;
}
void init(){if(registered)return;pawn_dance_compute::init(compute_enabled,accept_compute_transform);reshade::register_event<reshade::addon_event::init_pipeline>(pipeline);reshade::register_event<reshade::addon_event::init_resource>(init_resource);reshade::register_event<reshade::addon_event::destroy_resource>(destroy_resource);reshade::register_event<reshade::addon_event::draw_indexed>(indexed);registered=true;open_mapping();}
void shutdown(){
 localPreview=false;localPreviewStart=0;active=false;pawn_dance_compute::shutdown();if(registered){reshade::unregister_event<reshade::addon_event::init_pipeline>(pipeline);reshade::unregister_event<reshade::addon_event::init_resource>(init_resource);reshade::unregister_event<reshade::addon_event::destroy_resource>(destroy_resource);reshade::unregister_event<reshade::addon_event::draw_indexed>(indexed);registered=false;}
 publish<int32_t>(3072,0);ownerCandidates.clear();readbacks.clear();programs.clear();weaponPrograms.clear();buffers.clear();weaponBuffers.clear();gloveBuffers.clear();gloveOwnerPass={};gloveDevice.Reset();bindings.clear();layouts.clear();sequentialInstances.clear();lastTransforms.Reset();transformContext=nullptr;transformFrame=0;programCount.store(0);if(shared)UnmapViewOfFile(shared);shared=nullptr;if(mapping)CloseHandle(mapping);mapping=nullptr;
}
}
