#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "../source/cs2-bridge/native/pawn_dance_weapon_shader.h"
#include "../source/cs2-bridge/native/pawn_dance_glove_shader.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
using namespace std;
uint64_t hashBytes(const vector<char>&v){uint64_t h=14695981039346656037ull;for(unsigned char c:v){h^=c;h*=1099511628211ull;}return h;}
void blob(ostream&o,const string&name,ID3DBlob*b){o<<"inline constexpr unsigned char "<<name<<"[]={";auto p=static_cast<const unsigned char*>(b->GetBufferPointer());for(size_t i=0;i<b->GetBufferSize();i++){if(i%32==0)o<<'\n';o<<unsigned(p[i])<<',';}o<<"};\n";}
int main(int argc,char**argv)try{
 if(argc!=3)throw runtime_error("usage: build-dance-shader-cache capture-folder output.h");
 ofstream out(argv[2]);if(!out)throw runtime_error("output unavailable");
 out<<"#pragma once\n// Generated offline from captured original VS bytecode. Never compile in a game callback.\nnamespace pawn_dance_cache {\n";
 map<uint64_t,string> bodies,weapons;
 for(auto &file:filesystem::directory_iterator(argv[1])){
  if(file.path().extension()!=".dxbc")continue;
  ifstream in(file.path(),ios::binary);vector<char> bytes((istreambuf_iterator<char>(in)),{});auto hash=hashBytes(bytes);string id=to_string(hash);
  auto assemblyPath=file.path();assemblyPath.replace_extension(".asm.txt");if(!filesystem::exists(assemblyPath)){Microsoft::WRL::ComPtr<ID3DBlob> assembly;if(SUCCEEDED(D3DDisassemble(bytes.data(),bytes.size(),0,nullptr,&assembly))){ofstream a(assemblyPath,ios::binary);a.write(static_cast<const char*>(assembly->GetBufferPointer()),assembly->GetBufferSize());}}
  auto body=pawn_dance_shader::compile(bytes.data(),bytes.size(),pawn_dance_shader::Mode::WorldActors);
  if(body){blob(out,"body"+id,body.bytecode.Get());ostringstream s;s<<"case "<<hash<<"ull:r.bytecode=copy(body"<<id<<");r.mode=pawn_dance_shader::Mode::WorldActors;r.constantBytes=sizeof(pawn_dance_shader::WorldConstants);r.perViewSourceSlot="<<body.perViewSourceSlot<<";r.csgoViewSourceSlot="<<body.csgoViewSourceSlot<<";r.materialSourceSlot="<<body.materialSourceSlot<<";break;\n";bodies[hash]=s.str();}
  auto weapon=pawn_dance_weapon_shader::compile(bytes.data(),bytes.size());
  if(weapon){blob(out,"weapon"+id,weapon.bytecode.Get());blob(out,"owner"+id,weapon.ownerBytecode.Get());ostringstream s;s<<"case "<<hash<<"ull:r.bytecode=copy(weapon"<<id<<");r.ownerBytecode=copy(owner"<<id<<");r.perViewSlot="<<weapon.perViewSlot<<";r.csgoSlot="<<weapon.csgoSlot<<";r.materialSlot="<<weapon.materialSlot<<";r.needsCsgo="<<weapon.needsCsgo<<";r.needsMaterial="<<weapon.needsMaterial<<";break;\n";weapons[hash]=s.str();}
  cout<<file.path().filename().string()<<" body="<<bool(body)<<" attachment="<<bool(weapon);if(!body)cout<<" body-reason="<<body.error;cout<<endl;
 }
 auto glove=pawn_dance_glove_shader::compile();if(!glove)throw runtime_error(glove.error);blob(out,"gloveOwner",glove.bytecode.Get());
 out<<"template<size_t N> Microsoft::WRL::ComPtr<ID3DBlob> copy(const unsigned char (&data)[N]) {Microsoft::WRL::ComPtr<ID3DBlob> b;if(SUCCEEDED(D3DCreateBlob(N,&b)))std::memcpy(b->GetBufferPointer(),data,N);return b;}\n";
 out<<"inline pawn_dance_shader::CompiledPass body(uint64_t hash){pawn_dance_shader::CompiledPass r;switch(hash){\n";for(auto &[h,s]:bodies)out<<s;out<<"}return r;}\n";
 out<<"inline pawn_dance_weapon_shader::Compiled weapon(uint64_t hash){pawn_dance_weapon_shader::Compiled r;switch(hash){\n";for(auto &[h,s]:weapons)out<<s;out<<"}return r;}\n";
 out<<"inline pawn_dance_glove_shader::Compiled glove(){pawn_dance_glove_shader::Compiled r;r.bytecode=copy(gloveOwner);return r;}\n}\n";
 if(!out||bodies.empty()||weapons.empty())throw runtime_error("incomplete cache");cout<<"PASS cached body="<<bodies.size()<<" attachment="<<weapons.size()<<" glove=1\n";return 0;
}catch(const exception&e){cerr<<e.what()<<'\n';return 1;}
