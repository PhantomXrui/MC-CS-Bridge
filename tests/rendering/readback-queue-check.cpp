#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "../../source/cs2-bridge/native/pawn_dance_readback.h"
#include <iostream>
#include <stdexcept>
#include <chrono>
#include <algorithm>
using Microsoft::WRL::ComPtr;
void require(bool b,const char*m){if(!b)throw std::runtime_error(m);}
int main()try{
 ComPtr<ID3D11Device>d;ComPtr<ID3D11DeviceContext>c;
 require(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&d,nullptr,&c)),"WARP creation");
 std::vector<unsigned>data(131072);for(unsigned i=0;i<data.size();i++)data[i]=i;
 std::vector<ComPtr<ID3D11Buffer>> buffers(80);
 D3D11_BUFFER_DESC desc{};desc.ByteWidth=524288;desc.Usage=D3D11_USAGE_IMMUTABLE;desc.BindFlags=D3D11_BIND_VERTEX_BUFFER;D3D11_SUBRESOURCE_DATA initial{data.data(),0,0};
 for(auto &b:buffers)require(SUCCEEDED(d->CreateBuffer(&desc,&initial,&b)),"source creation");
 pawn_dance_readback::Queue q;std::vector<uint8_t>out;std::vector<bool>done(80);unsigned completed=0;uint64_t frame=1;
 auto start=std::chrono::steady_clock::now();double longest=0;
 while(completed<buffers.size()){
  require(std::chrono::steady_clock::now()-start<std::chrono::seconds(20),"bounded readback stress timeout");
  for(unsigned i=0;i<buffers.size();i++)if(!done[i]){
   auto before=std::chrono::steady_clock::now();bool ready=q.read(c.Get(),buffers[i].Get(),i%2?4096:524288,frame,out);
   longest=(std::max)(longest,std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-before).count());
   if(ready){require(!memcmp(out.data(),data.data(),out.size()),"copy data changed");done[i]=true;++completed;}
   require(q.pending.size()<=q.MaxPending&&q.queuedBytes<=q.MaxBytes&&q.submitted<=q.MaxPerFrame,"queue cap exceeded");
  }
  // Test harness submits its artificial frame; production never calls Flush.
  c->Flush();Sleep(1);++frame;
 }
 q.clear();require(q.pending.empty()&&!q.queuedBytes,"queue cleanup");
 std::cout<<"PASS 80 mixed 4 KiB/512 KiB asynchronous copies; 2 MiB/64 pending/8 per frame bounds; longest call "<<longest<<" ms\n";
 return 0;
}catch(const std::exception&e){std::cerr<<"FAIL "<<e.what()<<'\n';return 1;}
