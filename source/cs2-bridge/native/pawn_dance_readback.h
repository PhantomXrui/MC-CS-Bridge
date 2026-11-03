#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstring>
#include <unordered_map>
#include <vector>

namespace pawn_dance_readback {
// Caller serializes access. No wait, Flush, spin loop or CPU access to animated
// pose buffers. Pending staging copies are bounded and polled once per frame.
struct Pending {Microsoft::WRL::ComPtr<ID3D11Buffer> staging;uint64_t polled=0;unsigned bytes=0;};
struct Queue {
 std::unordered_map<uint64_t,Pending> pending;
 unsigned queuedBytes=0,submitted=0;
 uint64_t submissionFrame=0;
 static constexpr unsigned MaxPending=64,MaxBytes=2*1024*1024,MaxPerFrame=8;
 void erase(uint64_t key){auto it=pending.find(key);if(it==pending.end())return;auto retired=std::move(it->second.staging);queuedBytes-=it->second.bytes;pending.erase(it);}
 void clear(){pending.clear();queuedBytes=0;submitted=0;submissionFrame=0;}
 bool read(ID3D11DeviceContext*ctx,ID3D11Buffer*source,unsigned bytes,uint64_t frame,std::vector<uint8_t>&out){
  out.clear();if(!ctx||!source||!bytes||bytes>512*1024)return false;
  const uint64_t key=reinterpret_cast<uint64_t>(source);auto it=pending.find(key);
  if(it!=pending.end()){
   if(it->second.bytes!=bytes){erase(key);return false;}
   if(it->second.polled==frame)return false;it->second.polled=frame;
   D3D11_MAPPED_SUBRESOURCE data{};
   const HRESULT hr=ctx->Map(it->second.staging.Get(),0,D3D11_MAP_READ,D3D11_MAP_FLAG_DO_NOT_WAIT,&data);
   if(hr==DXGI_ERROR_WAS_STILL_DRAWING)return false;
   if(FAILED(hr)){erase(key);return false;}
   out.resize(bytes);std::memcpy(out.data(),data.pData,bytes);ctx->Unmap(it->second.staging.Get(),0);erase(key);return true;
  }
  if(submissionFrame!=frame){submissionFrame=frame;submitted=0;}
  if(submitted>=MaxPerFrame||pending.size()>=MaxPending||queuedBytes+bytes>MaxBytes)return false;
  D3D11_BUFFER_DESC original{};source->GetDesc(&original);
  if(original.StructureByteStride||original.ByteWidth<bytes||original.Usage==D3D11_USAGE_DYNAMIC)return false;
  Microsoft::WRL::ComPtr<ID3D11Device> device;source->GetDevice(&device);
  Pending copy;copy.bytes=bytes;copy.polled=frame;
  D3D11_BUFFER_DESC desc{};desc.ByteWidth=bytes;desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
  if(FAILED(device->CreateBuffer(&desc,nullptr,&copy.staging)))return false;
  D3D11_BOX region{0,0,0,bytes,1,1};ctx->CopySubresourceRegion(copy.staging.Get(),0,0,0,0,source,0,&region);
  pending.emplace(key,std::move(copy));queuedBytes+=bytes;++submitted;return false;
 }
};
}
